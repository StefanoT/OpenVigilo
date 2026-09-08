using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigilo.Core;

namespace Vigilo.Storage;

public sealed class OutlookSyncStore(
    IDbContextFactory<VigiloDbContext> dbContextFactory,
    IOutlookCategoryMapper mapper) : IOutlookSyncStore
{
    private static readonly OutlookSyncStatus[] ActiveStatuses =
    [
        OutlookSyncStatus.Pending, OutlookSyncStatus.InProgress, OutlookSyncStatus.RetryScheduled,
        OutlookSyncStatus.OutlookNotRunning, OutlookSyncStatus.MailboxNotFound, OutlookSyncStatus.MessageNotFound
    ];

    public async Task EnqueueOrSupersedeAsync(Guid messageId, IReadOnlySet<string> desiredCategories, CancellationToken cancellationToken, bool force = false)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await EnqueueOrSupersedeAsync(dbContext, messageId, desiredCategories, cancellationToken, force);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnqueueOrSupersedeAsync(
        VigiloDbContext dbContext,
        Guid messageId,
        IReadOnlySet<string> desiredCategories,
        CancellationToken cancellationToken,
        bool force)
    {
        var now = DateTimeOffset.UtcNow;
        var normalized = desiredCategories.Where(OutlookManagedCategories.IsDesired)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var json = JsonSerializer.Serialize(normalized);
        var current = await dbContext.OutlookCategorySyncOperations
            .Where(x => x.VigiloMessageId == messageId && ActiveStatuses.Contains(x.Status))
            .ToListAsync(cancellationToken);

        if (current.Count == 1 && string.Equals(current[0].DesiredCategoriesJson, json, StringComparison.Ordinal))
        {
            return;
        }

        if (!force && current.Count == 0)
        {
            var previous = (await dbContext.OutlookCategorySyncOperations.AsNoTracking()
                    .Where(x => x.VigiloMessageId == messageId && x.Status == OutlookSyncStatus.Completed)
                    .ToListAsync(cancellationToken))
                .OrderByDescending(x => x.CompletedAtUtc)
                .FirstOrDefault();
            if (previous is not null && string.Equals(previous.DesiredCategoriesJson, json, StringComparison.Ordinal)) return;
        }

        foreach (var operation in current)
        {
            operation.Status = OutlookSyncStatus.Superseded;
            operation.SupersededAtUtc = now;
            operation.UpdatedAtUtc = now;
        }

        dbContext.OutlookCategorySyncOperations.Add(new OutlookCategorySyncOperation
        {
            VigiloMessageId = messageId,
            DesiredCategoriesJson = json,
            Status = OutlookSyncStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
    }

    public async Task<IReadOnlyList<OutlookCategorySyncOperation>> GetDueAsync(int maximumCount, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await dbContext.OutlookCategorySyncOperations.AsNoTracking()
            .Where(x => (x.Status == OutlookSyncStatus.Pending || x.Status == OutlookSyncStatus.RetryScheduled
                         || x.Status == OutlookSyncStatus.OutlookNotRunning || x.Status == OutlookSyncStatus.MailboxNotFound
                         || x.Status == OutlookSyncStatus.MessageNotFound)
                        && (x.Status == OutlookSyncStatus.OutlookNotRunning || x.AttemptCount < 8))
            .ToListAsync(cancellationToken);
        return candidates.Where(x => x.NextAttemptAtUtc is null || x.NextAttemptAtUtc <= now)
            .OrderBy(x => x.CreatedAtUtc).Take(maximumCount).ToList();
    }

    public async Task<OutlookStoreBinding?> GetEnabledBindingAsync(Guid mailboxId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.OutlookStoreBindings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.VigiloMailboxId == mailboxId && x.IsEnabled, cancellationToken);
    }

    public async Task<OutlookStoreBinding?> GetBindingForMessageAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var mailboxId = await dbContext.EmailMessages.Where(x => x.Id == messageId)
            .Select(x => (Guid?)x.AccountId).FirstOrDefaultAsync(cancellationToken);
        return mailboxId is null
            ? null
            : await dbContext.OutlookStoreBindings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.VigiloMailboxId == mailboxId.Value && x.IsEnabled, cancellationToken);
    }

    public async Task<IReadOnlyList<OutlookStoreBinding>> GetBindingsAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.OutlookStoreBindings.AsNoTracking()
            .OrderBy(x => x.OutlookStoreDisplayName).ToListAsync(cancellationToken);
    }

    public async Task SaveBindingAsync(OutlookStoreBinding binding, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await SaveBindingAsync(dbContext, binding, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal static async Task SaveBindingAsync(
        VigiloDbContext dbContext,
        OutlookStoreBinding binding,
        CancellationToken cancellationToken)
    {
        SettingsValidator.ValidateOutlookBinding(binding);
        var existing = await dbContext.OutlookStoreBindings.FirstOrDefaultAsync(x => x.VigiloMailboxId == binding.VigiloMailboxId, cancellationToken);
        if (existing is null)
        {
            binding.CreatedAtUtc = DateTimeOffset.UtcNow;
            binding.UpdatedAtUtc = binding.CreatedAtUtc;
            dbContext.Add(binding);
        }
        else
        {
            existing.OutlookStoreId = binding.OutlookStoreId;
            existing.OutlookStoreDisplayName = binding.OutlookStoreDisplayName;
            existing.AccountAddress = binding.AccountAddress;
            existing.IsEnabled = binding.IsEnabled;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public async Task<OutlookMessageIdentity?> GetIdentityAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var message = await dbContext.EmailMessages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == messageId, cancellationToken);
        if (message is null) return null;
        var binding = await dbContext.OutlookItemBindings.AsNoTracking().FirstOrDefaultAsync(x => x.VigiloMessageId == messageId, cancellationToken);
        return new OutlookMessageIdentity(message.MessageIdHeader, binding?.OutlookEntryId, binding?.OutlookStoreId,
            message.SenderEmail, message.Subject, message.ReceivedAt);
    }

    public async Task SaveItemBindingAsync(Guid messageId, string? internetMessageId, OutlookItemReference item, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.OutlookItemBindings.FirstOrDefaultAsync(x => x.VigiloMessageId == messageId, cancellationToken);
        if (existing is null)
        {
            existing = new OutlookItemBinding { VigiloMessageId = messageId };
            dbContext.Add(existing);
        }
        existing.InternetMessageId = OutlookMessageId.Normalize(internetMessageId);
        existing.OutlookEntryId = item.EntryId;
        existing.OutlookStoreId = item.StoreId;
        existing.LastKnownFolderEntryId = item.FolderEntryId;
        existing.LastMatchedAtUtc = DateTimeOffset.UtcNow;
        existing.MatchMethod = item.MatchMethod;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task MarkInProgressAsync(Guid operationId, CancellationToken cancellationToken) =>
        UpdateOperationAsync(operationId, operation =>
        {
            operation.Status = OutlookSyncStatus.InProgress;
            operation.AttemptCount++;
            operation.NextAttemptAtUtc = null;
        }, cancellationToken);

    public Task MarkCompletedAsync(Guid operationId, CancellationToken cancellationToken) =>
        UpdateOperationAsync(operationId, operation =>
        {
            operation.Status = OutlookSyncStatus.Completed;
            operation.CompletedAtUtc = DateTimeOffset.UtcNow;
            operation.NextAttemptAtUtc = null;
            operation.LastErrorCode = null;
            operation.LastErrorMessageSanitized = null;
        }, cancellationToken);

    public Task MarkFailedAsync(Guid operationId, OutlookSyncStatus status, string code, string sanitizedMessage,
        DateTimeOffset? nextAttempt, CancellationToken cancellationToken) =>
        UpdateOperationAsync(operationId, operation =>
        {
            operation.Status = status;
            operation.LastErrorCode = code;
            operation.LastErrorMessageSanitized = sanitizedMessage.Length > 500 ? sanitizedMessage[..500] : sanitizedMessage;
            operation.NextAttemptAtUtc = nextAttempt;
        }, cancellationToken);

    public async Task<int> RecoverStaleInProgressAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var stale = (await dbContext.OutlookCategorySyncOperations
            .Where(x => x.Status == OutlookSyncStatus.InProgress).ToListAsync(cancellationToken))
            .Where(x => x.UpdatedAtUtc < olderThan).ToList();
        foreach (var operation in stale)
        {
            operation.Status = OutlookSyncStatus.RetryScheduled;
            operation.NextAttemptAtUtc = DateTimeOffset.UtcNow;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }

    public async Task<OutlookQueueStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var pending = await dbContext.OutlookCategorySyncOperations.CountAsync(x => ActiveStatuses.Contains(x.Status), cancellationToken);
        var completed = await dbContext.OutlookCategorySyncOperations.AsNoTracking()
            .Where(x => x.CompletedAtUtc != null).Select(x => x.CompletedAtUtc).ToListAsync(cancellationToken);
        var lastSuccess = completed.Count == 0 ? null : completed.Max();
        var errors = await dbContext.OutlookCategorySyncOperations.AsNoTracking()
            .Where(x => x.LastErrorMessageSanitized != null).ToListAsync(cancellationToken);
        var error = errors.OrderByDescending(x => x.UpdatedAtUtc).Select(x => x.LastErrorMessageSanitized).FirstOrDefault();
        return new OutlookQueueStatistics(pending, lastSuccess, error);
    }

    public async Task EnqueueAllTrackedMessagesAsync(CancellationToken cancellationToken)
    {
        await EnqueueTrackedMessagesCoreAsync(null, cancellationToken);
    }

    public async Task EnqueueTrackedMessagesAsync(Guid mailboxId, CancellationToken cancellationToken)
    {
        await EnqueueTrackedMessagesCoreAsync(mailboxId, cancellationToken);
    }

    private async Task EnqueueTrackedMessagesCoreAsync(Guid? mailboxId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var trackedQuery = dbContext.TrackedItems.AsNoTracking()
            .Join(dbContext.EmailMessages.AsNoTracking(), item => item.EmailMessageId, message => message.Id,
                (item, message) => new { Item = item, message.AccountId });
        if (mailboxId is not null) trackedQuery = trackedQuery.Where(x => x.AccountId == mailboxId.Value);
        var tracked = await trackedQuery.Select(x => x.Item).ToListAsync(cancellationToken);
        foreach (var item in tracked)
        {
            await EnqueueOrSupersedeAsync(
                dbContext,
                item.EmailMessageId,
                mapper.Map(item, DateTimeOffset.Now),
                cancellationToken,
                force: true);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateOperationAsync(Guid id, Action<OutlookCategorySyncOperation> update, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var operation = await dbContext.OutlookCategorySyncOperations.FirstAsync(x => x.Id == id, cancellationToken);
        update(operation);
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
