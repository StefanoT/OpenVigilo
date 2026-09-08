using Microsoft.EntityFrameworkCore;
using Vigilo.Core;

namespace Vigilo.Storage;

public sealed class SentReplyDetectionService(
    IDbContextFactory<VigiloDbContext> dbContextFactory,
    IAccountSettingsService accountSettings,
    ILiveReportService liveReportService) : ISentReplyDetectionService
{
    public async Task<int> DetectSentRepliesAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var openItems = await dbContext.TrackedItems
            .Where(x => x.Status == TrackedItemStatus.Open
                        || x.Status == TrackedItemStatus.Snoozed
                        || x.Status == TrackedItemStatus.NeedsReview)
            .ToListAsync(cancellationToken);

        var accounts = await accountSettings.GetAccountsAsync(cancellationToken);
        var changed = 0;
        foreach (var account in accounts)
        {
            var sentFolders = account.SentFoldersToMonitor
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (sentFolders.Count == 0)
            {
                continue;
            }

            var sentMessages = await dbContext.EmailMessages.AsNoTracking()
                .Where(x => x.AccountId == account.Id)
                .ToListAsync(cancellationToken);
            sentMessages = sentMessages
                .Where(x => sentFolders.Contains(x.Folder))
                .ToList();

            foreach (var item in openItems.Where(x => x.SentReplyDetectedAt is null))
            {
                var original = await dbContext.EmailMessages.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == item.EmailMessageId && x.AccountId == account.Id, cancellationToken);
                if (original is null)
                {
                    continue;
                }

                var reply = sentMessages
                    .Where(x => x.ReceivedAt >= original.ReceivedAt)
                    .FirstOrDefault(x =>
                        (!string.IsNullOrWhiteSpace(item.ThreadKey) && x.ThreadKey == item.ThreadKey)
                        || (!string.IsNullOrWhiteSpace(original.MessageIdHeader)
                            && ((x.InReplyTo?.Contains(original.MessageIdHeader, StringComparison.OrdinalIgnoreCase) ?? false)
                                || (x.References?.Contains(original.MessageIdHeader, StringComparison.OrdinalIgnoreCase) ?? false)))
                        || NormalizedSubject(x.Subject) == NormalizedSubject(original.Subject));

                if (reply is null)
                {
                    continue;
                }

                item.SentReplyDetectedAt = reply.ReceivedAt;
                item.SentReplyMessageId = reply.MessageIdHeader ?? reply.ProviderMessageId;
                item.UpdatedAt = DateTimeOffset.UtcNow;
                changed++;
            }
        }

        if (changed > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            liveReportService.NotifyChanged(LiveReportChange.All);
        }

        return changed;
    }

    private static string NormalizedSubject(string subject)
    {
        var value = subject.Trim();
        while (value.StartsWith("RE:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("AW:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("SV:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("FWD:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[(value.IndexOf(':') + 1)..].Trim();
        }

        return value.ToUpperInvariant();
    }
}
