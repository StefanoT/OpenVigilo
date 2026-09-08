using Microsoft.EntityFrameworkCore;
using Vigilo.Core;

namespace Vigilo.Storage;

public sealed class LocalDataMaintenanceService(
    IDbContextFactory<VigiloDbContext> dbContextFactory,
    IAccountSettingsService accountSettings,
    ILiveReportService liveReportService,
    StorageOptions? storageOptions = null) : ILocalDataMaintenanceService
{
    public async Task<LocalDataMaintenanceResult> ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var accounts = await accountSettings.GetAccountsAsync(cancellationToken);
        if (accounts.Count == 0)
        {
            accounts = [await accountSettings.GetOrCreateDefaultAccountAsync(cancellationToken)];
        }

        var oldMessages = new List<EmailMessage>();
        foreach (var account in accounts)
        {
            var cutoff = now.AddDays(-Math.Max(1, account.DataRetentionDays));
            oldMessages.AddRange((await dbContext.EmailMessages
                    .Where(x => x.AccountId == account.Id)
                    .ToListAsync(cancellationToken))
                .Where(x => x.ReceivedAt < cutoff));
        }

        var trackedItems = await dbContext.TrackedItems.ToListAsync(cancellationToken);
        var reopenedSnoozedItems = trackedItems.Where(x =>
                x.Status == TrackedItemStatus.Snoozed
                && x.SnoozedUntil <= now)
            .ToList();
        foreach (var snoozedItem in reopenedSnoozedItems)
        {
            snoozedItem.Status = TrackedItemStatus.Open;
            snoozedItem.SnoozedUntil = null;
            snoozedItem.UpdatedAt = now;
        }

        var dismissedCutoff = now.AddDays(-7);
        var completedCutoff = now.AddMonths(-1);
        var commercialOfferCutoff = now.AddDays(-7);
        var dismissedMessageIds = trackedItems
            .Where(x => x.Status == TrackedItemStatus.Dismissed)
            .Select(x => x.EmailMessageId)
            .ToHashSet();
        var expiredDismissedMessageIds = (await dbContext.EmailMessages
                .Where(x => dismissedMessageIds.Contains(x.Id))
                .ToListAsync(cancellationToken))
            .Where(message => message.ReceivedAt < dismissedCutoff)
            .Select(message => message.Id);
        var commercialOfferMessageIds = trackedItems
            .Where(x => TrackedItemCategories.IsCommercialOffer(x.MessageType, x.Deadline))
            .Select(x => x.EmailMessageId)
            .ToHashSet();
        var expiredCommercialOfferMessageIds = (await dbContext.EmailMessages
                .Where(x => commercialOfferMessageIds.Contains(x.Id))
                .ToListAsync(cancellationToken))
            .Where(message => message.ReceivedAt <= commercialOfferCutoff)
            .Select(message => message.Id);
        var expiredWorkflowMessageIds = trackedItems
            .Where(x => x.Status == TrackedItemStatus.Done
                        && (x.CompletedAt ?? x.UpdatedAt) <= completedCutoff)
            .Select(x => x.EmailMessageId)
            .Concat(expiredDismissedMessageIds);

        var oldIds = oldMessages.Select(x => x.Id)
            .Concat(expiredWorkflowMessageIds)
            .Concat(expiredCommercialOfferMessageIds)
            .ToHashSet();
        oldMessages = await dbContext.EmailMessages
            .Where(x => oldIds.Contains(x.Id))
            .ToListAsync(cancellationToken);
        var oldTrackedItems = await dbContext.TrackedItems
            .Where(x => oldIds.Contains(x.EmailMessageId))
            .ToListAsync(cancellationToken);
        var oldProcessed = await dbContext.ProcessedEmails
            .Where(x => x.EmailMessageId.HasValue && oldIds.Contains(x.EmailMessageId.Value))
            .ToListAsync(cancellationToken);
        var oldTrackedItemIds = oldTrackedItems.Select(x => x.Id).ToHashSet();
        var oldNotifications = await dbContext.NotificationRecords
            .Where(x => oldTrackedItemIds.Contains(x.TrackedItemId))
            .ToListAsync(cancellationToken);
        var oldOutlookOperations = await dbContext.OutlookCategorySyncOperations
            .Where(x => oldIds.Contains(x.VigiloMessageId)).ToListAsync(cancellationToken);
        var oldOutlookItems = await dbContext.OutlookItemBindings
            .Where(x => oldIds.Contains(x.VigiloMessageId)).ToListAsync(cancellationToken);

        dbContext.NotificationRecords.RemoveRange(oldNotifications);
        dbContext.OutlookCategorySyncOperations.RemoveRange(oldOutlookOperations);
        dbContext.OutlookItemBindings.RemoveRange(oldOutlookItems);
        dbContext.TrackedItems.RemoveRange(oldTrackedItems);
        dbContext.ProcessedEmails.RemoveRange(oldProcessed);
        dbContext.EmailMessages.RemoveRange(oldMessages);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (oldMessages.Count > 0)
        {
            // Derived model responses in the classification response cache may reference
            // the purged messages; dropping the whole file guarantees they never outlive
            // their sources. Live messages lose only cache warmth: their entries repopulate
            // on their next reclassification.
            DeleteClassificationResponseCache();
        }

        if (reopenedSnoozedItems.Count > 0 || oldMessages.Count > 0)
        {
            liveReportService.NotifyChanged(LiveReportChange.All);
        }

        return new LocalDataMaintenanceResult(oldMessages.Count, oldProcessed.Count);
    }

    private void DeleteClassificationResponseCache()
    {
        var cachePath = storageOptions?.ClassificationResponseCachePath;
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            return;
        }

        try
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked cache file must never fail retention; it is rebuilt on next use.
        }
    }
}
