using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Vigilo.Core;

namespace Vigilo.Storage;

public sealed class SenderRuleService(IDbContextFactory<VigiloDbContext> dbContextFactory) : ISenderRuleService
{
    public async Task<SenderRuleKind?> GetRuleKindAsync(
        Guid accountId,
        string senderEmail,
        CancellationToken cancellationToken)
    {
        if (!TryNormalize(senderEmail, out _, out var normalized))
        {
            return null;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.SenderRules.AsNoTracking()
            .Where(rule => rule.AccountId == accountId && rule.NormalizedSenderEmail == normalized)
            .Select(rule => (SenderRuleKind?)rule.Kind)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SenderRuleView>> GetRulesAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await dbContext.Accounts.AsNoTracking().ToListAsync(cancellationToken);
        var accountNames = accounts.ToDictionary(account => account.Id, account => account.DisplayName);
        var rules = await dbContext.SenderRules.AsNoTracking()
            .OrderBy(rule => rule.Kind)
            .ThenBy(rule => rule.SenderEmail)
            .ToListAsync(cancellationToken);

        return rules.Select(rule => new SenderRuleView(
                rule.Id,
                rule.AccountId,
                accountNames.GetValueOrDefault(rule.AccountId, "(account not found)"),
                rule.SenderEmail,
                rule.Kind,
                rule.UpdatedAt))
            .ToList();
    }

    public async Task SetRuleAsync(
        Guid accountId,
        string senderEmail,
        SenderRuleKind kind,
        CancellationToken cancellationToken)
    {
        if (!TryNormalize(senderEmail, out var canonical, out var normalized))
        {
            throw new ArgumentException("A valid sender email address is required.", nameof(senderEmail));
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await dbContext.Accounts.AnyAsync(account => account.Id == accountId, cancellationToken))
        {
            throw new InvalidOperationException("The email account for this sender rule no longer exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var existing = await dbContext.SenderRules.FirstOrDefaultAsync(
            rule => rule.AccountId == accountId && rule.NormalizedSenderEmail == normalized,
            cancellationToken);
        if (existing is null)
        {
            dbContext.SenderRules.Add(new SenderRule
            {
                AccountId = accountId,
                SenderEmail = canonical,
                NormalizedSenderEmail = normalized,
                Kind = kind,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            existing.SenderEmail = canonical;
            existing.Kind = kind;
            existing.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveRuleAsync(Guid ruleId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rule = await dbContext.SenderRules.FirstOrDefaultAsync(candidate => candidate.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return;
        }

        dbContext.SenderRules.Remove(rule);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool TryNormalize(string senderEmail, out string canonical, out string normalized)
    {
        canonical = "";
        normalized = "";
        if (string.IsNullOrWhiteSpace(senderEmail))
        {
            return false;
        }

        try
        {
            canonical = new MailAddress(senderEmail.Trim()).Address;
            normalized = canonical.Trim().ToUpperInvariant();
            return canonical.Length <= 320;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
