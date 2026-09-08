using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vigilo.Core;
using Vigilo.Storage;

namespace Vigilo.Tests;

public sealed class SenderRuleTests
{
    [Fact]
    public async Task Rule_service_normalizes_upserts_and_removes_account_scoped_sender()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Rules.SetRuleAsync(Fixture.AccountId, "  Person@Example.com ", SenderRuleKind.Vip, CancellationToken.None);
        await fixture.Rules.SetRuleAsync(Fixture.AccountId, "person@example.com", SenderRuleKind.Ignored, CancellationToken.None);

        var stored = await fixture.Db.SenderRules.SingleAsync();
        Assert.Equal("PERSON@EXAMPLE.COM", stored.NormalizedSenderEmail);
        Assert.Equal(SenderRuleKind.Ignored, stored.Kind);
        Assert.Equal(
            SenderRuleKind.Ignored,
            await fixture.Rules.GetRuleKindAsync(Fixture.AccountId, "PERSON@example.com", CancellationToken.None));

        await fixture.Rules.RemoveRuleAsync(stored.Id, CancellationToken.None);

        Assert.Empty(await fixture.Rules.GetRulesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Ignored_sender_is_ledgered_without_storage_or_model_processing()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Rules.SetRuleAsync(Fixture.AccountId, "ignored@example.com", SenderRuleKind.Ignored, CancellationToken.None);

        var outcome = await fixture.Processor.ProcessFetchedEmailAsync(
            Email("ignored@example.com", uid: 10),
            CancellationToken.None);

        Assert.Equal(EmailProcessingOutcome.Skipped, outcome);
        Assert.Equal(0, fixture.Classifier.Calls);
        Assert.Empty(fixture.Db.EmailMessages);
        Assert.Empty(fixture.Db.TrackedItems);
        var ledger = await fixture.Db.ProcessedEmails.SingleAsync();
        Assert.Equal(ProcessingStatus.Skipped, ledger.ProcessingStatus);
        Assert.Null(ledger.EmailMessageId);
    }

    [Fact]
    public async Task Vip_sender_is_always_tracked_even_when_classification_is_non_actionable()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Rules.SetRuleAsync(Fixture.AccountId, "vip@example.com", SenderRuleKind.Vip, CancellationToken.None);
        fixture.Classifier.Result = new ClassificationResult
        {
            MessageType = ClassificationMessageType.Newsletter,
            HasUserSpecificObligation = false,
            IsActionable = false,
            ActionSummary = "Weekly update",
            Reason = "No action requested"
        };

        var outcome = await fixture.Processor.ProcessFetchedEmailAsync(
            Email("vip@example.com", uid: 11),
            CancellationToken.None);

        Assert.Equal(EmailProcessingOutcome.Classified, outcome);
        Assert.Equal(1, fixture.Classifier.Calls);
        var item = await fixture.Db.TrackedItems.SingleAsync();
        Assert.Equal(TrackedItemStatus.Open, item.Status);
        Assert.Equal(TrackedItemCategories.Upcoming, TrackedItemCategories.Classify(item, DateTimeOffset.Now));
        Assert.Contains("VIP sender rule", item.Reason);
    }

    [Fact]
    public async Task Vip_sender_promotion_is_routed_to_needs_review_instead_of_commercial_offers()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Rules.SetRuleAsync(Fixture.AccountId, "vip@example.com", SenderRuleKind.Vip, CancellationToken.None);
        fixture.Classifier.Result = new ClassificationResult
        {
            MessageType = ClassificationMessageType.Promotion,
            HasUserSpecificObligation = false,
            IsActionable = false,
            ActionSummary = "Limited-time offer",
            Reason = "Promotional content"
        };

        var outcome = await fixture.Processor.ProcessFetchedEmailAsync(
            Email("vip@example.com", uid: 13),
            CancellationToken.None);

        Assert.Equal(EmailProcessingOutcome.NeedsReview, outcome);
        var item = await fixture.Db.TrackedItems.SingleAsync();
        Assert.Equal(TrackedItemStatus.NeedsReview, item.Status);
        Assert.Equal(TrackedItemCategories.NeedsReview, TrackedItemCategories.Classify(item, DateTimeOffset.Now));
        Assert.Contains("commercial offers from this sender require review", item.Reason);
    }

    [Fact]
    public async Task Vip_sender_newsletter_with_a_dated_offer_is_routed_to_needs_review()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Rules.SetRuleAsync(Fixture.AccountId, "vip@example.com", SenderRuleKind.Vip, CancellationToken.None);
        fixture.Classifier.Result = new ClassificationResult
        {
            MessageType = ClassificationMessageType.Newsletter,
            HasUserSpecificObligation = false,
            IsActionable = false,
            UserActionDeadline = DateTimeOffset.Now.AddDays(1),
            ActionSummary = "",
            Reason = "The newsletter advertises a workshop with a date."
        };

        var outcome = await fixture.Processor.ProcessFetchedEmailAsync(
            Email("vip@example.com", uid: 14),
            CancellationToken.None);

        Assert.Equal(EmailProcessingOutcome.NeedsReview, outcome);
        var item = await fixture.Db.TrackedItems.SingleAsync();
        Assert.Equal(TrackedItemStatus.NeedsReview, item.Status);
        Assert.Equal(TrackedItemCategories.NeedsReview, TrackedItemCategories.Classify(item, DateTimeOffset.Now));
        Assert.Contains("commercial offers from this sender require review", item.Reason);
    }

    [Fact]
    public async Task Vip_sender_is_not_shown_to_the_user_when_the_model_is_unavailable()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Rules.SetRuleAsync(Fixture.AccountId, "vip@example.com", SenderRuleKind.Vip, CancellationToken.None);
        fixture.Classifier.Result = ClassificationResult.UnavailableFallback("Model offline");

        var outcome = await fixture.Processor.ProcessFetchedEmailAsync(
            Email("vip@example.com", uid: 12),
            CancellationToken.None);

        Assert.Equal(EmailProcessingOutcome.Failed, outcome);
        Assert.Equal(ProcessingStatus.Failed, (await fixture.Db.ProcessedEmails.SingleAsync()).ProcessingStatus);
        Assert.Empty(fixture.Db.TrackedItems);
    }

    private static FetchedEmail Email(string sender, long uid)
    {
        var message = new EmailMessage
        {
            AccountId = Fixture.AccountId,
            Folder = "Inbox",
            ProviderMessageId = $"Inbox:1:{uid}",
            MessageIdHeader = $"<{uid}@example.com>",
            SenderName = sender.Split('@')[0],
            SenderEmail = sender,
            Subject = "Sender rule test",
            ReceivedAt = DateTimeOffset.UtcNow,
            Snippet = "Message body",
            NormalizedBody = "Message body",
            OriginalTextBody = "Message body",
            ThreadKey = $"thread-{uid}",
            LastScannedAt = DateTimeOffset.UtcNow
        };
        return new FetchedEmail(Fixture.AccountId, "Inbox", 1, uid, message, "subject", "body", "flags");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public static readonly Guid AccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private readonly SqliteConnection _connection;

        private Fixture(
            SqliteConnection connection,
            VigiloDbContext db,
            SenderRuleService rules,
            StubClassifier classifier,
            EmailProcessingService processor)
        {
            _connection = connection;
            Db = db;
            Rules = rules;
            Classifier = classifier;
            Processor = processor;
        }

        public VigiloDbContext Db { get; }
        public SenderRuleService Rules { get; }
        public StubClassifier Classifier { get; }
        public EmailProcessingService Processor { get; }

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
                Name = "Rules account",
                EmailAddress = "owner@example.com",
                Username = "owner@example.com"
            });
            await db.SaveChangesAsync();

            var rules = new SenderRuleService(dbContextFactory);
            var classifier = new StubClassifier();
            var notifier = new LiveReportChangeNotifier();
            var report = new LiveReportService(dbContextFactory, notifier, senderRuleService: rules);
            var processor = new EmailProcessingService(
                dbContextFactory,
                classifier,
                report,
                new NoOpNotificationService(),
                NullLogger<EmailProcessingService>.Instance,
                senderRuleService: rules);
            return new Fixture(connection, db, rules, classifier, processor);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class StubClassifier : IMessageClassifier
    {
        public ClassificationResult Result { get; set; } = new();
        public int Calls { get; private set; }

        public Task<ClassificationResult> ClassifyAsync(
            EmailMessage message,
            CancellationToken cancellationToken,
            IProgress<string>? progress = null)
        {
            Calls++;
            return Task.FromResult(Result);
        }
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
