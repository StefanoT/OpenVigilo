using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vigilo.Classification;
using Vigilo.Configuration;
using Vigilo.Core;
using Vigilo.LocalAi;
using Vigilo.Storage;

namespace Vigilo.Tests;

public sealed class ProcessingLedgerTests
{
    [Fact]
    public async Task Ledger_uses_uid_identity_and_reclassifies_only_body_changes()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Reply to the sender", requiresReply: true));
        fixture.Classifier.Enqueue(Actionable("Updated body needs reply", requiresReply: true));

        var first = Email(uid: 42, bodyHash: "body-1", flagsHash: "flags-1");
        var firstOutcome = await fixture.Processor.ProcessFetchedEmailAsync(first, CancellationToken.None);
        var duplicateOutcome = await fixture.Processor.ProcessFetchedEmailAsync(first, CancellationToken.None);
        var changedBody = first with
        {
            BodyHash = "body-2",
            Message = first.Message.WithBody("Updated body")
        };
        var reclassifiedOutcome = await fixture.Processor.ProcessFetchedEmailAsync(changedBody, CancellationToken.None);
        var flagsOnlyOutcome = await fixture.Processor.ProcessFetchedEmailAsync(changedBody with { FlagsHash = "flags-2" }, CancellationToken.None);

        Assert.Equal(EmailProcessingOutcome.Classified, firstOutcome);
        Assert.Equal(EmailProcessingOutcome.Unchanged, duplicateOutcome);
        Assert.Equal(EmailProcessingOutcome.Classified, reclassifiedOutcome);
        Assert.Equal(EmailProcessingOutcome.FlagsOnlyUpdated, flagsOnlyOutcome);
        Assert.Equal(1, await fixture.Db.TrackedItems.CountAsync());
        Assert.Equal(2, fixture.Classifier.Calls);
    }

    [Fact]
    public async Task Live_report_counts_all_messages_in_the_persisted_processing_ledger()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("First", requiresReply: true));
        fixture.Classifier.Enqueue(Actionable("Second", requiresReply: true));

        await fixture.Processor.ProcessFetchedEmailAsync(Email(uid: 1), CancellationToken.None);
        await fixture.Processor.ProcessFetchedEmailAsync(Email(uid: 2, bodyHash: "body-2"), CancellationToken.None);

        var report = await fixture.LiveReport.GetLiveReportAsync(CancellationToken.None);

        Assert.Equal(2, report.ProcessedMessageCount);
    }

    [Fact]
    public async Task New_tracked_item_notifies_report_and_table_subscribers_on_other_service_instances()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Reply to the sender", requiresReply: true));
        var tableReportService = new LiveReportService(fixture.DbContextFactory, fixture.LiveReportChangeNotifier);
        var changes = new List<LiveReportChange>();
        EventHandler<LiveReportChangedEventArgs> handler = (_, args) => changes.Add(args.Changes);
        tableReportService.ReportChanged += handler;

        try
        {
            await fixture.Processor.ProcessFetchedEmailAsync(Email(), CancellationToken.None);
        }
        finally
        {
            tableReportService.ReportChanged -= handler;
        }

        Assert.Equal(1, await fixture.Db.TrackedItems.CountAsync());
        Assert.Equal([LiveReportChange.All], changes);
    }

    [Fact]
    public async Task New_non_actionable_message_notifies_summary_without_invalidating_tracked_items()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(NonActionableNewsletter());
        var changes = new List<LiveReportChange>();
        EventHandler<LiveReportChangedEventArgs> handler = (_, args) => changes.Add(args.Changes);
        fixture.LiveReport.ReportChanged += handler;

        try
        {
            await fixture.Processor.ProcessFetchedEmailAsync(Email(), CancellationToken.None);
        }
        finally
        {
            fixture.LiveReport.ReportChanged -= handler;
        }

        Assert.Empty(await fixture.Db.TrackedItems.ToListAsync());
        Assert.Equal([LiveReportChange.Summary], changes);
    }

    [Fact]
    public async Task Flags_only_update_does_not_invalidate_live_report_or_tracked_items()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Reply to the sender", requiresReply: true));
        var fetched = Email(flagsHash: "flags-1");
        await fixture.Processor.ProcessFetchedEmailAsync(fetched, CancellationToken.None);
        var changes = new List<LiveReportChange>();
        EventHandler<LiveReportChangedEventArgs> handler = (_, args) => changes.Add(args.Changes);
        fixture.LiveReport.ReportChanged += handler;

        try
        {
            var outcome = await fixture.Processor.ProcessFetchedEmailAsync(
                fetched with { FlagsHash = "flags-2" },
                CancellationToken.None);

            Assert.Equal(EmailProcessingOutcome.FlagsOnlyUpdated, outcome);
        }
        finally
        {
            fixture.LiveReport.ReportChanged -= handler;
        }

        Assert.Empty(changes);
    }

    [Fact]
    public async Task UidValidity_change_marks_previous_folder_entries_stale()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Reply", requiresReply: true));

        await fixture.Processor.ProcessFetchedEmailAsync(Email(uidValidity: 10), CancellationToken.None);
        await fixture.Processor.MarkFolderUidValidityChangedAsync(TestFixture.AccountId, "Inbox", 11, CancellationToken.None);

        var processed = await fixture.Db.ProcessedEmails.SingleAsync();
        Assert.Equal(ProcessingStatus.DeletedFromMailbox, processed.ProcessingStatus);
    }

    [Fact]
    public async Task Existing_email_reclassifies_when_classifier_version_changes()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Initial", requiresReply: true));
        fixture.Classifier.Enqueue(Actionable("After version change", requiresReply: true));
        var fetched = Email();
        await fixture.Processor.ProcessFetchedEmailAsync(fetched, CancellationToken.None);
        var processed = await fixture.Db.ProcessedEmails.SingleAsync();
        processed.ClassificationVersion = "old-version";
        await fixture.Db.SaveChangesAsync();

        var outcome = await fixture.Processor.ProcessFetchedEmailAsync(fetched, CancellationToken.None);

        Assert.Equal(EmailProcessingOutcome.Classified, outcome);
        Assert.Equal(2, fixture.Classifier.Calls);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(ClassificationMetadata.CurrentVersion, (await fixture.Db.ProcessedEmails.SingleAsync()).ClassificationVersion);
    }

    [Fact]
    public async Task Visible_tasks_are_deduplicated_by_thread_key()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("First", requiresReply: true));
        fixture.Classifier.Enqueue(Actionable("Thread follow-up", requiresReply: true));
        var first = Email(uid: 1);
        first.Message.ThreadKey = "thread-1";
        var second = Email(uid: 2, bodyHash: "body-2");
        second.Message.ThreadKey = "thread-1";
        second.Message.MessageIdHeader = "different-message-id";

        await fixture.Processor.ProcessFetchedEmailAsync(first, CancellationToken.None);
        await fixture.Processor.ProcessFetchedEmailAsync(second, CancellationToken.None);

        Assert.Equal(2, await fixture.Db.ProcessedEmails.CountAsync());
        Assert.Equal(1, await fixture.Db.TrackedItems.CountAsync());
    }

    [Fact]
    public async Task Sent_reply_detection_annotates_open_item_without_completing_it()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Reply", requiresReply: true));
        var fetched = Email();
        fetched.Message.ThreadKey = "reply-thread";
        await fixture.Processor.ProcessFetchedEmailAsync(fetched, CancellationToken.None);
        var sent = Message("I replied");
        sent.Folder = "Sent";
        sent.ProviderMessageId = "Sent:1:99";
        sent.ThreadKey = "reply-thread";
        sent.ReceivedAt = DateTimeOffset.Now.AddMinutes(5);
        fixture.Db.EmailMessages.Add(sent);
        await fixture.Db.SaveChangesAsync();

        var detector = new SentReplyDetectionService(fixture.DbContextFactory, fixture.AccountSettings, fixture.LiveReport);
        var changed = await detector.DetectSentRepliesAsync(CancellationToken.None);
        var item = await fixture.Db.TrackedItems.SingleAsync();

        Assert.Equal(1, changed);
        Assert.NotNull(item.SentReplyDetectedAt);
        Assert.Equal(TrackedItemStatus.Open, item.Status);
    }

    [Fact]
    public async Task Sent_reply_detection_does_not_cross_account_boundaries()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var secondAccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        fixture.Db.Accounts.Add(new EmailAccount
        {
            Id = secondAccountId,
            EmailAddress = "second@yahoo.com",
            Username = "second@yahoo.com"
        });
        await fixture.Db.SaveChangesAsync();

        fixture.Classifier.Enqueue(Actionable("Reply", requiresReply: true));
        var fetched = Email();
        fetched.Message.ThreadKey = "reply-thread";
        await fixture.Processor.ProcessFetchedEmailAsync(fetched, CancellationToken.None);

        var sent = Message("I replied");
        sent.AccountId = secondAccountId;
        sent.Folder = "Sent";
        sent.ProviderMessageId = "Sent:1:99";
        sent.ThreadKey = "reply-thread";
        sent.ReceivedAt = DateTimeOffset.Now.AddMinutes(5);
        fixture.Db.EmailMessages.Add(sent);
        await fixture.Db.SaveChangesAsync();

        var detector = new SentReplyDetectionService(fixture.DbContextFactory, fixture.AccountSettings, fixture.LiveReport);
        var changed = await detector.DetectSentRepliesAsync(CancellationToken.None);
        var item = await fixture.Db.TrackedItems.SingleAsync();

        Assert.Equal(0, changed);
        Assert.Null(item.SentReplyDetectedAt);
    }

    [Fact]
    public async Task Local_model_failure_does_not_create_tracked_item()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var processor = new EmailProcessingService(
            fixture.DbContextFactory,
            new ThrowingClassifier(),
            fixture.LiveReport,
            new RecordingNotificationService(),
            NullLogger<EmailProcessingService>.Instance);

        var outcome = await processor.ProcessFetchedEmailAsync(Email(), CancellationToken.None);
        var processed = await fixture.Db.ProcessedEmails.SingleAsync();

        Assert.Equal(EmailProcessingOutcome.Failed, outcome);
        Assert.Equal(ProcessingStatus.Failed, processed.ProcessingStatus);
        Assert.Contains("Local model is unavailable", processed.ErrorMessage);
        Assert.Equal(1, processed.ClassificationAttemptCount);
        Assert.Equal(ClassificationFailureKind.ModelUnavailable.ToString(), processed.LastClassificationFailureKind);
        Assert.NotNull(processed.NextClassificationAttemptAt);
        Assert.Empty(await fixture.Db.TrackedItems.ToListAsync());
    }

    [Fact]
    public async Task Classification_version_source_change_triggers_reclassification_of_unchanged_email()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("First pass"));
        await fixture.Processor.ProcessFetchedEmailAsync(Email(), CancellationToken.None);
        var processed = await fixture.Db.ProcessedEmails.SingleAsync();
        Assert.Equal(ClassificationMetadata.CurrentVersion, processed.ClassificationVersion);

        var versionSource = new MutableClassificationVersionSource
        {
            ClassificationVersion = "vigilo-semantic-harness-test-vNext"
        };
        var processor = new EmailProcessingService(
            fixture.DbContextFactory,
            fixture.Classifier,
            fixture.LiveReport,
            new RecordingNotificationService(),
            NullLogger<EmailProcessingService>.Instance,
            classificationVersionSource: versionSource);
        fixture.Classifier.Enqueue(Actionable("Second pass"));
        var outcome = await processor.ProcessFetchedEmailAsync(Email(), CancellationToken.None);

        Assert.Equal(EmailProcessingOutcome.Classified, outcome);
        // Re-query without tracking: the second processor saved through its own context.
        Assert.Equal(
            "vigilo-semantic-harness-test-vNext",
            (await fixture.Db.ProcessedEmails.AsNoTracking().SingleAsync()).ClassificationVersion);
        var trackedItem = await fixture.Db.TrackedItems.SingleAsync();
        Assert.Equal("Second pass", trackedItem.ActionSummary);
    }

    private sealed class MutableClassificationVersionSource : IClassificationVersionSource
    {
        public string ClassificationVersion { get; set; } = ClassificationMetadata.CurrentVersion;
        public string PromptVersion { get; set; } = ClassificationMetadata.CurrentPromptVersion;
    }

    [Fact]
    public async Task Email_content_ambiguity_remains_user_review_without_becoming_a_technical_failure()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(ClassificationResult.NeedsReviewFallback(
            "The email itself is ambiguous and requires user judgment."));

        var outcome = await fixture.Processor.ProcessFetchedEmailAsync(Email(), CancellationToken.None);
        var processed = await fixture.Db.ProcessedEmails.SingleAsync();
        var item = await fixture.Db.TrackedItems.SingleAsync();

        Assert.Equal(EmailProcessingOutcome.NeedsReview, outcome);
        Assert.Equal(ProcessingStatus.Classified, processed.ProcessingStatus);
        Assert.Equal(0, processed.ClassificationAttemptCount);
        Assert.Equal(TrackedItemStatus.NeedsReview, item.Status);
    }

    [Fact]
    public async Task Deferred_classification_is_retried_when_due_and_recovery_state_is_cleared()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(ClassificationResult.UnavailableFallback(
            "Unusable output.",
            ClassificationFailureKind.UnusableModelOutput));
        fixture.Classifier.Enqueue(Actionable("Recovered", requiresReply: true) with
        {
            RecoveryKind = ClassificationRecoveryKind.ReviewedByModel
        });
        var fetched = Email();

        var firstOutcome = await fixture.Processor.ProcessFetchedEmailAsync(fetched, CancellationToken.None);
        var immediateOutcome = await fixture.Processor.ProcessFetchedEmailAsync(fetched, CancellationToken.None);
        var processed = await fixture.Db.ProcessedEmails.SingleAsync();

        Assert.Equal(EmailProcessingOutcome.Failed, firstOutcome);
        Assert.Equal(EmailProcessingOutcome.Unchanged, immediateOutcome);
        Assert.Equal(1, fixture.Classifier.Calls);
        Assert.Equal(1, processed.ClassificationAttemptCount);
        Assert.Equal(ClassificationFailureKind.UnusableModelOutput.ToString(), processed.LastClassificationFailureKind);

        processed.NextClassificationAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        var retryCount = await fixture.Processor.RetryDeferredClassificationsAsync(CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();
        processed = await fixture.Db.ProcessedEmails.SingleAsync();

        Assert.Equal(1, retryCount);
        Assert.Equal(2, fixture.Classifier.Calls);
        Assert.Equal(ProcessingStatus.Classified, processed.ProcessingStatus);
        Assert.Equal(0, processed.ClassificationAttemptCount);
        Assert.Null(processed.NextClassificationAttemptAt);
        Assert.Equal("", processed.LastClassificationFailureKind);
        Assert.Equal(ClassificationRecoveryKind.ReviewedByModel.ToString(), processed.LastClassificationRecoveryKind);
        Assert.Single(await fixture.Db.TrackedItems.ToListAsync());
    }

    [Fact]
    public async Task Deferred_classification_stops_retrying_after_the_attempt_limit()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var fetched = Email();
        fixture.Db.ProcessedEmails.Add(new ProcessedEmail
        {
            AccountId = fetched.AccountId,
            Folder = fetched.Folder,
            UidValidity = fetched.UidValidity,
            ImapUid = fetched.ImapUid,
            SubjectHash = fetched.SubjectHash,
            BodyHash = fetched.BodyHash,
            FlagsHash = fetched.FlagsHash,
            ReceivedAt = fetched.Message.ReceivedAt,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ProcessingStatus = ProcessingStatus.Failed,
            ClassificationVersion = ClassificationMetadata.CurrentVersion,
            ClassificationAttemptCount = 5,
            EmailMessageId = fetched.Message.Id
        });
        fixture.Db.EmailMessages.Add(fetched.Message);
        await fixture.Db.SaveChangesAsync();

        var outcome = await fixture.Processor.ProcessFetchedEmailAsync(fetched, CancellationToken.None);

        Assert.Equal(EmailProcessingOutcome.Unchanged, outcome);
        Assert.Equal(0, fixture.Classifier.Calls);
    }

    [Fact]
    public async Task Final_failed_classification_attempt_surfaces_the_email_for_review()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var fetched = Email();
        // Four attempts already failed with backoff; this processing pass spends the final
        // allowed attempt, which must surface the email instead of idling it as Failed.
        fixture.Db.ProcessedEmails.Add(new ProcessedEmail
        {
            AccountId = fetched.AccountId,
            Folder = fetched.Folder,
            UidValidity = fetched.UidValidity,
            ImapUid = fetched.ImapUid,
            SubjectHash = fetched.SubjectHash,
            BodyHash = fetched.BodyHash,
            FlagsHash = fetched.FlagsHash,
            ReceivedAt = fetched.Message.ReceivedAt,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ProcessingStatus = ProcessingStatus.Failed,
            ClassificationVersion = ClassificationMetadata.CurrentVersion,
            ClassificationAttemptCount = 4,
            LastClassificationFailureKind = ClassificationFailureKind.UnusableModelOutput.ToString(),
            EmailMessageId = fetched.Message.Id
        });
        fixture.Db.EmailMessages.Add(fetched.Message);
        await fixture.Db.SaveChangesAsync();
        fixture.Classifier.Enqueue(ClassificationResult.UnavailableFallback(
            "Unusable output.",
            ClassificationFailureKind.UnusableModelOutput));

        var outcome = await fixture.Processor.ProcessFetchedEmailAsync(fetched, CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();
        var processed = await fixture.Db.ProcessedEmails.AsNoTracking().SingleAsync();
        var item = await fixture.Db.TrackedItems.SingleAsync();

        Assert.Equal(EmailProcessingOutcome.NeedsReview, outcome);
        Assert.Equal(ProcessingStatus.Classified, processed.ProcessingStatus);
        Assert.Equal(5, processed.ClassificationAttemptCount);
        Assert.Null(processed.NextClassificationAttemptAt);
        Assert.Equal(
            ClassificationFailureKind.UnusableModelOutput.ToString(),
            processed.LastClassificationFailureKind);
        Assert.Equal(TrackedItemStatus.NeedsReview, item.Status);
        Assert.Contains("5 attempts", item.Reason);
        Assert.Contains("UnusableModelOutput", item.Reason);
    }

    [Fact]
    public async Task Retry_sweep_surfaces_already_exhausted_deferrals_for_review()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var fetched = Email();
        fixture.Db.ProcessedEmails.Add(new ProcessedEmail
        {
            AccountId = fetched.AccountId,
            Folder = fetched.Folder,
            UidValidity = fetched.UidValidity,
            ImapUid = fetched.ImapUid,
            SubjectHash = fetched.SubjectHash,
            BodyHash = fetched.BodyHash,
            FlagsHash = fetched.FlagsHash,
            ReceivedAt = fetched.Message.ReceivedAt,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ProcessingStatus = ProcessingStatus.Failed,
            ClassificationVersion = ClassificationMetadata.CurrentVersion,
            ClassificationAttemptCount = 5,
            LastClassificationFailureKind = ClassificationFailureKind.ModelUnavailable.ToString(),
            EmailMessageId = fetched.Message.Id
        });
        fixture.Db.EmailMessages.Add(fetched.Message);
        await fixture.Db.SaveChangesAsync();

        var retryCount = await fixture.Processor.RetryDeferredClassificationsAsync(CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();
        var processed = await fixture.Db.ProcessedEmails.AsNoTracking().SingleAsync();
        var item = await fixture.Db.TrackedItems.SingleAsync();

        Assert.Equal(0, retryCount);
        Assert.Equal(0, fixture.Classifier.Calls);
        Assert.Equal(ProcessingStatus.Classified, processed.ProcessingStatus);
        Assert.Equal(5, processed.ClassificationAttemptCount);
        Assert.Equal(TrackedItemStatus.NeedsReview, item.Status);
        Assert.Contains("5 attempts", item.Reason);
    }

    [Fact]
    public async Task Non_actionable_newsletter_does_not_create_open_tracked_item()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(NonActionableNewsletter());

        var outcome = await fixture.Processor.ProcessFetchedEmailAsync(Email(), CancellationToken.None);
        var processed = await fixture.Db.ProcessedEmails.SingleAsync();

        Assert.Equal(EmailProcessingOutcome.Classified, outcome);
        Assert.Equal(ProcessingStatus.Classified, processed.ProcessingStatus);
        Assert.Empty(await fixture.Db.TrackedItems.ToListAsync());
    }

    [Fact]
    public async Task Non_actionable_promotion_is_kept_in_commercial_offers_without_reply_requirement()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(new ClassificationResult
        {
            MessageType = ClassificationMessageType.Promotion,
            IsActionable = false,
            HasUserSpecificObligation = false,
            UserReplyRequired = true,
            UserObligationMayEscalate = true,
            UserActionDeadline = DateTimeOffset.Now.AddDays(1),
            ActionSummary = "Enroll in the free short course",
            Reason = "This is a promotional course invitation."
        });
        fixture.Classifier.Enqueue(Actionable("Normal open work"));

        var promotion = Email(uid: 1);
        promotion.Message.ThreadKey = "commercial-offer-thread";
        promotion.Message.MessageIdHeader = "commercial-offer@example.com";
        promotion.Message.Subject = "Commercial course offer";
        promotion.Message.WithBody("Enroll in the free short course.");
        var normalOpen = Email(uid: 2, bodyHash: "normal-open");
        normalOpen.Message.ThreadKey = "normal-open-thread";
        normalOpen.Message.MessageIdHeader = "normal-open@example.com";
        normalOpen.Message.Subject = "Normal open work";
        normalOpen.Message.WithBody("Handle this normal open work.");

        var outcome = await fixture.Processor.ProcessFetchedEmailAsync(promotion, CancellationToken.None);
        await fixture.Processor.ProcessFetchedEmailAsync(normalOpen, CancellationToken.None);

        var stored = await fixture.Db.TrackedItems.SingleAsync(x => x.MessageType == ClassificationMessageType.Promotion);
        var views = await fixture.LiveReport.GetItemsAsync(CancellationToken.None);
        var report = await fixture.LiveReport.GetLiveReportAsync(CancellationToken.None);
        Assert.Equal(EmailProcessingOutcome.Classified, outcome);
        Assert.Equal(ClassificationMessageType.Promotion, stored.MessageType);
        Assert.False(stored.RequiresReply);
        Assert.NotNull(stored.Deadline);
        Assert.Equal(["Upcoming", "Commercial offers"], views.Select(x => x.Section));
        Assert.Equal(1, report.CommercialOfferCount);
        Assert.Equal(0, report.WaitingReplyCount);
        Assert.Equal(1, report.UpcomingCount);
    }

    [Fact]
    public async Task Newsletter_with_a_dated_offer_is_kept_in_commercial_offers_instead_of_active_work()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(new ClassificationResult
        {
            MessageType = ClassificationMessageType.Newsletter,
            IsActionable = false,
            HasUserSpecificObligation = false,
            UserActionDeadline = DateTimeOffset.Now.AddDays(3),
            ActionSummary = "",
            Reason = "The newsletter advertises a webinar on that date."
        });
        fixture.Classifier.Enqueue(new ClassificationResult
        {
            MessageType = ClassificationMessageType.Newsletter,
            IsActionable = false,
            HasUserSpecificObligation = false,
            UserActionDeadline = DateTimeOffset.Now.AddDays(-2),
            ActionSummary = "",
            Reason = "The newsletter advertises a workshop that already took place."
        });

        var upcomingOffer = Email(uid: 1);
        upcomingOffer.Message.Subject = "Weekly digest with webinar invitation";
        upcomingOffer.Message.WithBody("Join our free webinar next week.");
        var expiredOffer = Email(uid: 2, bodyHash: "expired-offer");
        expiredOffer.Message.Subject = "Daily digest with workshop reminder";
        expiredOffer.Message.WithBody("The live workshop reminder was in this issue.");

        await fixture.Processor.ProcessFetchedEmailAsync(upcomingOffer, CancellationToken.None);
        await fixture.Processor.ProcessFetchedEmailAsync(expiredOffer, CancellationToken.None);

        var stored = (await fixture.Db.TrackedItems.ToListAsync()).ToDictionary(x => x.ActionSummary);
        Assert.Equal(["Daily digest with workshop reminder", "Weekly digest with webinar invitation"],
            stored.Keys.OrderBy(x => x).ToArray());
        Assert.All(stored.Values, item =>
        {
            Assert.Equal(ClassificationMessageType.Newsletter, item.MessageType);
            Assert.False(item.RequiresReply);
            Assert.False(item.IsEscalation);
        });
        Assert.Equal(TrackedItemStatus.Open, stored["Daily digest with workshop reminder"].Status);

        var views = await fixture.LiveReport.GetItemsAsync(CancellationToken.None);
        var report = await fixture.LiveReport.GetLiveReportAsync(CancellationToken.None);
        Assert.All(views, view =>
        {
            Assert.Equal("Commercial offers", view.Section);
            Assert.Equal(TrackedItemExpirationState.None, view.ExpirationState);
        });
        Assert.Equal(2, report.CommercialOfferCount);
        Assert.Equal(0, report.DueTodayCount);
        Assert.Equal(0, report.UpcomingCount);
    }

    [Fact]
    public async Task Structured_reply_deadline_and_escalation_results_create_open_tracked_items()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Reply", requiresReply: true));
        fixture.Classifier.Enqueue(Actionable("Deadline", deadline: DateTimeOffset.Now.AddDays(2)));
        fixture.Classifier.Enqueue(Actionable("Escalation", escalation: true));

        var reply = Email(uid: 1, bodyHash: "body-1");
        reply.Message.WithBody("Please reply to this message.");
        var deadline = Email(uid: 2, bodyHash: "body-2");
        deadline.Message.WithBody("Please finish this by Friday.");
        var escalation = Email(uid: 3, bodyHash: "body-3");
        escalation.Message.WithBody("Escalated customer issue.");

        await fixture.Processor.ProcessFetchedEmailAsync(reply, CancellationToken.None);
        await fixture.Processor.ProcessFetchedEmailAsync(deadline, CancellationToken.None);
        await fixture.Processor.ProcessFetchedEmailAsync(escalation, CancellationToken.None);

        var items = await fixture.Db.TrackedItems.OrderBy(x => x.ActionSummary).ToListAsync();
        Assert.Equal(3, items.Count);
        Assert.All(items, item => Assert.Equal(TrackedItemStatus.Open, item.Status));
        Assert.Contains(items, x => x.RequiresReply);
        Assert.Contains(items, x => x.Deadline is not null);
        Assert.Contains(items, x => x.IsEscalation);
    }

    [Fact]
    public async Task Sanitized_actionable_result_without_deadline_creates_open_tracked_item()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Update expiring credit card"));
        var fetched = Email();
        fetched.Message.Subject = "Google Cloud Platform & APIs: Your credit card is expiring soon";
        fetched.Message.NormalizedBody = "To keep your service running, update the expiration date or add another primary payment method.";
        fetched.Message.Snippet = fetched.Message.NormalizedBody;

        var outcome = await fixture.Processor.ProcessFetchedEmailAsync(fetched, CancellationToken.None);

        var item = await fixture.Db.TrackedItems.SingleAsync();
        Assert.Equal(EmailProcessingOutcome.Classified, outcome);
        Assert.Equal(TrackedItemStatus.Open, item.Status);
        Assert.Equal("Update expiring credit card", item.ActionSummary);
        Assert.Null(item.Deadline);
        Assert.False(item.RequiresReply);
        Assert.False(item.IsEscalation);
    }

    [Fact]
    public async Task Existing_open_item_is_dismissed_when_reclassified_as_non_actionable()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Reply", requiresReply: true));
        fixture.Classifier.Enqueue(NonActionableNewsletter());
        var fetched = Email();
        await fixture.Processor.ProcessFetchedEmailAsync(fetched, CancellationToken.None);

        var changedBody = fetched with
        {
            BodyHash = "newsletter-body",
            Message = fetched.Message.WithBody("Weekly AI roundup")
        };
        await fixture.Processor.ProcessFetchedEmailAsync(changedBody, CancellationToken.None);

        var item = await fixture.Db.TrackedItems.SingleAsync();
        var report = await fixture.LiveReport.GetLiveReportAsync(CancellationToken.None);
        Assert.Equal(TrackedItemStatus.Dismissed, item.Status);
        Assert.NotNull(item.DismissedAt);
        Assert.Equal(0, report.UpcomingCount);
    }

    [Fact]
    public async Task Live_report_groups_due_reply_escalation_and_needs_review()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Due today", deadline: DateTimeOffset.Now));
        fixture.Classifier.Enqueue(Actionable("Escalated", escalation: true));
        fixture.Classifier.Enqueue(Actionable("Due soon", deadline: DateTimeOffset.Now.AddDays(3)));
        fixture.Classifier.Enqueue(Actionable("Reply", requiresReply: true));
        fixture.Classifier.Enqueue(ClassificationResult.NeedsReviewFallback("Invalid JSON"));
        fixture.Classifier.Enqueue(Actionable("Open"));

        for (var i = 1; i <= 6; i++)
        {
            var fetched = Email(uid: i, bodyHash: $"body-{i}");
            fetched.Message.NormalizedBody = $"Please handle item {i}";
            fetched.Message.Snippet = fetched.Message.NormalizedBody;
            fetched.Message.Subject = $"Action required {i}";
            await fixture.Processor.ProcessFetchedEmailAsync(fetched, CancellationToken.None);
        }

        var report = await fixture.LiveReport.GetLiveReportAsync(CancellationToken.None);
        var items = await fixture.LiveReport.GetItemsAsync(CancellationToken.None);

        Assert.Equal(1, report.DueTodayCount);
        Assert.Equal(3, report.UpcomingCount);
        Assert.Equal(1, report.WaitingReplyCount);
        Assert.Equal(1, report.EscalationCount);
        Assert.Equal(1, report.NeedsReviewCount);
        Assert.Equal(
            ["Due today", "Waiting for my reply", "Needs review", "Upcoming", "Upcoming", "Upcoming"],
            items.Select(item => item.Section));
    }

    [Fact]
    public async Task Live_report_groups_overdue_items_into_due_today_with_expired_highlight()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Expired", deadline: DateTimeOffset.Now.AddDays(-1)));
        await fixture.Processor.ProcessFetchedEmailAsync(Email(uid: 1), CancellationToken.None);

        var report = await fixture.LiveReport.GetLiveReportAsync(CancellationToken.None);
        var item = Assert.Single(await fixture.LiveReport.GetItemsAsync(CancellationToken.None));

        Assert.Equal(1, report.DueTodayCount);
        Assert.Equal(TrackedItemStatus.Expired, item.Status);
        Assert.Equal("Due today", item.Section);
        Assert.Equal(TrackedItemExpirationState.Expired, item.ExpirationState);
    }

    [Fact]
    public async Task Commercial_offer_with_past_due_deadline_stays_unhighlighted_in_its_section()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(new ClassificationResult
        {
            MessageType = ClassificationMessageType.Promotion,
            IsActionable = false,
            UserActionDeadline = DateTimeOffset.Now.AddDays(-2),
            ActionSummary = "Seasonal sale",
            Reason = "Promotion with an expiry date."
        });
        await fixture.Processor.ProcessFetchedEmailAsync(Email(uid: 1), CancellationToken.None);

        var report = await fixture.LiveReport.GetLiveReportAsync(CancellationToken.None);
        var item = Assert.Single(await fixture.LiveReport.GetItemsAsync(CancellationToken.None));

        Assert.Equal(TrackedItemStatus.Open, item.Status);
        Assert.Equal("Commercial offers", item.Section);
        Assert.Equal(TrackedItemExpirationState.None, item.ExpirationState);
        Assert.Equal(1, report.CommercialOfferCount);
        Assert.Equal(0, report.UpcomingCount);
    }

    [Fact]
    public async Task Live_report_marks_items_under_72_hours_from_deadline_as_near_expiration()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable(
            "Reply before deadline",
            deadline: DateTimeOffset.Now.AddHours(48),
            requiresReply: true));
        await fixture.Processor.ProcessFetchedEmailAsync(Email(uid: 1), CancellationToken.None);

        var report = await fixture.LiveReport.GetLiveReportAsync(CancellationToken.None);
        var item = Assert.Single(await fixture.LiveReport.GetItemsAsync(CancellationToken.None));

        Assert.Equal(1, report.WaitingReplyCount);
        Assert.Equal("Waiting for my reply", item.Section);
        Assert.Equal(TrackedItemExpirationState.NearExpiration, item.ExpirationState);
    }

    [Fact]
    public async Task Live_report_items_include_the_configured_account_name()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var account = await fixture.Db.Accounts.SingleAsync();
        account.Name = "Personal mailbox";
        await fixture.Db.SaveChangesAsync();
        fixture.Classifier.Enqueue(Actionable("Reply", requiresReply: true));
        await fixture.Processor.ProcessFetchedEmailAsync(Email(uid: 1), CancellationToken.None);

        var item = Assert.Single(await fixture.LiveReport.GetItemsAsync(CancellationToken.None));

        Assert.Equal("Personal mailbox", item.AccountDisplayName);
    }

    [Fact]
    public async Task Tracked_messages_sort_same_category_by_deadline_then_received_datetime_descending()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Expired 28 days ago", deadline: DateTimeOffset.Now.AddDays(-28)));
        fixture.Classifier.Enqueue(Actionable("Expired 13 days ago", deadline: DateTimeOffset.Now.AddDays(-13)));
        fixture.Classifier.Enqueue(Actionable("Undated newer"));
        fixture.Classifier.Enqueue(Actionable("Undated older"));

        var older = Email(uid: 1, bodyHash: "body-1");
        older.Message.ReceivedAt = DateTimeOffset.Now.AddDays(-2);
        older.Message.Subject = "Older expired item";
        older.Message.NormalizedBody = "Please handle older expired item";
        older.Message.Snippet = older.Message.NormalizedBody;
        var newer = Email(uid: 2, bodyHash: "body-2");
        newer.Message.ReceivedAt = DateTimeOffset.Now.AddDays(-1);
        newer.Message.Subject = "Newer expired item";
        newer.Message.NormalizedBody = "Please handle newer expired item";
        newer.Message.Snippet = newer.Message.NormalizedBody;
        var undatedNewer = Email(uid: 3, bodyHash: "body-3");
        undatedNewer.Message.ReceivedAt = DateTimeOffset.Now.AddHours(-12);
        undatedNewer.Message.Subject = "Undated newer item";
        undatedNewer.Message.NormalizedBody = "Please handle undated newer item";
        undatedNewer.Message.Snippet = undatedNewer.Message.NormalizedBody;
        var undatedOlder = Email(uid: 4, bodyHash: "body-4");
        undatedOlder.Message.ReceivedAt = DateTimeOffset.Now.AddDays(-1);
        undatedOlder.Message.Subject = "Undated older item";
        undatedOlder.Message.NormalizedBody = "Please handle undated older item";
        undatedOlder.Message.Snippet = undatedOlder.Message.NormalizedBody;

        await fixture.Processor.ProcessFetchedEmailAsync(older, CancellationToken.None);
        await fixture.Processor.ProcessFetchedEmailAsync(newer, CancellationToken.None);
        await fixture.Processor.ProcessFetchedEmailAsync(undatedNewer, CancellationToken.None);
        await fixture.Processor.ProcessFetchedEmailAsync(undatedOlder, CancellationToken.None);

        var items = await fixture.LiveReport.GetItemsAsync(CancellationToken.None);

        Assert.Collection(
            items,
            item => Assert.Equal("Expired 28 days ago", item.ActionSummary),
            item => Assert.Equal("Expired 13 days ago", item.ActionSummary),
            item => Assert.Equal("Undated newer", item.ActionSummary),
            item => Assert.Equal("Undated older", item.ActionSummary));
    }

    [Fact]
    public async Task Tracked_message_clipboard_export_includes_processed_ledger_with_sqlite()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Reply", requiresReply: true));
        await fixture.Processor.ProcessFetchedEmailAsync(Email(), CancellationToken.None);
        var item = await fixture.Db.TrackedItems.SingleAsync();

        var text = await fixture.LiveReport.GetTrackedMessageClipboardTextAsync(item.Id, CancellationToken.None);

        Assert.NotNull(text);
        Assert.Contains("[Processing ledger]", text);
        Assert.Contains("ProcessingStatus: Classified", text);
        Assert.Contains("[Normalized body]", text);
    }

    [Fact]
    public async Task Tracked_message_detail_includes_original_body_and_classification_context()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Reply to customer", requiresReply: true));
        var fetched = Email();
        fetched.Message.Subject = "Customer follow-up";
        fetched.Message.NormalizedBody = "Please confirm the delivery date.";
        fetched.Message.OriginalTextBody = "Original email text";
        fetched.Message.OriginalHtmlBody = "<html><body><p>Original email HTML</p></body></html>";
        fetched.Message.Snippet = fetched.Message.NormalizedBody;

        await fixture.Processor.ProcessFetchedEmailAsync(fetched, CancellationToken.None);
        var item = await fixture.Db.TrackedItems.SingleAsync();

        var detail = await fixture.LiveReport.GetTrackedMessageDetailAsync(item.Id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("Customer follow-up", detail.Subject);
        Assert.Equal("Please confirm the delivery date.", detail.Body);
        Assert.Equal("Original email text", detail.OriginalTextBody);
        Assert.Equal("<html><body><p>Original email HTML</p></body></html>", detail.OriginalHtmlBody);
        Assert.Equal("HTML", detail.OriginalBodyKind);
        Assert.Equal("Reply to customer", detail.ActionSummary);
        Assert.Equal("Classified", detail.ProcessingStatus);
        Assert.True(detail.RequiresReply);
    }

    [Fact]
    public async Task Model_manager_detects_required_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "vigilo-model-" + Guid.NewGuid());
        var options = new ModelOptions { ModelRoot = root };
        var manager = new Phi4MiniModelManager(
            options,
            new DenyApprovalService(),
            new LocalAiActivityTracker(),
            NullLogger<Phi4MiniModelManager>.Instance);

        var missing = await manager.GetStatusAsync(CancellationToken.None);
        Directory.CreateDirectory(root);
        foreach (var file in options.RequiredFiles)
        {
            await File.WriteAllTextAsync(Path.Combine(root, file), "x");
        }

        var hashes = options.RequiredFiles.ToDictionary(
            file => file,
            file => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Path.Combine(root, file)))));
        await File.WriteAllTextAsync(
            Path.Combine(root, "vigilo-model-state.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                options.Version,
                Installed = true,
                LastCheckedAt = DateTimeOffset.UtcNow,
                Message = "Installed for test.",
                FileSha256 = hashes
            }));

        var installed = await manager.GetStatusAsync(CancellationToken.None);

        Assert.False(missing.IsInstalled);
        Assert.True(installed.IsInstalled);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Model_profile_catalog_applies_gemma_litert_profile()
    {
        var options = new ModelOptions
        {
            Preset = ModelProfileCatalog.Gemma4E2BLiteRtPreset,
            ModelBaseDirectory = Path.Combine(Path.GetTempPath(), "vigilo-models")
        };

        ModelProfileCatalog.ApplyPreset(options);

        Assert.Equal(ModelProfileCatalog.OpenAiCompatibleBackend, options.Backend);
        Assert.Equal("litert-community/gemma-4-E2B-it-litert-lm", options.Version);
        Assert.Equal("http://localhost:9379/v1", options.EndpointBaseUrl);
        Assert.Equal("gemma4-e2b", options.ChatModel);
        Assert.Empty(options.RequiredFiles);
        Assert.Empty(options.ModelRoot);
    }

    [Fact]
    public void Model_profile_catalog_applies_gemma_e4b_litert_profile()
    {
        var options = new ModelOptions
        {
            Preset = ModelProfileCatalog.Gemma4E4BLiteRtPreset,
            ModelBaseDirectory = Path.Combine(Path.GetTempPath(), "vigilo-models")
        };

        ModelProfileCatalog.ApplyPreset(options);

        Assert.Equal(ModelProfileCatalog.OpenAiCompatibleBackend, options.Backend);
        Assert.Equal("litert-community/gemma-4-E4B-it-litert-lm", options.Version);
        Assert.Equal("http://localhost:9379/v1", options.EndpointBaseUrl);
        Assert.Equal("gemma4-e4b", options.ChatModel);
        Assert.Empty(options.RequiredFiles);
        Assert.Empty(options.ModelRoot);
    }

    [Theory]
    [InlineData(ModelProfileCatalog.Phi4MiniPreset, 131_072, 7_168, 384, 6)]
    [InlineData(ModelProfileCatalog.Phi4FullPreset, 16_384, 6_144, 384, 5)]
    [InlineData(ModelProfileCatalog.Gemma4E2BLiteRtPreset, 131_072, 3_584, 1_024, 4)]
    [InlineData(ModelProfileCatalog.Gemma4E4BLiteRtPreset, 131_072, 4_608, 1_024, 5)]
    public void Model_profile_catalog_applies_model_specific_inference_capabilities(
        string preset,
        int expectedContextWindowTokens,
        int expectedInputTokens,
        int expectedOutputTokens,
        int expectedProgressiveRequests)
    {
        var options = new ModelOptions { Preset = preset };

        ModelProfileCatalog.ApplyPreset(options);

        Assert.Equal(expectedContextWindowTokens, options.InferenceCapabilities.ContextWindowTokens);
        Assert.Equal(expectedInputTokens, options.InferenceCapabilities.EffectiveInputTokenLimit);
        Assert.Equal(expectedOutputTokens, options.InferenceCapabilities.MaximumOutputTokens);
        Assert.Equal(expectedProgressiveRequests, options.InferenceCapabilities.MaximumProgressiveRequests);
    }

    [Fact]
    public void Model_profiles_leave_headroom_for_descriptive_classification_json()
    {
        const string representativeResponse =
            "{\"messageType\":\"transactional\",\"hasObligation\":\"yes\",\"requiresReply\":\"yes\","
            + "\"mayEscalate\":\"yes\",\"hasDeadline\":\"yes\","
            + "\"deadlineExpression\":\"Check-in: 06-08-2026 at 15:00\",\"normalizedDeadline\":\"2026-08-06T15:00:00+02:00\","
            + "\"actionSummary\":\"Confirm attendance with the organizer\","
            + "\"decisionReason\":\"The recipient must confirm attendance for the scheduled appointment.\","
            + "\"evidence\":[{\"segmentId\":\"current-body\",\"quote\":\"Please confirm your attendance at the check-in on 06-08-2026 at 15:00\",\"role\":\"primary\",\"occurrence\":null}]}";
        var estimatedResponseTokens = LocalAiInferenceCapabilities.Conservative
            .EstimateMessageTokens(representativeResponse);

        Assert.All(
            ModelProfileCatalog.Profiles,
            profile => Assert.True(
                estimatedResponseTokens * 2 <= profile.InferenceCapabilities.MaximumOutputTokens,
                $"{profile.Preset} does not leave two-times output headroom for the descriptive JSON contract."));
    }

    [Fact]
    public async Task Model_selection_service_persists_selected_preset()
    {
        var root = Path.Combine(Path.GetTempPath(), "vigilo-model-settings-" + Guid.NewGuid());
        var settingsPath = Path.Combine(root, "model-settings.json");
        var options = new ModelOptions
        {
            ModelBaseDirectory = Path.Combine(root, "Models")
        };
        var service = new ModelSelectionService(
            options,
            new ModelSettingsStore(settingsPath),
            NullLogger<ModelSelectionService>.Instance,
            new AtomicConfigurationRepository());

        await service.SelectProfileAsync(ModelProfileCatalog.Gemma4E2BLiteRtPreset, CancellationToken.None);

        Assert.Equal(ModelProfileCatalog.Gemma4E2BLiteRtPreset, options.Preset);
        var persisted = await File.ReadAllTextAsync(settingsPath);
        Assert.Contains(ModelProfileCatalog.Gemma4E2BLiteRtPreset, persisted);
        Assert.Contains("\"SchemaVersion\": 1", persisted);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Snoozed_items_reopen_when_snooze_expires()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Reply", requiresReply: true));
        await fixture.Processor.ProcessFetchedEmailAsync(Email(), CancellationToken.None);
        var item = await fixture.Db.TrackedItems.SingleAsync();

        await fixture.LiveReport.SnoozeAsync(item.Id, DateTimeOffset.UtcNow.AddSeconds(-1), CancellationToken.None);
        var items = await fixture.LiveReport.GetItemsAsync(CancellationToken.None);

        Assert.Equal(TrackedItemStatus.Open, items.Single().Status);
    }

    [Fact]
    public async Task Updating_deadline_persists_and_recalculates_expired_status()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Reply", requiresReply: true));
        await fixture.Processor.ProcessFetchedEmailAsync(Email(), CancellationToken.None);
        var item = await fixture.Db.TrackedItems.SingleAsync();
        var pastDeadline = DateTimeOffset.Now.AddDays(-1);

        await fixture.LiveReport.UpdateDeadlineAsync(item.Id, pastDeadline, CancellationToken.None);

        await fixture.Db.Entry(item).ReloadAsync();
        Assert.Equal(pastDeadline, item.Deadline);
        Assert.Equal(TrackedItemStatus.Expired, item.Status);

        await fixture.LiveReport.UpdateDeadlineAsync(item.Id, null, CancellationToken.None);

        await fixture.Db.Entry(item).ReloadAsync();
        Assert.Null(item.Deadline);
        Assert.Equal(TrackedItemStatus.Open, item.Status);
    }

    [Fact]
    public async Task Retention_preserves_bodies_for_retained_messages()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Classifier.Enqueue(Actionable("Reply", requiresReply: true));
        await fixture.Processor.ProcessFetchedEmailAsync(Email(), CancellationToken.None);

        var maintenance = new LocalDataMaintenanceService(fixture.DbContextFactory, fixture.AccountSettings, fixture.LiveReport);
        var result = await maintenance.ApplyRetentionAsync(CancellationToken.None);

        Assert.Equal(0, result.DeletedMessages);
        var message = await fixture.Db.EmailMessages.SingleAsync();
        Assert.Equal("Please reply", message.NormalizedBody);
        Assert.Equal("Please reply", message.OriginalTextBody);
    }

    [Fact]
    public async Task Retention_purges_dismissed_messages_by_receipt_date_and_done_messages_by_completion_date()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var cases = new[]
        {
            (Status: TrackedItemStatus.Dismissed, ReceivedAge: TimeSpan.FromDays(8), StatusAge: TimeSpan.FromDays(1), ShouldDelete: true),
            (Status: TrackedItemStatus.Dismissed, ReceivedAge: TimeSpan.FromDays(6), StatusAge: TimeSpan.FromDays(8), ShouldDelete: false),
            (Status: TrackedItemStatus.Done, ReceivedAge: TimeSpan.Zero, StatusAge: TimeSpan.FromDays(32), ShouldDelete: true),
            (Status: TrackedItemStatus.Done, ReceivedAge: TimeSpan.Zero, StatusAge: TimeSpan.FromDays(28), ShouldDelete: false)
        };
        var expectedRetainedMessageIds = new HashSet<Guid>();

        foreach (var testCase in cases)
        {
            var statusChangedAt = now - testCase.StatusAge;
            var message = Message(testCase.Status.ToString());
            message.ReceivedAt = now - testCase.ReceivedAge;
            var item = new TrackedItem
            {
                EmailMessageId = message.Id,
                Status = testCase.Status,
                CreatedAt = statusChangedAt,
                UpdatedAt = statusChangedAt,
                CompletedAt = testCase.Status == TrackedItemStatus.Done ? statusChangedAt : null,
                DismissedAt = testCase.Status == TrackedItemStatus.Dismissed ? statusChangedAt : null
            };
            fixture.Db.EmailMessages.Add(message);
            fixture.Db.TrackedItems.Add(item);
            fixture.Db.ProcessedEmails.Add(new ProcessedEmail
            {
                AccountId = TestFixture.AccountId,
                Folder = "Inbox",
                UidValidity = 1,
                ImapUid = cases.ToList().IndexOf(testCase) + 100,
                EmailMessageId = message.Id
            });
            fixture.Db.NotificationRecords.Add(new NotificationRecord
            {
                TrackedItemId = item.Id,
                Kind = NotificationKind.NewActionableItem,
                DedupeKey = message.Id.ToString("N")
            });

            if (!testCase.ShouldDelete)
            {
                expectedRetainedMessageIds.Add(message.Id);
            }
        }

        await fixture.Db.SaveChangesAsync();
        var maintenance = new LocalDataMaintenanceService(fixture.DbContextFactory, fixture.AccountSettings, fixture.LiveReport);

        var result = await maintenance.ApplyRetentionAsync(CancellationToken.None);

        Assert.Equal(2, result.DeletedMessages);
        Assert.Equal(2, result.DeletedProcessedRows);
        Assert.Equal(expectedRetainedMessageIds, (await fixture.Db.EmailMessages.Select(x => x.Id).ToListAsync()).ToHashSet());
        Assert.Equal(2, await fixture.Db.TrackedItems.CountAsync());
        Assert.Equal(2, await fixture.Db.NotificationRecords.CountAsync());
    }

    [Fact]
    public async Task Retention_purges_commercial_offers_one_week_after_email_receipt()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var expiredMessage = Message("Expired commercial offer");
        expiredMessage.ReceivedAt = now.AddDays(-8);
        var recentMessage = Message("Recent commercial offer");
        recentMessage.ReceivedAt = now.AddDays(-6);
        var expiredItem = new TrackedItem
        {
            EmailMessageId = expiredMessage.Id,
            Status = TrackedItemStatus.Open,
            MessageType = ClassificationMessageType.Promotion,
            CreatedAt = now,
            UpdatedAt = now
        };
        var recentItem = new TrackedItem
        {
            EmailMessageId = recentMessage.Id,
            Status = TrackedItemStatus.Open,
            MessageType = ClassificationMessageType.Promotion,
            CreatedAt = now,
            UpdatedAt = now
        };
        fixture.Db.EmailMessages.AddRange(expiredMessage, recentMessage);
        fixture.Db.TrackedItems.AddRange(expiredItem, recentItem);
        fixture.Db.ProcessedEmails.AddRange(
            new ProcessedEmail
            {
                AccountId = TestFixture.AccountId,
                Folder = "Inbox",
                UidValidity = 1,
                ImapUid = 201,
                EmailMessageId = expiredMessage.Id
            },
            new ProcessedEmail
            {
                AccountId = TestFixture.AccountId,
                Folder = "Inbox",
                UidValidity = 1,
                ImapUid = 202,
                EmailMessageId = recentMessage.Id
            });
        fixture.Db.NotificationRecords.AddRange(
            new NotificationRecord
            {
                TrackedItemId = expiredItem.Id,
                Kind = NotificationKind.NewActionableItem,
                DedupeKey = expiredMessage.Id.ToString("N")
            },
            new NotificationRecord
            {
                TrackedItemId = recentItem.Id,
                Kind = NotificationKind.NewActionableItem,
                DedupeKey = recentMessage.Id.ToString("N")
            });
        await fixture.Db.SaveChangesAsync();
        var maintenance = new LocalDataMaintenanceService(fixture.DbContextFactory, fixture.AccountSettings, fixture.LiveReport);

        var result = await maintenance.ApplyRetentionAsync(CancellationToken.None);

        Assert.Equal(1, result.DeletedMessages);
        Assert.Equal(1, result.DeletedProcessedRows);
        Assert.Equal(recentMessage.Id, (await fixture.Db.EmailMessages.SingleAsync()).Id);
        Assert.Equal(recentItem.Id, (await fixture.Db.TrackedItems.SingleAsync()).Id);
        Assert.Equal(1, await fixture.Db.ProcessedEmails.CountAsync());
        Assert.Equal(recentItem.Id, (await fixture.Db.NotificationRecords.SingleAsync()).TrackedItemId);
    }

    [Fact]
    public async Task Retention_purges_dated_newsletter_offers_one_week_after_email_receipt()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var expiredMessage = Message("Expired newsletter offer");
        expiredMessage.ReceivedAt = now.AddDays(-8);
        var recentMessage = Message("Recent newsletter offer");
        recentMessage.ReceivedAt = now.AddDays(-6);
        var expiredItem = new TrackedItem
        {
            EmailMessageId = expiredMessage.Id,
            Status = TrackedItemStatus.Open,
            MessageType = ClassificationMessageType.Newsletter,
            Deadline = now.AddDays(-9),
            CreatedAt = now,
            UpdatedAt = now
        };
        var recentItem = new TrackedItem
        {
            EmailMessageId = recentMessage.Id,
            Status = TrackedItemStatus.Open,
            MessageType = ClassificationMessageType.Newsletter,
            Deadline = now.AddDays(1),
            CreatedAt = now,
            UpdatedAt = now
        };
        fixture.Db.EmailMessages.AddRange(expiredMessage, recentMessage);
        fixture.Db.TrackedItems.AddRange(expiredItem, recentItem);
        await fixture.Db.SaveChangesAsync();
        var maintenance = new LocalDataMaintenanceService(fixture.DbContextFactory, fixture.AccountSettings, fixture.LiveReport);

        var result = await maintenance.ApplyRetentionAsync(CancellationToken.None);

        Assert.Equal(1, result.DeletedMessages);
        fixture.Db.ChangeTracker.Clear();
        var remaining = await fixture.Db.TrackedItems.SingleAsync();
        Assert.Equal(recentItem.Id, remaining.Id);
    }

    [Fact]
    public async Task Retention_reopens_expired_snoozes_without_purging_snoozed_messages()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var expiredMessage = Message("Expired snooze");
        var activeMessage = Message("Active snooze");
        fixture.Db.EmailMessages.AddRange(expiredMessage, activeMessage);
        fixture.Db.TrackedItems.AddRange(
            new TrackedItem
            {
                EmailMessageId = expiredMessage.Id,
                Status = TrackedItemStatus.Snoozed,
                CreatedAt = now.AddMonths(-2),
                UpdatedAt = now.AddMonths(-2),
                SnoozedUntil = now.AddMinutes(-1)
            },
            new TrackedItem
            {
                EmailMessageId = activeMessage.Id,
                Status = TrackedItemStatus.Snoozed,
                CreatedAt = now.AddMonths(-2),
                UpdatedAt = now.AddMonths(-2),
                SnoozedUntil = now.AddDays(1)
            });
        await fixture.Db.SaveChangesAsync();
        var maintenance = new LocalDataMaintenanceService(fixture.DbContextFactory, fixture.AccountSettings, fixture.LiveReport);

        var result = await maintenance.ApplyRetentionAsync(CancellationToken.None);

        Assert.Equal(0, result.DeletedMessages);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(2, await fixture.Db.EmailMessages.CountAsync());
        var items = await fixture.Db.TrackedItems.ToDictionaryAsync(x => x.EmailMessageId);
        Assert.Equal(TrackedItemStatus.Open, items[expiredMessage.Id].Status);
        Assert.Null(items[expiredMessage.Id].SnoozedUntil);
        Assert.Equal(TrackedItemStatus.Snoozed, items[activeMessage.Id].Status);
        Assert.Equal(now.AddDays(1), items[activeMessage.Id].SnoozedUntil);
    }

    private static FetchedEmail Email(long uid = 1, long uidValidity = 1, string bodyHash = "body", string flagsHash = "flags")
    {
        var message = Message("Please reply")
            .WithProvider(uidValidity, uid);

        return new FetchedEmail(
            TestFixture.AccountId,
            "Inbox",
            uidValidity,
            uid,
            message,
            "subject",
            bodyHash,
            flagsHash);
    }

    private static EmailMessage Message(string body) => new()
    {
        AccountId = TestFixture.AccountId,
        Folder = "Inbox",
        ProviderMessageId = Guid.NewGuid().ToString("N"),
        MessageIdHeader = Guid.NewGuid().ToString("N"),
        SenderName = "Sender",
        SenderEmail = "sender@example.com",
        Subject = "Action required",
        ReceivedAt = DateTimeOffset.Now,
        Snippet = body,
        NormalizedBody = body,
        OriginalTextBody = body,
        ThreadKey = Guid.NewGuid().ToString("N"),
        LastScannedAt = DateTimeOffset.UtcNow
    };

    private static ClassificationResult Actionable(
        string summary,
        DateTimeOffset? deadline = null,
        bool requiresReply = false,
        bool escalation = false) =>
        new()
        {
            IsActionable = true,
            UserActionDeadline = deadline,
            UserReplyRequired = requiresReply,
            UserObligationMayEscalate = escalation,
            ActionSummary = summary,
            Reason = "The message requires user action.",
            Tags = ["reply"]
        };

    private static ClassificationResult NonActionableNewsletter() => new()
    {
        IsActionable = false,
        UserReplyRequired = false,
        UserObligationMayEscalate = false,
        UserActionDeadline = null,
        ActionSummary = "AI Roundup Notification",
        Reason = "No action required",
        Tags = ["newsletter"]
    };

    private sealed class TestFixture : IAsyncDisposable
    {
        public static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private readonly SqliteConnection _connection;

        private TestFixture(
            SqliteConnection connection,
            VigiloDbContext db,
            TestDbContextFactory dbContextFactory,
            StubClassifier classifier,
            LiveReportService liveReport,
            LiveReportChangeNotifier liveReportChangeNotifier,
            EmailProcessingService processor)
        {
            _connection = connection;
            Db = db;
            DbContextFactory = dbContextFactory;
            Classifier = classifier;
            LiveReport = liveReport;
            LiveReportChangeNotifier = liveReportChangeNotifier;
            Processor = processor;
            AccountSettings = new DbAccountSettings(db);
        }

        public VigiloDbContext Db { get; }
        public TestDbContextFactory DbContextFactory { get; }
        public StubClassifier Classifier { get; }
        public LiveReportService LiveReport { get; }
        public LiveReportChangeNotifier LiveReportChangeNotifier { get; }
        public EmailProcessingService Processor { get; }
        public IAccountSettingsService AccountSettings { get; }

        public static async Task<TestFixture> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VigiloDbContext>().UseSqlite(connection).Options;
            var db = new VigiloDbContext(options);
            var dbContextFactory = new TestDbContextFactory(options);
            await db.Database.EnsureCreatedAsync();
            db.Accounts.Add(new EmailAccount { Id = AccountId, EmailAddress = "me@yahoo.com", Username = "me@yahoo.com" });
            await db.SaveChangesAsync();

            var classifier = new StubClassifier();
            var liveReportChangeNotifier = new LiveReportChangeNotifier();
            var liveReport = new LiveReportService(dbContextFactory, liveReportChangeNotifier);
            var processor = new EmailProcessingService(
                dbContextFactory,
                classifier,
                liveReport,
                new RecordingNotificationService(),
                NullLogger<EmailProcessingService>.Instance);
            return new TestFixture(connection, db, dbContextFactory, classifier, liveReport, liveReportChangeNotifier, processor);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class StubClassifier : IMessageClassifier
    {
        private readonly Queue<ClassificationResult> _results = new();

        public int Calls { get; private set; }

        public void Enqueue(ClassificationResult result) => _results.Enqueue(result);

        public Task<ClassificationResult> ClassifyAsync(
            EmailMessage message,
            CancellationToken cancellationToken,
            IProgress<string>? progress = null)
        {
            Calls++;
            return Task.FromResult(_results.Count == 0 ? Actionable("Default", requiresReply: true) : _results.Dequeue());
        }
    }

    private sealed class ThrowingClassifier : IMessageClassifier
    {
        public Task<ClassificationResult> ClassifyAsync(
            EmailMessage message,
            CancellationToken cancellationToken,
            IProgress<string>? progress = null) =>
            Task.FromResult(ClassificationResult.UnavailableFallback("Local model is unavailable or failed during inference."));
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public Task NotifyAsync(NotificationKind kind, TrackedItem item, EmailMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DenyApprovalService : IUserApprovalService
    {
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class DbAccountSettings(VigiloDbContext dbContext) : IAccountSettingsService
    {
        public async Task<IReadOnlyList<EmailAccount>> GetAccountsAsync(CancellationToken cancellationToken) =>
            (await dbContext.Accounts.ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt)
            .ToList();

        public async Task<EmailAccount?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
            await dbContext.Accounts.FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken);

        public async Task<EmailAccount> GetOrCreateDefaultAccountAsync(CancellationToken cancellationToken) =>
            await dbContext.Accounts.FirstAsync(cancellationToken);

        public Task SaveAccountAsync(EmailAccount account, CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);

        public async Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken)
        {
            var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken);
            if (account is not null)
            {
                dbContext.Accounts.Remove(account);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        public Task<string?> GetPasswordAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<bool> HasPasswordAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task SavePasswordAsync(Guid accountId, string password, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeletePasswordAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

file static class EmailMessageTestExtensions
{
    public static EmailMessage WithBody(this EmailMessage message, string body)
    {
        message.NormalizedBody = body;
        message.OriginalTextBody = body;
        message.Snippet = body;
        return message;
    }

    public static EmailMessage WithProvider(this EmailMessage message, long uidValidity, long uid)
    {
        message.ProviderMessageId = $"Inbox:{uidValidity}:{uid}";
        return message;
    }
}
