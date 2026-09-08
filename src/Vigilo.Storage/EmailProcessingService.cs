using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vigilo.Core;

namespace Vigilo.Storage;

public sealed class EmailProcessingService(
    IDbContextFactory<VigiloDbContext> dbContextFactory,
    IMessageClassifier classifier,
    ILiveReportService liveReportService,
    INotificationService notificationService,
    ILogger<EmailProcessingService> logger,
    IOutlookSyncStore? outlookSyncStore = null,
    IOutlookCategoryMapper? outlookCategoryMapper = null,
    ISenderRuleService? senderRuleService = null,
    IClassificationVersionSource? classificationVersionSource = null) : IEmailProcessingService
{
    // Cached model responses make retries cheap and the processing ledger makes them
    // idempotent, so leave headroom for transient local-server failures (e.g. the startup
    // race) instead of letting one hiccup strand an email as permanently Failed.
    private const int MaximumAutomaticClassificationAttempts = 5;

    // The live source composes the harness version with the content-derived prompt version,
    // so prompt edits make stored messages reclassification-eligible automatically. The
    // static metadata remains only as the fallback for composition roots without a source.
    private string CurrentClassificationVersion =>
        classificationVersionSource?.ClassificationVersion ?? ClassificationMetadata.CurrentVersion;

    private string CurrentPromptVersion =>
        classificationVersionSource?.PromptVersion ?? ClassificationMetadata.CurrentPromptVersion;

    public async Task<long> GetHighestKnownUidAsync(
        Guid accountId,
        string folder,
        long uidValidity,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ProcessedEmails
            .Where(x => x.AccountId == accountId && x.Folder == folder && x.UidValidity == uidValidity)
            .Select(x => (long?)x.ImapUid)
            .MaxAsync(cancellationToken) ?? 0;
    }

    public async Task<long?> GetKnownUidValidityAsync(Guid accountId, string folder, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entries = await dbContext.ProcessedEmails
            .Where(x => x.AccountId == accountId && x.Folder == folder)
            .ToListAsync(cancellationToken);

        return entries
            .OrderByDescending(x => x.LastSeenAt)
            .Select(x => (long?)x.UidValidity)
            .FirstOrDefault();
    }

    public async Task<int> RetryDeferredClassificationsAsync(
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await SurfaceExhaustedDeferralsAsync(dbContext, cancellationToken);
        var failedCandidates = await dbContext.ProcessedEmails
            .Where(processed => processed.ProcessingStatus == ProcessingStatus.Failed
                                && processed.ClassificationAttemptCount < MaximumAutomaticClassificationAttempts
                                && processed.EmailMessageId != null)
            .ToListAsync(cancellationToken);
        var due = failedCandidates
            .Where(processed => processed.NextClassificationAttemptAt is null
                                || processed.NextClassificationAttemptAt <= now)
            .OrderBy(processed => processed.NextClassificationAttemptAt)
            .ToList();
        var attempted = 0;

        foreach (var processed in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = await dbContext.EmailMessages
                .FirstOrDefaultAsync(candidate => candidate.Id == processed.EmailMessageId, cancellationToken);
            if (message is null)
            {
                logger.LogWarning(
                    "Deferred classification could not be retried because its stored message is missing. ProcessedEmailId={ProcessedEmailId} EmailMessageId={EmailMessageId}",
                    processed.Id,
                    processed.EmailMessageId);
                processed.ClassificationAttemptCount = MaximumAutomaticClassificationAttempts;
                processed.NextClassificationAttemptAt = null;
                processed.LastClassificationFailureKind = "MissingStoredMessage";
                processed.ErrorMessage = "Stored email content is unavailable for deferred classification.";
                continue;
            }

            attempted++;
            progress?.Report($"Retrying deferred classification {attempted}/{due.Count}");
            logger.LogInformation(
                "Retrying deferred classification. ProcessedEmailId={ProcessedEmailId} MessageId={MessageId} AttemptCount={AttemptCount} FailureKind={FailureKind}",
                processed.Id,
                message.Id,
                processed.ClassificationAttemptCount,
                processed.LastClassificationFailureKind);
            processed.ProcessingStatus = ProcessingStatus.NeedsReclassification;
            await ClassifyAndTrackAsync(dbContext, processed, message, cancellationToken, progress);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return attempted;
    }

    public async Task MarkFolderUidValidityChangedAsync(
        Guid accountId,
        string folder,
        long newUidValidity,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var previous = await dbContext.ProcessedEmails
            .Where(x => x.AccountId == accountId && x.Folder == folder && x.UidValidity != newUidValidity)
            .ToListAsync(cancellationToken);

        foreach (var item in previous)
        {
            item.ProcessingStatus = ProcessingStatus.DeletedFromMailbox;
            item.LastSeenAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<EmailProcessingOutcome> ProcessFetchedEmailAsync(
        FetchedEmail fetchedEmail,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;
        logger.LogInformation(
            "Processing fetched email started. AccountId={AccountId} Folder={Folder} UidValidity={UidValidity} ImapUid={ImapUid} ProviderMessageId={ProviderMessageId} MessageId={MessageId} SubjectHash={SubjectHash} BodyHash={BodyHash}",
            fetchedEmail.AccountId,
            fetchedEmail.Folder,
            fetchedEmail.UidValidity,
            fetchedEmail.ImapUid,
            fetchedEmail.Message.ProviderMessageId,
            fetchedEmail.Message.Id,
            ShortHash(fetchedEmail.SubjectHash),
            ShortHash(fetchedEmail.BodyHash));

        var processed = await dbContext.ProcessedEmails
            .FirstOrDefaultAsync(
                x => x.AccountId == fetchedEmail.AccountId
                     && x.Folder == fetchedEmail.Folder
                     && x.UidValidity == fetchedEmail.UidValidity
                     && x.ImapUid == fetchedEmail.ImapUid,
                cancellationToken);

        var senderRule = await GetSenderRuleKindAsync(fetchedEmail.Message, cancellationToken);
        if (senderRule == SenderRuleKind.Ignored)
        {
            var skipped = await SkipIgnoredEmailAsync(dbContext, fetchedEmail, processed, now, cancellationToken);
            liveReportService.NotifyChanged(LiveReportChange.Summary);
            LogProcessingCompleted(fetchedEmail, processed ?? skipped, EmailProcessingOutcome.Skipped, stopwatch);
            return EmailProcessingOutcome.Skipped;
        }

        if (processed is not null)
        {
            logger.LogDebug(
                "Existing processing ledger entry found. ProcessedEmailId={ProcessedEmailId} Status={ProcessingStatus} LastProcessedAt={LastProcessedAt}",
                processed.Id,
                processed.ProcessingStatus,
                processed.LastProcessedAt);
            processed.LastSeenAt = now;
            var classifierChanged = processed.ClassificationVersion != CurrentClassificationVersion;
            var classificationInputsChanged = processed.BodyHash != fetchedEmail.BodyHash
                                              || processed.SubjectHash != fetchedEmail.SubjectHash
                                              || classifierChanged;
            var deferredRetryDue = IsDeferredRetryDue(processed, now);
            if (classificationInputsChanged || deferredRetryDue)
            {
                logger.LogInformation(
                    "Email requires reclassification. ProcessedEmailId={ProcessedEmailId} BodyChanged={BodyChanged} SubjectChanged={SubjectChanged} ClassifierChanged={ClassifierChanged} DeferredRetryDue={DeferredRetryDue} AttemptCount={AttemptCount} PreviousClassificationVersion={PreviousClassificationVersion} CurrentClassificationVersion={CurrentClassificationVersion}",
                    processed.Id,
                    processed.BodyHash != fetchedEmail.BodyHash,
                    processed.SubjectHash != fetchedEmail.SubjectHash,
                    classifierChanged,
                    deferredRetryDue,
                    processed.ClassificationAttemptCount,
                    processed.ClassificationVersion,
                    CurrentClassificationVersion);
                if (classificationInputsChanged)
                {
                    ResetClassificationRecovery(processed);
                }

                processed.ProcessingStatus = ProcessingStatus.NeedsReclassification;
                processed.BodyHash = fetchedEmail.BodyHash;
                processed.SubjectHash = fetchedEmail.SubjectHash;
                processed.ClassificationVersion = CurrentClassificationVersion;
                await UpsertMessageAsync(dbContext, fetchedEmail.Message, cancellationToken);
                var reclassified = await ClassifyAndTrackAsync(dbContext, processed, fetchedEmail.Message, cancellationToken, progress);
                LogProcessingCompleted(fetchedEmail, processed, reclassified, stopwatch);
                return reclassified;
            }

            if (processed.FlagsHash != fetchedEmail.FlagsHash)
            {
                logger.LogInformation(
                    "Email flags changed. ProcessedEmailId={ProcessedEmailId} PreviousFlagsHash={PreviousFlagsHash} NewFlagsHash={NewFlagsHash}",
                    processed.Id,
                    ShortHash(processed.FlagsHash),
                    ShortHash(fetchedEmail.FlagsHash));
                processed.FlagsHash = fetchedEmail.FlagsHash;
                await dbContext.SaveChangesAsync(cancellationToken);
                LogProcessingCompleted(fetchedEmail, processed, EmailProcessingOutcome.FlagsOnlyUpdated, stopwatch);
                return EmailProcessingOutcome.FlagsOnlyUpdated;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            LogProcessingCompleted(fetchedEmail, processed, EmailProcessingOutcome.Unchanged, stopwatch);
            return EmailProcessingOutcome.Unchanged;
        }

        logger.LogInformation(
            "Creating processing ledger entry for new email. AccountId={AccountId} Folder={Folder} UidValidity={UidValidity} ImapUid={ImapUid}",
            fetchedEmail.AccountId,
            fetchedEmail.Folder,
            fetchedEmail.UidValidity,
            fetchedEmail.ImapUid);
        await UpsertMessageAsync(dbContext, fetchedEmail.Message, cancellationToken);
        processed = new ProcessedEmail
        {
            AccountId = fetchedEmail.AccountId,
            Folder = fetchedEmail.Folder,
            UidValidity = fetchedEmail.UidValidity,
            ImapUid = fetchedEmail.ImapUid,
            MessageIdHeader = fetchedEmail.Message.MessageIdHeader,
            SubjectHash = fetchedEmail.SubjectHash,
            BodyHash = fetchedEmail.BodyHash,
            FlagsHash = fetchedEmail.FlagsHash,
            ReceivedAt = fetchedEmail.Message.ReceivedAt,
            FirstSeenAt = now,
            LastSeenAt = now,
            ProcessingStatus = ProcessingStatus.Fetched,
            ClassificationVersion = CurrentClassificationVersion,
            EmailMessageId = fetchedEmail.Message.Id
        };

        dbContext.ProcessedEmails.Add(processed);
        await dbContext.SaveChangesAsync(cancellationToken);
        var outcome = await ClassifyAndTrackAsync(dbContext, processed, fetchedEmail.Message, cancellationToken, progress);
        LogProcessingCompleted(fetchedEmail, processed, outcome, stopwatch);
        return outcome;
    }

    private async Task UpsertMessageAsync(
        VigiloDbContext dbContext,
        EmailMessage message,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.EmailMessages
            .FirstOrDefaultAsync(x => x.AccountId == message.AccountId && x.ProviderMessageId == message.ProviderMessageId, cancellationToken);

        if (existing is null)
        {
            dbContext.EmailMessages.Add(message);
            logger.LogDebug(
                "Adding email message row. MessageId={MessageId} AccountId={AccountId} ProviderMessageId={ProviderMessageId}",
                message.Id,
                message.AccountId,
                message.ProviderMessageId);
            return;
        }

        logger.LogDebug(
            "Updating email message row. MessageId={MessageId} AccountId={AccountId} ProviderMessageId={ProviderMessageId}",
            existing.Id,
            message.AccountId,
            message.ProviderMessageId);
        existing.MessageIdHeader = message.MessageIdHeader;
        existing.SenderName = message.SenderName;
        existing.SenderEmail = message.SenderEmail;
        existing.Subject = message.Subject;
        existing.ReceivedAt = message.ReceivedAt;
        existing.Snippet = message.Snippet;
        existing.NormalizedBody = message.NormalizedBody;
        existing.OriginalTextBody = message.OriginalTextBody;
        existing.OriginalHtmlBody = message.OriginalHtmlBody;
        existing.InReplyTo = message.InReplyTo;
        existing.References = message.References;
        existing.ThreadKey = message.ThreadKey;
        existing.HasAttachments = message.HasAttachments;
        existing.IsRead = message.IsRead;
        existing.LastScannedAt = message.LastScannedAt;
        message.Id = existing.Id;
    }

    private async Task<EmailProcessingOutcome> ClassifyAndTrackAsync(
        VigiloDbContext dbContext,
        ProcessedEmail processed,
        EmailMessage message,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var senderRule = await GetSenderRuleKindAsync(message, cancellationToken);
            if (senderRule == SenderRuleKind.Ignored)
            {
                processed.ProcessingStatus = ProcessingStatus.Skipped;
                processed.LastProcessedAt = DateTimeOffset.UtcNow;
                ResetClassificationRecovery(processed);
                await dbContext.SaveChangesAsync(cancellationToken);
                liveReportService.NotifyChanged(LiveReportChange.Summary);
                return EmailProcessingOutcome.Skipped;
            }

            var isVip = senderRule == SenderRuleKind.Vip;
            logger.LogInformation(
                "Classification started. ProcessedEmailId={ProcessedEmailId} MessageId={MessageId} AccountId={AccountId} ProviderMessageId={ProviderMessageId} ClassificationVersion={ClassificationVersion} PromptVersion={PromptVersion}",
                processed.Id,
                message.Id,
                message.AccountId,
                message.ProviderMessageId,
                CurrentClassificationVersion,
                CurrentPromptVersion);
            progress?.Report("Classifying email");
            var result = await classifier.ClassifyAsync(message, cancellationToken, progress);
            processed.LastProcessedAt = DateTimeOffset.UtcNow;
            if (result.ClassificationUnavailable)
            {
                processed.ProcessingStatus = ProcessingStatus.Failed;
                RecordClassificationFailure(
                    processed,
                    result.FailureKind == ClassificationFailureKind.None
                        ? ClassificationFailureKind.ModelUnavailable.ToString()
                        : result.FailureKind.ToString(),
                    result.Reason,
                    processed.LastProcessedAt.Value);
                if (processed.ClassificationAttemptCount >= MaximumAutomaticClassificationAttempts)
                {
                    // Every automatic attempt has failed: model outage, unusable output, or an
                    // actionable claim whose evidence never resolved. Leaving the ledger Failed
                    // idles silently forever; surface the email as explicit manual review
                    // instead. The stale stored classification version still makes any future
                    // harness or prompt change reclassification-eligible, so an improved
                    // classifier retries it naturally.
                    processed.ProcessingStatus = ProcessingStatus.Classified;
                    processed.NextClassificationAttemptAt = null;
                    result = ClassificationResult.NeedsReviewFallback(ExhaustedAttemptsReason(processed));
                    logger.LogWarning(
                        "Deferred classification exhausted its attempts and was surfaced for review. ProcessedEmailId={ProcessedEmailId} MessageId={MessageId} AttemptCount={AttemptCount} LastFailureKind={LastFailureKind}",
                        processed.Id,
                        message.Id,
                        processed.ClassificationAttemptCount,
                        processed.LastClassificationFailureKind);
                }
            }
            else
            {
                processed.ProcessingStatus = ProcessingStatus.Classified;
                processed.ErrorMessage = result.NeedsReview ? result.Reason : null;
                processed.ClassificationAttemptCount = 0;
                processed.NextClassificationAttemptAt = null;
                processed.LastClassificationFailureKind = "";
                processed.LastClassificationRecoveryKind = result.RecoveryKind.ToString();
            }

            processed.ClassificationHash = Hash(JsonSerializer.Serialize(result));
            var trackingResult = isVip ? ApplyVipRule(result, message) : result;

            var isCommercialOffer = !isVip && IsCommercialOffer(result);
            var isActionable = isVip || (!isCommercialOffer && IsActionable(result));
            TrackedItem? displayedItem = null;
            var trackedItemsChanged = false;
            // A classifier outage is not semantic uncertainty in the email. It must never create
            // a user-visible work item, including for VIP senders; the ledger owns recovery.
            if (!result.ClassificationUnavailable && (isVip || isActionable || result.NeedsReview || isCommercialOffer))
            {
                progress?.Report("Updating tracked item");
                var item = await GetOrCreateTrackedItemAsync(dbContext, message, trackingResult, cancellationToken, isVip);
                displayedItem = item;
                trackedItemsChanged = true;
                if (!result.ClassificationUnavailable && (isActionable || result.NeedsReview))
                {
                    // Notifications use their own short-lived context and require the tracked item to exist first.
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await NotifyIfNeededAsync(item, message, trackingResult, cancellationToken);
                }
            }
            else if (!result.ClassificationUnavailable)
            {
                trackedItemsChanged = await DismissNonActionableTrackedItemAsync(dbContext, message, result, cancellationToken);
            }

            // Outlook never sees raw model output. Only the final normalized, deterministic projection is queued.
            if (!result.ClassificationUnavailable && outlookSyncStore is not null
                && await outlookSyncStore.GetEnabledBindingAsync(message.AccountId, cancellationToken) is not null)
            {
                displayedItem ??= await dbContext.TrackedItems
                    .FirstOrDefaultAsync(item => item.EmailMessageId == message.Id, cancellationToken);
                var mapper = outlookCategoryMapper ?? new OutlookCategoryMapper();
                var categories = displayedItem is not null && displayedItem.EmailMessageId == message.Id
                    ? mapper.Map(displayedItem, DateTimeOffset.Now)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await outlookSyncStore.EnqueueOrSupersedeAsync(message.Id, categories, cancellationToken);
                if (displayedItem is not null && displayedItem.EmailMessageId != message.Id)
                {
                    await outlookSyncStore.EnqueueOrSupersedeAsync(
                        displayedItem.EmailMessageId,
                        mapper.Map(displayedItem, DateTimeOffset.Now),
                        cancellationToken);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            liveReportService.NotifyChanged(
                trackedItemsChanged ? LiveReportChange.All : LiveReportChange.Summary);
            if (result.ClassificationUnavailable)
            {
                logger.LogWarning(
                    "Classification unavailable. ProcessedEmailId={ProcessedEmailId} MessageId={MessageId} Reason={Reason} ElapsedMilliseconds={ElapsedMilliseconds}",
                    processed.Id,
                    message.Id,
                    result.Reason,
                    stopwatch.ElapsedMilliseconds);
                return EmailProcessingOutcome.Failed;
            }

            var outcome = trackingResult.NeedsReview
                ? EmailProcessingOutcome.NeedsReview
                : EmailProcessingOutcome.Classified;
            logger.LogInformation(
                "Classification completed. ProcessedEmailId={ProcessedEmailId} MessageId={MessageId} Outcome={Outcome} Actionable={Actionable} NeedsReview={NeedsReview} RecoveryKind={RecoveryKind} Deadline={Deadline} Escalation={Escalation} RequiresReply={RequiresReply} TagCount={TagCount} ElapsedMilliseconds={ElapsedMilliseconds}",
                processed.Id,
                message.Id,
                outcome,
                isActionable,
                trackingResult.NeedsReview,
                result.RecoveryKind,
                result.UserActionDeadline,
                result.UserObligationMayEscalate,
                result.UserReplyRequired,
                result.Tags.Count,
                stopwatch.ElapsedMilliseconds);
            return outcome;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Classification failed. ProcessedEmailId={ProcessedEmailId} MessageId={MessageId} AccountId={AccountId} ProviderMessageId={ProviderMessageId} ElapsedMilliseconds={ElapsedMilliseconds}",
                processed.Id,
                message.Id,
                message.AccountId,
                message.ProviderMessageId,
                stopwatch.ElapsedMilliseconds);
            processed.ProcessingStatus = ProcessingStatus.Failed;
            processed.LastProcessedAt = DateTimeOffset.UtcNow;
            RecordClassificationFailure(
                processed,
                "UnhandledException",
                ex.Message,
                processed.LastProcessedAt.Value);
            await dbContext.SaveChangesAsync(cancellationToken);
            liveReportService.NotifyChanged(LiveReportChange.Summary);
            return EmailProcessingOutcome.Failed;
        }
    }

    private static bool IsActionable(ClassificationResult result) =>
        result.IsActionable
        || result.UserReplyRequired
        || result.UserObligationMayEscalate
        || result.UserActionDeadline is not null;

    private static ClassificationResult ApplyVipRule(ClassificationResult result, EmailMessage message)
    {
        var routeToNeedsReview = !result.ClassificationUnavailable && IsCommercialOffer(result);
        var summary = string.IsNullOrWhiteSpace(result.ActionSummary)
            ? $"Review message from VIP sender: {message.Subject}"
            : result.ActionSummary;
        var reason = routeToNeedsReview
            ? string.IsNullOrWhiteSpace(result.Reason)
                ? "VIP sender rule: commercial offers from this sender require review."
                : $"VIP sender rule: commercial offers from this sender require review. {result.Reason}"
            : result.ClassificationUnavailable
            ? $"VIP sender rule: this message is always tracked. Classification is temporarily unavailable: {result.Reason}"
            : string.IsNullOrWhiteSpace(result.Reason)
                ? "VIP sender rule: this message is always tracked."
                : $"VIP sender rule: this message is always tracked. {result.Reason}";

        return result with
        {
            IsActionable = true,
            NeedsReview = result.NeedsReview || result.ClassificationUnavailable || routeToNeedsReview,
            ActionSummary = summary,
            Reason = reason
        };
    }

    private static bool IsCommercialOffer(ClassificationResult result) =>
        !result.HasUserSpecificObligation
        && TrackedItemCategories.IsCommercialOffer(result.MessageType, result.UserActionDeadline);

    private static bool IsDeferredRetryDue(ProcessedEmail processed, DateTimeOffset now) =>
        processed.ProcessingStatus == ProcessingStatus.Failed
        && processed.ClassificationAttemptCount < MaximumAutomaticClassificationAttempts
        && (processed.NextClassificationAttemptAt is null || processed.NextClassificationAttemptAt <= now);

    private static string ExhaustedAttemptsReason(ProcessedEmail processed) =>
        $"Automatic classification failed after {processed.ClassificationAttemptCount} attempts"
        + (string.IsNullOrWhiteSpace(processed.LastClassificationFailureKind)
            ? "."
            : $" ({processed.LastClassificationFailureKind}).")
        + " Review this email manually.";

    /// <summary>
    /// Emails whose deferral already exhausted every automatic attempt (including rows
    /// recorded before the conversion existed) are moved out of the silent Failed state:
    /// they become Needs review items so the user sees them, while the stale stored
    /// classification version keeps future classifier changes reclassification-eligible.
    /// </summary>
    private async Task SurfaceExhaustedDeferralsAsync(
        VigiloDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var exhausted = await dbContext.ProcessedEmails
            .Where(processed => processed.ProcessingStatus == ProcessingStatus.Failed
                                && processed.ClassificationAttemptCount >= MaximumAutomaticClassificationAttempts
                                && processed.EmailMessageId != null)
            .ToListAsync(cancellationToken);
        foreach (var processed in exhausted)
        {
            var message = await dbContext.EmailMessages
                .FirstOrDefaultAsync(candidate => candidate.Id == processed.EmailMessageId, cancellationToken);
            if (message is null)
            {
                continue;
            }

            await GetOrCreateTrackedItemAsync(
                dbContext,
                message,
                ClassificationResult.NeedsReviewFallback(ExhaustedAttemptsReason(processed)),
                cancellationToken);
            processed.ProcessingStatus = ProcessingStatus.Classified;
            processed.NextClassificationAttemptAt = null;
            logger.LogWarning(
                "Deferred classification had exhausted its attempts and was surfaced for review. ProcessedEmailId={ProcessedEmailId} MessageId={MessageId} AttemptCount={AttemptCount} LastFailureKind={LastFailureKind}",
                processed.Id,
                message.Id,
                processed.ClassificationAttemptCount,
                processed.LastClassificationFailureKind);
        }

        if (exhausted.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            liveReportService.NotifyChanged(LiveReportChange.All);
        }
    }

    private static void RecordClassificationFailure(
        ProcessedEmail processed,
        string failureKind,
        string reason,
        DateTimeOffset failedAt)
    {
        processed.ProcessingStatus = ProcessingStatus.Failed;
        processed.ErrorMessage = reason;
        processed.ClassificationAttemptCount++;
        processed.LastClassificationFailureKind = failureKind;
        processed.LastClassificationRecoveryKind = "";
        processed.NextClassificationAttemptAt = processed.ClassificationAttemptCount switch
        {
            1 => failedAt.AddMinutes(5),
            2 => failedAt.AddMinutes(30),
            3 => failedAt.AddHours(2),
            _ => null
        };
    }

    private static void ResetClassificationRecovery(ProcessedEmail processed)
    {
        processed.ClassificationAttemptCount = 0;
        processed.NextClassificationAttemptAt = null;
        processed.LastClassificationFailureKind = "";
        processed.LastClassificationRecoveryKind = "";
        processed.ErrorMessage = null;
    }

    private async Task<TrackedItem> GetOrCreateTrackedItemAsync(
        VigiloDbContext dbContext,
        EmailMessage message,
        ClassificationResult result,
        CancellationToken cancellationToken,
        bool forceOpen = false)
    {
        var isCommercialOffer = !forceOpen && IsCommercialOffer(result);
        var duplicate = await FindDuplicateVisibleItemAsync(dbContext, message, cancellationToken);
        if (duplicate is not null)
        {
            logger.LogInformation(
                "Updating existing tracked item for classified email. TrackedItemId={TrackedItemId} MessageId={MessageId} Status={Status}",
                duplicate.Id,
                message.Id,
                duplicate.Status);
            duplicate.UpdatedAt = DateTimeOffset.UtcNow;
            duplicate.MessageType = result.MessageType;
            if (forceOpen && IsCommercialOffer(result))
            {
                duplicate.Status = TrackedItemStatus.NeedsReview;
                duplicate.SnoozedUntil = null;
            }

            // Offer expiry is useful to the user, so commercial offers keep their deadline;
            // only work-related flags are suppressed for them.
            duplicate.Deadline = result.UserActionDeadline ?? duplicate.Deadline;
            if (isCommercialOffer)
            {
                duplicate.IsEscalation = false;
                duplicate.RequiresReply = false;
            }
            else
            {
                duplicate.IsEscalation |= result.UserObligationMayEscalate;
                duplicate.RequiresReply |= result.UserReplyRequired;
            }
            duplicate.ThreadKey = string.IsNullOrWhiteSpace(duplicate.ThreadKey) ? message.ThreadKey : duplicate.ThreadKey;
            duplicate.ActionSummary = string.IsNullOrWhiteSpace(result.ActionSummary)
                ? IsCommercialOffer(result) ? message.Subject : duplicate.ActionSummary
                : result.ActionSummary;
            duplicate.Reason = string.IsNullOrWhiteSpace(result.Reason) ? duplicate.Reason : result.Reason;
            return duplicate;
        }

        var item = new TrackedItem
        {
            EmailMessageId = message.Id,
            Status = result.NeedsReview ? TrackedItemStatus.NeedsReview : TrackedItemStatus.Open,
            Deadline = result.UserActionDeadline,
            IsEscalation = !isCommercialOffer && result.UserObligationMayEscalate,
            RequiresReply = !isCommercialOffer && result.UserReplyRequired,
            MessageType = result.MessageType,
            ThreadKey = message.ThreadKey,
            ActionSummary = string.IsNullOrWhiteSpace(result.ActionSummary)
                ? IsCommercialOffer(result) ? message.Subject : "Review this email"
                : result.ActionSummary,
            Reason = result.Reason,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Tags = string.Join(';', result.Tags)
        };

        dbContext.TrackedItems.Add(item);
        logger.LogInformation(
            "Creating tracked item for classified email. TrackedItemId={TrackedItemId} MessageId={MessageId} Status={Status} Deadline={Deadline} Escalation={Escalation} RequiresReply={RequiresReply}",
            item.Id,
            message.Id,
            item.Status,
            item.Deadline,
            item.IsEscalation,
            item.RequiresReply);
        return item;
    }

    private async Task<SenderRuleKind?> GetSenderRuleKindAsync(
        EmailMessage message,
        CancellationToken cancellationToken) =>
        senderRuleService is null
            ? null
            : await senderRuleService.GetRuleKindAsync(message.AccountId, message.SenderEmail, cancellationToken);

    private async Task<ProcessedEmail> SkipIgnoredEmailAsync(
        VigiloDbContext dbContext,
        FetchedEmail fetchedEmail,
        ProcessedEmail? processed,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        processed ??= new ProcessedEmail
        {
            AccountId = fetchedEmail.AccountId,
            Folder = fetchedEmail.Folder,
            UidValidity = fetchedEmail.UidValidity,
            ImapUid = fetchedEmail.ImapUid,
            FirstSeenAt = now
        };

        if (dbContext.Entry(processed).State == EntityState.Detached)
        {
            dbContext.ProcessedEmails.Add(processed);
        }

        processed.MessageIdHeader = fetchedEmail.Message.MessageIdHeader;
        processed.SubjectHash = fetchedEmail.SubjectHash;
        processed.BodyHash = fetchedEmail.BodyHash;
        processed.FlagsHash = fetchedEmail.FlagsHash;
        processed.ReceivedAt = fetchedEmail.Message.ReceivedAt;
        processed.LastSeenAt = now;
        processed.LastProcessedAt = now;
        processed.ProcessingStatus = ProcessingStatus.Skipped;
        processed.ClassificationVersion = CurrentClassificationVersion;
        processed.ClassificationHash = null;
        processed.EmailMessageId = null;
        ResetClassificationRecovery(processed);

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Ignored sender skipped before storage and classification. AccountId={AccountId} Folder={Folder} ImapUid={ImapUid}",
            fetchedEmail.AccountId,
            fetchedEmail.Folder,
            fetchedEmail.ImapUid);
        return processed;
    }

    private async Task<bool> DismissNonActionableTrackedItemAsync(
        VigiloDbContext dbContext,
        EmailMessage message,
        ClassificationResult result,
        CancellationToken cancellationToken)
    {
        var item = await FindDuplicateVisibleItemAsync(dbContext, message, cancellationToken);
        if (item is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        item.Status = TrackedItemStatus.Dismissed;
        item.DismissedAt = now;
        item.UpdatedAt = now;
        item.Deadline = null;
        item.IsEscalation = false;
        item.RequiresReply = false;
        item.ActionSummary = string.IsNullOrWhiteSpace(result.ActionSummary)
            ? item.ActionSummary
            : result.ActionSummary;
        item.Reason = string.IsNullOrWhiteSpace(result.Reason)
            ? "Classified as non-actionable."
            : result.Reason;
        item.Tags = string.Join(';', result.Tags);

        logger.LogInformation(
            "Dismissing non-actionable tracked item after classification. TrackedItemId={TrackedItemId} MessageId={MessageId}",
            item.Id,
            message.Id);
        return true;
    }

    private async Task<TrackedItem?> FindDuplicateVisibleItemAsync(
        VigiloDbContext dbContext,
        EmailMessage message,
        CancellationToken cancellationToken)
    {
        var sameMessageId = string.IsNullOrWhiteSpace(message.MessageIdHeader)
            ? null
            : await dbContext.TrackedItems
                .Join(dbContext.EmailMessages, item => item.EmailMessageId, email => email.Id, (item, email) => new { item, email })
                .Where(x => x.email.MessageIdHeader == message.MessageIdHeader
                            && x.item.Status != TrackedItemStatus.Done
                            && x.item.Status != TrackedItemStatus.Dismissed)
                .Select(x => x.item)
                .FirstOrDefaultAsync(cancellationToken);

        if (sameMessageId is not null)
        {
            return sameMessageId;
        }

        if (!string.IsNullOrWhiteSpace(message.ThreadKey))
        {
            var sameThread = await dbContext.TrackedItems
                .Where(x => x.ThreadKey == message.ThreadKey
                            && x.Status != TrackedItemStatus.Done
                            && x.Status != TrackedItemStatus.Dismissed)
                .FirstOrDefaultAsync(cancellationToken);
            if (sameThread is not null)
            {
                return sameThread;
            }
        }

        var subjectHash = Hash(message.Subject.Trim().ToUpperInvariant());
        var bodyHash = Hash(message.NormalizedBody ?? "");
        var receivedDayStart = new DateTimeOffset(message.ReceivedAt.Date, message.ReceivedAt.Offset);
        var receivedDayEnd = receivedDayStart.AddDays(1);
        return dbContext.TrackedItems
            .Join(dbContext.EmailMessages, item => item.EmailMessageId, email => email.Id, (item, email) => new { item, email })
            .Where(x => x.email.SenderEmail == message.SenderEmail
                        && x.item.Status != TrackedItemStatus.Done
                        && x.item.Status != TrackedItemStatus.Dismissed)
            .AsEnumerable()
            .Where(x => x.email.ReceivedAt >= receivedDayStart
                        && x.email.ReceivedAt < receivedDayEnd
                        && Hash(x.email.Subject.Trim().ToUpperInvariant()) == subjectHash
                        && Hash(x.email.NormalizedBody ?? "") == bodyHash)
            .Select(x => x.item)
            .FirstOrDefault();
    }

    private async Task NotifyIfNeededAsync(
        TrackedItem item,
        EmailMessage message,
        ClassificationResult result,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Sending notification. Kind={NotificationKind} TrackedItemId={TrackedItemId} MessageId={MessageId}",
            NotificationKind.NewActionableItem,
            item.Id,
            message.Id);
        await notificationService.NotifyAsync(NotificationKind.NewActionableItem, item, message, cancellationToken);

        if (result.UserActionDeadline?.Date == DateTimeOffset.Now.Date)
        {
            logger.LogInformation(
                "Sending notification. Kind={NotificationKind} TrackedItemId={TrackedItemId} MessageId={MessageId}",
                NotificationKind.DeadlineDueToday,
                item.Id,
                message.Id);
            await notificationService.NotifyAsync(NotificationKind.DeadlineDueToday, item, message, cancellationToken);
        }

        if (result.UserObligationMayEscalate)
        {
            logger.LogInformation(
                "Sending notification. Kind={NotificationKind} TrackedItemId={TrackedItemId} MessageId={MessageId}",
                NotificationKind.PossibleEscalation,
                item.Id,
                message.Id);
            await notificationService.NotifyAsync(NotificationKind.PossibleEscalation, item, message, cancellationToken);
        }
    }

    private void LogProcessingCompleted(
        FetchedEmail fetchedEmail,
        ProcessedEmail processed,
        EmailProcessingOutcome outcome,
        Stopwatch stopwatch)
    {
        logger.LogInformation(
            "Processing fetched email completed. AccountId={AccountId} Folder={Folder} UidValidity={UidValidity} ImapUid={ImapUid} ProcessedEmailId={ProcessedEmailId} MessageId={MessageId} Outcome={Outcome} ProcessingStatus={ProcessingStatus} ElapsedMilliseconds={ElapsedMilliseconds}",
            fetchedEmail.AccountId,
            fetchedEmail.Folder,
            fetchedEmail.UidValidity,
            fetchedEmail.ImapUid,
            processed.Id,
            fetchedEmail.Message.Id,
            outcome,
            processed.ProcessingStatus,
            stopwatch.ElapsedMilliseconds);
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private static string ShortHash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value[..Math.Min(12, value.Length)];
}
