using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vigilo.Core;
using Vigilo.Storage;

namespace Vigilo.Tests;

public sealed class ReclassificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 15, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void Apply_moves_needs_review_item_into_each_target_section()
    {
        var targets = new (string Section, ClassificationMessageType MessageType)[]
        {
            (TrackedItemCategories.DueToday, ClassificationMessageType.Personal),
            (TrackedItemCategories.Upcoming, ClassificationMessageType.Personal),
            (TrackedItemCategories.WaitingForMyReply, ClassificationMessageType.Personal),
            (TrackedItemCategories.CommercialOffers, ClassificationMessageType.Promotion)
        };

        foreach (var (section, messageType) in targets)
        {
            var item = NeedsReviewItem();
            TrackedItemReclassification.Apply(item, section, Now);

            Assert.Equal(TrackedItemStatus.Open, item.Status);
            Assert.Equal(section, item.SectionOverride);
            Assert.Null(item.CompletedAt);
            Assert.Null(item.DismissedAt);
            Assert.Null(item.SnoozedUntil);
            Assert.Equal(section, TrackedItemCategories.Classify(item, Now));
            if (section == TrackedItemCategories.CommercialOffers)
            {
                Assert.Equal(messageType, item.MessageType);
            }
        }
    }

    [Fact]
    public void Apply_for_due_today_keeps_today_and_overdue_deadlines_and_replaces_future_ones()
    {
        var currentDeadline = new DateTimeOffset(Now.Date.AddHours(9), Now.Offset);
        var kept = NeedsReviewItem();
        kept.Deadline = currentDeadline;
        TrackedItemReclassification.Apply(kept, TrackedItemCategories.DueToday, Now);
        Assert.Equal(currentDeadline, kept.Deadline);

        var overdue = NeedsReviewItem();
        overdue.Deadline = Now.AddDays(-3);
        TrackedItemReclassification.Apply(overdue, TrackedItemCategories.DueToday, Now);
        Assert.Equal(Now.AddDays(-3), overdue.Deadline);
        Assert.Equal(TrackedItemCategories.DueToday, TrackedItemCategories.Classify(overdue, Now));

        var future = NeedsReviewItem();
        future.Deadline = Now.AddDays(4);
        TrackedItemReclassification.Apply(future, TrackedItemCategories.DueToday, Now);
        Assert.Equal(Now.Date, future.Deadline!.Value.ToOffset(Now.Offset).Date);
        Assert.True(future.Deadline.Value > Now);
    }

    [Fact]
    public void Apply_for_upcoming_keeps_future_and_undated_deadlines_and_moves_stale_ones()
    {
        var future = new DateTimeOffset(Now.Date.AddDays(9), Now.Offset);
        var kept = NeedsReviewItem();
        kept.Deadline = future;
        TrackedItemReclassification.Apply(kept, TrackedItemCategories.Upcoming, Now);
        Assert.Equal(future, kept.Deadline);

        var undated = NeedsReviewItem();
        undated.Deadline = null;
        TrackedItemReclassification.Apply(undated, TrackedItemCategories.Upcoming, Now);
        Assert.Null(undated.Deadline);

        var moved = NeedsReviewItem();
        moved.Deadline = Now.AddDays(-1);
        TrackedItemReclassification.Apply(moved, TrackedItemCategories.Upcoming, Now);
        Assert.Equal(Now.Date.AddDays(1), moved.Deadline!.Value.ToOffset(Now.Offset).Date);
        Assert.Equal(TrackedItemCategories.Upcoming, TrackedItemCategories.Classify(moved, Now));
    }

    [Fact]
    public void Apply_computes_days_in_now_offset_rather_than_the_machine_timezone()
    {
        // Reproduces the CI failure mode: now carries an offset that differs from the
        // machine's UTC offset, so any LocalDateTime re-basing or Kind=Local constructor
        // input throws or shifts the day. The chosen offset is guaranteed to differ from
        // whatever timezone the test machine happens to run in.
        var machineOffset = DateTimeOffset.Now.Offset;
        var offset = machineOffset == TimeSpan.FromHours(2) ? TimeSpan.FromHours(9) : TimeSpan.FromHours(2);
        var now = new DateTimeOffset(2026, 9, 5, 23, 30, 0, offset);

        var future = NeedsReviewItem();
        future.Deadline = now.AddDays(4);
        TrackedItemReclassification.Apply(future, TrackedItemCategories.DueToday, now);
        Assert.Equal(now.Date, future.Deadline!.Value.ToOffset(offset).Date);
        Assert.Equal(TrackedItemCategories.DueToday, TrackedItemCategories.Classify(future, now));

        var stale = NeedsReviewItem();
        stale.Deadline = now.AddDays(-1);
        TrackedItemReclassification.Apply(stale, TrackedItemCategories.Upcoming, now);
        Assert.Equal(now.Date.AddDays(1), stale.Deadline!.Value.ToOffset(offset).Date);
        Assert.Equal(TrackedItemCategories.Upcoming, TrackedItemCategories.Classify(stale, now));
    }

    [Fact]
    public void Apply_rejects_sections_that_are_not_reclassification_targets()
    {
        foreach (var section in new[]
                 {
                     TrackedItemCategories.Done,
                     TrackedItemCategories.Dismissed,
                     TrackedItemCategories.Snoozed,
                     TrackedItemCategories.NeedsReview,
                     "Possible escalation",
                     "Open",
                     "Not a section"
                 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TrackedItemReclassification.Apply(NeedsReviewItem(), section, Now));
        }
    }

    [Fact]
    public void Classify_prefers_the_user_override_over_derived_sections_until_terminal_status()
    {
        var item = NeedsReviewItem();
        item.SectionOverride = TrackedItemCategories.WaitingForMyReply;

        item.MessageType = ClassificationMessageType.Promotion;
        item.IsEscalation = true;
        item.RequiresReply = true;
        item.Deadline = Now;
        Assert.Equal(TrackedItemCategories.WaitingForMyReply, TrackedItemCategories.Classify(item, Now));

        item.Status = TrackedItemStatus.Done;
        Assert.Equal(TrackedItemCategories.Done, TrackedItemCategories.Classify(item, Now));

        item.Status = TrackedItemStatus.Dismissed;
        Assert.Equal(TrackedItemCategories.Dismissed, TrackedItemCategories.Classify(item, Now));
    }

    [Fact]
    public async Task Reclassify_service_persists_the_choice_and_moves_the_visible_section()
    {
        await using var fixture = await Fixture.CreateAsync();
        var trackedItemId = await fixture.SeedTrackedItemAsync();

        await fixture.Report.ReclassifyAsync(trackedItemId, TrackedItemCategories.Upcoming, CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();

        var stored = await fixture.Db.TrackedItems.SingleAsync(x => x.Id == trackedItemId);
        Assert.Equal(TrackedItemCategories.Upcoming, stored.SectionOverride);
        Assert.Equal(TrackedItemStatus.Open, stored.Status);
        var items = await fixture.Report.GetItemsAsync(CancellationToken.None);
        var view = items.Single(x => x.Id == trackedItemId);
        Assert.Equal(TrackedItemCategories.Upcoming, view.Section);
    }

    [Fact]
    public async Task Snooze_clears_a_previous_reclassification()
    {
        await using var fixture = await Fixture.CreateAsync();
        var trackedItemId = await fixture.SeedTrackedItemAsync();

        await fixture.Report.ReclassifyAsync(trackedItemId, TrackedItemCategories.DueToday, CancellationToken.None);
        await fixture.Report.SnoozeAsync(trackedItemId, DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();

        var stored = await fixture.Db.TrackedItems.SingleAsync(x => x.Id == trackedItemId);
        Assert.Null(stored.SectionOverride);
        Assert.Equal(TrackedItemStatus.Snoozed, stored.Status);
    }

    [Fact]
    public async Task Reclassified_item_survives_a_classifier_rerun_of_the_same_message()
    {
        await using var fixture = await Fixture.CreateAsync();
        var trackedItemId = await fixture.SeedTrackedItemAsync();

        await fixture.Report.ReclassifyAsync(trackedItemId, TrackedItemCategories.Upcoming, CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();

        var stored = await fixture.Db.TrackedItems.SingleAsync(x => x.Id == trackedItemId);
        stored.Status = TrackedItemStatus.Open;
        await fixture.Db.SaveChangesAsync();
        var email = await fixture.Db.EmailMessages.SingleAsync();
        // An edited body forces the processor to re-run the classifier for this message.
        var outcome = await fixture.Processor.ProcessFetchedEmailAsync(
            Fixture.Email(email.SenderEmail, uid: 10, bodyHash: "body-edited"),
            CancellationToken.None);

        Assert.Equal(EmailProcessingOutcome.NeedsReview, outcome);
        fixture.Db.ChangeTracker.Clear();
        var reloaded = await fixture.Db.TrackedItems.SingleAsync(x => x.Id == trackedItemId);
        Assert.Equal(TrackedItemCategories.Upcoming, reloaded.SectionOverride);
        Assert.Equal(
            TrackedItemCategories.Upcoming,
            TrackedItemCategories.Classify(reloaded, DateTimeOffset.Now));
    }

    private static TrackedItem NeedsReviewItem() => new()
    {
        Status = TrackedItemStatus.NeedsReview,
        IsEscalation = true,
        RequiresReply = true,
        MessageType = ClassificationMessageType.Promotion,
        Deadline = Now.AddDays(-1),
        CreatedAt = Now.AddDays(-1),
        UpdatedAt = Now.AddDays(-1),
        CompletedAt = null,
        DismissedAt = null,
        SnoozedUntil = Now.AddDays(1),
        ThreadKey = "thread",
        ActionSummary = "Review this email",
        Reason = "Uncertain classification",
        Tags = ""
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, VigiloDbContext db, LiveReportService report, EmailProcessingService processor, StubClassifier classifier)
        {
            _connection = connection;
            Db = db;
            Report = report;
            Processor = processor;
            Classifier = classifier;
        }

        public VigiloDbContext Db { get; }
        public LiveReportService Report { get; }
        public EmailProcessingService Processor { get; }
        public StubClassifier Classifier { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<VigiloDbContext>().UseSqlite(connection).Options;
            var db = new VigiloDbContext(options);
            var dbContextFactory = new TestDbContextFactory(options);
            await db.Database.EnsureCreatedAsync();
            db.Accounts.Add(new EmailAccount
            {
                Id = AccountId,
                Name = "Reclassification account",
                EmailAddress = "owner@example.com",
                Username = "owner@example.com"
            });
            await db.SaveChangesAsync();

            var notifier = new LiveReportChangeNotifier();
            var classifier = new StubClassifier
            {
                Result = new ClassificationResult
                {
                    NeedsReview = true,
                    Reason = "Still uncertain"
                }
            };
            var report = new LiveReportService(dbContextFactory, notifier);
            var processor = new EmailProcessingService(
                dbContextFactory,
                classifier,
                report,
                new NoOpNotificationService(),
                NullLogger<EmailProcessingService>.Instance);
            return new Fixture(connection, db, report, processor, classifier);
        }

        public async Task<Guid> SeedTrackedItemAsync()
        {
            var message = new EmailMessage
            {
                AccountId = AccountId,
                Folder = "INBOX",
                ProviderMessageId = "provider-1",
                SenderEmail = "sender@example.com",
                Subject = "Uncertain email",
                ReceivedAt = Now,
                LastScannedAt = Now,
                ThreadKey = "thread"
            };
            Db.EmailMessages.Add(message);
            var item = NeedsReviewItem();
            item.EmailMessageId = message.Id;
            Db.TrackedItems.Add(item);
            Db.ProcessedEmails.Add(new ProcessedEmail
            {
                AccountId = AccountId,
                Folder = "INBOX",
                UidValidity = 1,
                ImapUid = 10,
                MessageIdHeader = message.MessageIdHeader,
                SubjectHash = "subject",
                BodyHash = "body",
                FlagsHash = "flags",
                ReceivedAt = Now,
                FirstSeenAt = Now,
                LastSeenAt = Now,
                ProcessingStatus = ProcessingStatus.Classified,
                ClassificationVersion = ClassificationMetadata.CurrentVersion,
                EmailMessageId = message.Id
            });
            await Db.SaveChangesAsync();
            return item.Id;
        }

        public static FetchedEmail Email(string senderEmail, long uid, string bodyHash = "body") => new(
            AccountId,
            "INBOX",
            1,
            uid,
            new EmailMessage
            {
                AccountId = AccountId,
                Folder = "INBOX",
                ProviderMessageId = $"provider-{uid}",
                SenderEmail = senderEmail,
                Subject = "Uncertain email",
                ReceivedAt = Now,
                LastScannedAt = Now,
                ThreadKey = "thread"
            },
            "subject",
            bodyHash,
            "flags");

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class StubClassifier : IMessageClassifier
    {
        public ClassificationResult Result { get; set; } = new();

        public Task<ClassificationResult> ClassifyAsync(
            EmailMessage message,
            CancellationToken cancellationToken,
            IProgress<string>? progress = null) => Task.FromResult(Result);
    }

    private sealed class NoOpNotificationService : INotificationService
    {
        public Task NotifyAsync(
            NotificationKind kind,
            TrackedItem item,
            EmailMessage message,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
