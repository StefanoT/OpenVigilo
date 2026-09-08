using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigilo.Core;

namespace Vigilo.Storage;

public sealed class LiveReportService(
    IDbContextFactory<VigiloDbContext> dbContextFactory,
    LiveReportChangeNotifier changeNotifier,
    IOutlookSyncStore? outlookSyncStore = null,
    IOutlookCategoryMapper? outlookCategoryMapper = null,
    ISenderRuleService? senderRuleService = null) : ILiveReportService
{
    public event EventHandler<LiveReportChangedEventArgs>? ReportChanged
    {
        add => changeNotifier.ReportChanged += value;
        remove => changeNotifier.ReportChanged -= value;
    }

    public async Task<LiveReport> GetLiveReportAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await ApplyScheduledTransitionsAsync(dbContext, cancellationToken);
        var effectiveNow = DateTimeOffset.UtcNow;
        var activeCandidates = await dbContext.TrackedItems.AsNoTracking()
            .Where(x => x.Status == TrackedItemStatus.Open
                        || x.Status == TrackedItemStatus.NeedsReview
                        || x.Status == TrackedItemStatus.Snoozed
                        || x.Status == TrackedItemStatus.Expired)
            .ToListAsync(cancellationToken);
        var activeItems = activeCandidates
            .Where(x => x.Status != TrackedItemStatus.Snoozed || x.SnoozedUntil <= effectiveNow)
            .ToList();
        var sectionCounts = activeItems
            .GroupBy(SectionFor)
            .ToDictionary(group => group.Key, group => group.Count());
        var processedMessages = await dbContext.ProcessedEmails.AsNoTracking()
            .Select(x => x.LastProcessedAt)
            .ToListAsync(cancellationToken);

        return new LiveReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            DueTodayCount = sectionCounts.GetValueOrDefault(TrackedItemCategories.DueToday),
            UpcomingCount = sectionCounts.GetValueOrDefault(TrackedItemCategories.Upcoming),
            WaitingReplyCount = sectionCounts.GetValueOrDefault(TrackedItemCategories.WaitingForMyReply),
            // Escalation is a row badge, not a section: the metric counts the flag across all
            // active items, so it intentionally overlaps the deadline and reply sections.
            EscalationCount = activeItems.Count(x => x.IsEscalation),
            NeedsReviewCount = sectionCounts.GetValueOrDefault(TrackedItemCategories.NeedsReview),
            CommercialOfferCount = sectionCounts.GetValueOrDefault(TrackedItemCategories.CommercialOffers),
            ProcessedMessageCount = processedMessages.Count,
            LastMessageProcessedAt = processedMessages.Max()
        };
    }

    public async Task<IReadOnlyList<TrackedItemView>> GetItemsAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await ApplyScheduledTransitionsAsync(dbContext, cancellationToken);
        var itemData = await dbContext.TrackedItems.AsNoTracking()
            .Join(
                dbContext.EmailMessages.AsNoTracking(),
                item => item.EmailMessageId,
                email => email.Id,
                (item, email) => new { Item = item, Email = email })
            .Join(
                dbContext.Accounts.AsNoTracking(),
                data => data.Email.AccountId,
                account => account.Id,
                (data, account) => new { data.Item, data.Email, Account = account })
            .ToListAsync(cancellationToken);

        var items = itemData
            .Select(data => new TrackedItemView(
                data.Item.Id,
                data.Email.Id,
                data.Account.DisplayName,
                SectionFor(data.Item),
                data.Item.Status,
                data.Item.ActionSummary,
                string.IsNullOrWhiteSpace(data.Email.SenderName) ? data.Email.SenderEmail : data.Email.SenderName,
                data.Email.SenderEmail,
                data.Email.Subject,
                data.Item.Deadline,
                data.Item.Reason,
                data.Item.IsEscalation,
                data.Item.RequiresReply,
                data.Item.SentReplyDetectedAt,
                data.Email.ReceivedAt))
            .ToList();

        return items
            .OrderBy(x => SectionOrder(x.Section))
            // Items with a deadline come first, ordered by increasing deadline so expired
            // and imminent deadlines surface before distant ones; undated items follow.
            .ThenBy(x => x.Deadline is null)
            .ThenBy(x => x.Deadline ?? DateTimeOffset.MaxValue)
            .ThenByDescending(x => x.ReceivedAt)
            .ToList();
    }

    public async Task<string?> GetTrackedMessageClipboardTextAsync(Guid trackedItemId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await ApplyScheduledTransitionsAsync(dbContext, cancellationToken);

        var data = await GetTrackedMessageDataAsync(dbContext, trackedItemId, cancellationToken);
        if (data is null)
        {
            return null;
        }

        var (item, email, account, processed) = data;

        var builder = new StringBuilder();
        builder.AppendLine("Tracked message export");
        AppendField(builder, "GeneratedAtUtc", DateTimeOffset.UtcNow);
        builder.AppendLine();

        builder.AppendLine("[Tracked item]");
        AppendField(builder, "Id", item.Id);
        AppendField(builder, "EmailMessageId", item.EmailMessageId);
        AppendField(builder, "Section", SectionFor(item));
        AppendField(builder, "Status", item.Status);
        AppendField(builder, "Deadline", item.Deadline);
        AppendField(builder, "IsEscalation", item.IsEscalation);
        AppendField(builder, "RequiresReply", item.RequiresReply);
        AppendField(builder, "ThreadKey", item.ThreadKey);
        AppendField(builder, "SentReplyDetectedAt", item.SentReplyDetectedAt);
        AppendField(builder, "SentReplyMessageId", item.SentReplyMessageId);
        AppendField(builder, "ActionSummary", item.ActionSummary);
        AppendField(builder, "Reason", item.Reason);
        AppendField(builder, "Tags", item.Tags);
        AppendField(builder, "CreatedAt", item.CreatedAt);
        AppendField(builder, "UpdatedAt", item.UpdatedAt);
        AppendField(builder, "CompletedAt", item.CompletedAt);
        AppendField(builder, "DismissedAt", item.DismissedAt);
        AppendField(builder, "SnoozedUntil", item.SnoozedUntil);
        builder.AppendLine();

        builder.AppendLine("[Email message]");
        AppendField(builder, "Id", email.Id);
        AppendField(builder, "AccountId", email.AccountId);
        AppendField(builder, "Folder", email.Folder);
        AppendField(builder, "ProviderMessageId", email.ProviderMessageId);
        AppendField(builder, "MessageIdHeader", email.MessageIdHeader);
        AppendField(builder, "SenderName", email.SenderName);
        AppendField(builder, "SenderEmail", email.SenderEmail);
        AppendField(builder, "Subject", email.Subject);
        AppendField(builder, "ReceivedAt", email.ReceivedAt);
        AppendField(builder, "Snippet", email.Snippet);
        AppendField(builder, "InReplyTo", email.InReplyTo);
        AppendField(builder, "References", email.References);
        AppendField(builder, "ThreadKey", email.ThreadKey);
        AppendField(builder, "HasAttachments", email.HasAttachments);
        AppendField(builder, "IsRead", email.IsRead);
        AppendField(builder, "LastScannedAt", email.LastScannedAt);
        builder.AppendLine();

        builder.AppendLine("[Account]");
        if (account is null)
        {
            builder.AppendLine("(account record not found)");
        }
        else
        {
            AppendField(builder, "Id", account.Id);
            AppendField(builder, "Name", account.Name);
            AppendField(builder, "DisplayName", account.DisplayName);
            AppendField(builder, "EmailAddress", account.EmailAddress);
            AppendField(builder, "ProviderName", account.ProviderName);
            AppendField(builder, "ImapHost", account.ImapHost);
            AppendField(builder, "ImapPort", account.ImapPort);
            AppendField(builder, "UseSsl", account.UseSsl);
            AppendField(builder, "Username", account.Username);
            AppendField(builder, "FoldersToMonitor", account.FoldersToMonitor);
            AppendField(builder, "SentFoldersToMonitor", account.SentFoldersToMonitor);
            AppendField(builder, "PollingFallbackMinutes", account.PollingFallbackMinutes);
            AppendField(builder, "UseImapIdle", account.UseImapIdle);
            AppendField(builder, "NotificationsEnabled", account.NotificationsEnabled);
            AppendField(builder, "DataRetentionDays", account.DataRetentionDays);
            AppendField(builder, "CreatedAt", account.CreatedAt);
            AppendField(builder, "UpdatedAt", account.UpdatedAt);
        }

        builder.AppendLine();
        builder.AppendLine("[Processing ledger]");
        if (processed is null)
        {
            builder.AppendLine("(processed email record not found)");
        }
        else
        {
            AppendField(builder, "Id", processed.Id);
            AppendField(builder, "AccountId", processed.AccountId);
            AppendField(builder, "Folder", processed.Folder);
            AppendField(builder, "UidValidity", processed.UidValidity);
            AppendField(builder, "ImapUid", processed.ImapUid);
            AppendField(builder, "MessageIdHeader", processed.MessageIdHeader);
            AppendField(builder, "SubjectHash", processed.SubjectHash);
            AppendField(builder, "BodyHash", processed.BodyHash);
            AppendField(builder, "FlagsHash", processed.FlagsHash);
            AppendField(builder, "ReceivedAt", processed.ReceivedAt);
            AppendField(builder, "FirstSeenAt", processed.FirstSeenAt);
            AppendField(builder, "LastSeenAt", processed.LastSeenAt);
            AppendField(builder, "LastProcessedAt", processed.LastProcessedAt);
            AppendField(builder, "ProcessingStatus", processed.ProcessingStatus);
            AppendField(builder, "ClassificationVersion", processed.ClassificationVersion);
            AppendField(builder, "ClassifierModelVersion", processed.ClassifierModelVersion);
            AppendField(builder, "ClassificationHash", processed.ClassificationHash);
            AppendField(builder, "ErrorMessage", processed.ErrorMessage);
            AppendField(builder, "EmailMessageId", processed.EmailMessageId);
            AppendField(builder, "ClassificationAttemptCount", processed.ClassificationAttemptCount);
            AppendField(builder, "NextClassificationAttemptAt", processed.NextClassificationAttemptAt);
            AppendField(builder, "LastClassificationFailureKind", processed.LastClassificationFailureKind);
            AppendField(builder, "LastClassificationRecoveryKind", processed.LastClassificationRecoveryKind);
        }

        builder.AppendLine();
        builder.AppendLine("[Normalized body]");
        builder.AppendLine(string.IsNullOrWhiteSpace(email.NormalizedBody)
            ? "(not stored or empty)"
            : email.NormalizedBody.Trim());

        return builder.ToString().TrimEnd();
    }

    public async Task<TrackedMessageDetail?> GetTrackedMessageDetailAsync(Guid trackedItemId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await ApplyScheduledTransitionsAsync(dbContext, cancellationToken);

        var data = await GetTrackedMessageDataAsync(dbContext, trackedItemId, cancellationToken);
        if (data is null)
        {
            return null;
        }

        var (item, email, account, processed) = data;
        var outlookOperations = await dbContext.OutlookCategorySyncOperations.AsNoTracking()
            .Where(x => x.VigiloMessageId == email.Id && x.Status != OutlookSyncStatus.Superseded)
            .ToListAsync(cancellationToken);
        var outlookOperation = outlookOperations.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
        var outlookStatus = outlookOperation is null
            ? "Outlook: Disabled"
            : outlookOperation.Status == OutlookSyncStatus.Completed
                ? "Outlook: Tagged"
                : $"Outlook: Pending — {outlookOperation.LastErrorMessageSanitized ?? outlookOperation.Status.ToString()}";
        var outlookCategories = outlookOperation is null
            ? ""
            : string.Join(", ", outlookOperation.GetDesiredCategories().OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var senderRule = senderRuleService is null
            ? null
            : await senderRuleService.GetRuleKindAsync(email.AccountId, email.SenderEmail, cancellationToken);
        return new TrackedMessageDetail(
            item.Id,
            email.Id,
            SectionFor(item),
            item.Status,
            item.ActionSummary,
            item.Reason,
            item.Tags,
            item.IsEscalation,
            item.RequiresReply,
            item.Deadline,
            item.SentReplyDetectedAt,
            account?.DisplayName ?? "(account record not found)",
            email.Folder,
            email.SenderName,
            email.SenderEmail,
            email.Subject,
            email.ReceivedAt,
            email.Snippet,
            string.IsNullOrWhiteSpace(email.NormalizedBody) ? "(not stored or empty)" : email.NormalizedBody.Trim(),
            email.HasAttachments,
            email.IsRead,
            email.ThreadKey,
            processed?.ProcessingStatus.ToString() ?? "(processed email record not found)",
            email.OriginalTextBody,
            email.OriginalHtmlBody,
            outlookStatus,
            outlookCategories,
            email.AccountId,
            senderRule);
    }

    public async Task UpdateStatusAsync(Guid trackedItemId, TrackedItemStatus status, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await dbContext.TrackedItems.FirstOrDefaultAsync(x => x.Id == trackedItemId, cancellationToken);
        if (item is null)
        {
            return;
        }

        item.Status = status;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.CompletedAt = status == TrackedItemStatus.Done ? DateTimeOffset.UtcNow : null;
        item.DismissedAt = status == TrackedItemStatus.Dismissed ? DateTimeOffset.UtcNow : null;
        item.SnoozedUntil = status == TrackedItemStatus.Snoozed ? DateTimeOffset.UtcNow.AddDays(1) : null;
        if (status == TrackedItemStatus.Snoozed)
        {
            // A snoozed item must surface in Snoozed (or its deadline section) instead of the
            // user's earlier re-classification; the choice does not survive a snooze.
            item.SectionOverride = null;
        }

        if (status == TrackedItemStatus.Open)
        {
            item.CompletedAt = null;
            item.DismissedAt = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await QueueOutlookCategoryIfEnabledAsync(item, cancellationToken);
        NotifyChanged(LiveReportChange.All);
    }

    public async Task ReclassifyAsync(Guid trackedItemId, string section, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await dbContext.TrackedItems.FirstOrDefaultAsync(x => x.Id == trackedItemId, cancellationToken);
        if (item is null)
        {
            return;
        }

        TrackedItemReclassification.Apply(item, section, DateTimeOffset.Now);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await QueueOutlookCategoryIfEnabledAsync(item, cancellationToken);
        NotifyChanged(LiveReportChange.All);
    }

    public async Task UpdateDeadlineAsync(
        Guid trackedItemId,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await dbContext.TrackedItems.FirstOrDefaultAsync(x => x.Id == trackedItemId, cancellationToken);
        if (item is null)
        {
            return;
        }

        item.Deadline = deadline;
        item.UpdatedAt = DateTimeOffset.UtcNow;

        var today = DateTimeOffset.Now.Date;
        if (item.Status == TrackedItemStatus.Open
            && !TrackedItemCategories.IsCommercialOffer(item.MessageType, deadline)
            && deadline is { } expiredDeadline
            && expiredDeadline.LocalDateTime.Date < today)
        {
            item.Status = TrackedItemStatus.Expired;
        }
        else if (item.Status == TrackedItemStatus.Expired
                 && (deadline is null || deadline.Value.LocalDateTime.Date >= today))
        {
            item.Status = TrackedItemStatus.Open;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await QueueOutlookCategoryIfEnabledAsync(item, cancellationToken);
        NotifyChanged(LiveReportChange.All);
    }

    public async Task SnoozeAsync(Guid trackedItemId, DateTimeOffset snoozedUntil, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await dbContext.TrackedItems.FirstOrDefaultAsync(x => x.Id == trackedItemId, cancellationToken);
        if (item is null)
        {
            return;
        }

        item.Status = TrackedItemStatus.Snoozed;
        item.SnoozedUntil = snoozedUntil;
        item.SectionOverride = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        await QueueOutlookCategoryIfEnabledAsync(item, cancellationToken);
        NotifyChanged(LiveReportChange.All);
    }

    public async Task DeleteAllLocalDataAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.OutlookCategorySyncOperations.RemoveRange(dbContext.OutlookCategorySyncOperations);
        dbContext.OutlookItemBindings.RemoveRange(dbContext.OutlookItemBindings);
        dbContext.NotificationRecords.RemoveRange(dbContext.NotificationRecords);
        dbContext.TrackedItems.RemoveRange(dbContext.TrackedItems);
        dbContext.ProcessedEmails.RemoveRange(dbContext.ProcessedEmails);
        dbContext.EmailMessages.RemoveRange(dbContext.EmailMessages);
        dbContext.LiveReports.RemoveRange(dbContext.LiveReports);
        await dbContext.SaveChangesAsync(cancellationToken);
        NotifyChanged(LiveReportChange.All);
    }

    public void NotifyChanged(LiveReportChange changes) => changeNotifier.NotifyChanged(this, changes);

    private async Task ApplyScheduledTransitionsAsync(
        VigiloDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        var snoozed = (await dbContext.TrackedItems
                .Where(x => x.Status == TrackedItemStatus.Snoozed)
                .ToListAsync(cancellationToken))
            .Where(x => x.SnoozedUntil <= now)
            .ToList();

        foreach (var item in snoozed)
        {
            item.Status = TrackedItemStatus.Open;
            item.SnoozedUntil = null;
            item.UpdatedAt = now;
            changed = true;
        }

        var today = DateTimeOffset.Now.Date;
        var expired = (await dbContext.TrackedItems
                .Where(x => x.Status == TrackedItemStatus.Open && x.Deadline.HasValue)
                .ToListAsync(cancellationToken))
            .Where(x => x.Deadline!.Value.LocalDateTime.Date < today)
            // Commercial offers carry an informational expiry date, not required work;
            // they stay in the Commercial offers section instead of turning Expired.
            .Where(x => !TrackedItemCategories.IsCommercialOffer(x.MessageType, x.Deadline))
            .ToList();

        foreach (var item in expired)
        {
            item.Status = TrackedItemStatus.Expired;
            item.UpdatedAt = now;
            changed = true;
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (outlookSyncStore is not null)
        {
            var allItems = await dbContext.TrackedItems.ToListAsync(cancellationToken);
            foreach (var item in allItems)
            {
                await QueueOutlookCategoryIfEnabledAsync(item, cancellationToken);
            }
        }
    }

    private async Task<bool> QueueOutlookCategoryIfEnabledAsync(TrackedItem item, CancellationToken cancellationToken)
    {
        if (outlookSyncStore is null
            || await outlookSyncStore.GetBindingForMessageAsync(item.EmailMessageId, cancellationToken) is null)
            return false;

        var mapper = outlookCategoryMapper ?? new OutlookCategoryMapper();
        await outlookSyncStore.EnqueueOrSupersedeAsync(
            item.EmailMessageId,
            mapper.Map(item, DateTimeOffset.Now),
            cancellationToken);
        return true;
    }

    private static string SectionFor(TrackedItem item) =>
        TrackedItemCategories.Classify(item, DateTimeOffset.Now);

    private static int SectionOrder(string section) => section switch
    {
        TrackedItemCategories.DueToday => 0,
        TrackedItemCategories.WaitingForMyReply => 1,
        TrackedItemCategories.NeedsReview => 2,
        TrackedItemCategories.Upcoming => 3,
        TrackedItemCategories.CommercialOffers => 4,
        TrackedItemCategories.Snoozed => 5,
        TrackedItemCategories.Dismissed => 6,
        TrackedItemCategories.Done => 7,
        _ => 99
    };

    private static async Task<TrackedMessageData?> GetTrackedMessageDataAsync(
        VigiloDbContext dbContext,
        Guid trackedItemId,
        CancellationToken cancellationToken)
    {
        var item = await dbContext.TrackedItems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == trackedItemId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var email = await dbContext.EmailMessages.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == item.EmailMessageId, cancellationToken);
        if (email is null)
        {
            return null;
        }

        var account = await dbContext.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == email.AccountId, cancellationToken);
        var processedCandidates = await dbContext.ProcessedEmails.AsNoTracking()
            .Where(x => x.EmailMessageId == email.Id)
            .ToListAsync(cancellationToken);
        var processed = processedCandidates
            .OrderByDescending(x => x.LastProcessedAt ?? x.LastSeenAt)
            .FirstOrDefault();

        return new TrackedMessageData(item, email, account, processed);
    }

    private static void AppendField(StringBuilder builder, string name, object? value)
    {
        var formatted = value switch
        {
            null => "(null)",
            DateTimeOffset dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
            _ => value.ToString()
        };

        builder.Append(name);
        builder.Append(": ");
        builder.AppendLine(string.IsNullOrWhiteSpace(formatted) ? "(empty)" : formatted);
    }

    private sealed record TrackedMessageData(
        TrackedItem Item,
        EmailMessage Email,
        EmailAccount? Account,
        ProcessedEmail? Processed);
}
