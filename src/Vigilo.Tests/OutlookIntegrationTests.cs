using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vigilo.Core;
using Vigilo.Storage;

namespace Vigilo.Tests;

public sealed class OutlookIntegrationTests
{
    private readonly OutlookCategoryMapper _mapper = new();

    [Theory]
    [InlineData(0, "Due today")]
    [InlineData(1, "Upcoming")]
    [InlineData(2, "Waiting for my reply")]
    [InlineData(3, "Needs review")]
    [InlineData(4, "Snoozed")]
    [InlineData(5, "Commercial offers")]
    [InlineData(6, "Dismissed")]
    [InlineData(7, "Done")]
    public void Mapper_matches_every_ui_section(int kind, string expected)
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var item = new TrackedItem { Status = TrackedItemStatus.Open };
        switch (kind)
        {
            case 0: item.Deadline = now; break;
            case 1: item.Deadline = now.AddDays(3); break;
            case 2: item.RequiresReply = true; break;
            case 3: item.Status = TrackedItemStatus.NeedsReview; break;
            case 4: item.Status = TrackedItemStatus.Snoozed; break;
            case 5: item.MessageType = ClassificationMessageType.Promotion; break;
            case 6: item.Status = TrackedItemStatus.Dismissed; break;
            case 7: item.Status = TrackedItemStatus.Done; break;
        }
        var categories = _mapper.Map(item, now);
        Assert.Single(categories);
        Assert.Contains(expected, categories);
    }

    [Fact]
    public void Mapper_keeps_expired_items_in_their_workflow_category()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var item = new TrackedItem
        {
            Status = TrackedItemStatus.Expired,
            Deadline = now.AddMinutes(-1),
            RequiresReply = true
        };

        Assert.Equal(
            [TrackedItemCategories.WaitingForMyReply],
            _mapper.Map(item, now));
        Assert.True(OutlookManagedCategories.IsManaged("Expired"));
        Assert.False(OutlookManagedCategories.IsDesired("Expired"));
    }

    [Fact]
    public void Mapper_treats_escalation_as_a_badge_that_never_overrides_the_section()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var overdue = new TrackedItem { Status = TrackedItemStatus.Open, IsEscalation = true, Deadline = now };
        Assert.Equal([TrackedItemCategories.DueToday], _mapper.Map(overdue, now));

        var undated = new TrackedItem { Status = TrackedItemStatus.Open, IsEscalation = true };
        Assert.Equal([TrackedItemCategories.Upcoming], _mapper.Map(undated, now));
    }

    [Fact]
    public void Mapper_uses_the_same_exclusive_precedence_as_the_ui()
    {
        var item = new TrackedItem
        {
            Status = TrackedItemStatus.Open,
            MessageType = ClassificationMessageType.Promotion,
            IsEscalation = true,
            RequiresReply = true
        };
        var categories = _mapper.Map(item, DateTimeOffset.Now);
        Assert.Single(categories);
        Assert.Contains(TrackedItemCategories.CommercialOffers, categories);
    }

    [Theory]
    [InlineData("Commercial offers", true)]
    [InlineData("commercial OFFERS", true)]
    [InlineData("Commercial offer", false)]
    [InlineData("Vigilo - Commercial offers", false)]
    public void Registry_recognizes_only_exact_names(string value, bool expected) =>
        Assert.Equal(expected, OutlookManagedCategories.IsManaged(value));

    [Fact]
    public void Merge_preserves_user_categories_removes_obsolete_managed_and_prevents_duplicates()
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TrackedItemCategories.CommercialOffers
        };
        var result = OutlookCategorySet.Merge("Personal;Action required;reply REQUIRED;Personal", desired, ";");
        Assert.True(result.IsChanged);
        Assert.Equal(["Commercial offers", "Personal"], result.Categories.OrderBy(x => x).ToArray());

        var second = OutlookCategorySet.Merge(result.Serialized, desired, ";");
        Assert.False(second.IsChanged);
    }

    [Fact]
    public void Merge_uses_locale_separator_without_splitting_other_punctuation()
    {
        var result = OutlookCategorySet.Merge("Family, friends;Review required", new HashSet<string>(), ";");
        Assert.Equal("Family, friends", result.Serialized);
    }

    [Theory]
    [InlineData(" abc@example.test ", "<abc@example.test>")]
    [InlineData("<ABC@example.test>", "<ABC@example.test>")]
    [InlineData("<>", null)]
    [InlineData(" ", null)]
    public void Message_id_is_normalized(string value, string? expected) => Assert.Equal(expected, OutlookMessageId.Normalize(value));

    [Fact]
    public void Message_id_comparison_is_case_insensitive() =>
        Assert.True(OutlookMessageId.Equals("<ABC@example.test>", "abc@EXAMPLE.test"));

    [Fact]
    public void Fast_path_requires_mail_store_and_message_id_validation()
    {
        Assert.True(OutlookMatchPolicy.IsValidFastPath(true, "store", "store", "<id@test>", "id@test"));
        Assert.False(OutlookMatchPolicy.IsValidFastPath(false, "store", "store", "id@test", "id@test"));
        Assert.False(OutlookMatchPolicy.IsValidFastPath(true, "other", "store", "id@test", "id@test"));
        Assert.False(OutlookMatchPolicy.IsValidFastPath(true, "store", "store", "id@test", "different@test"));
    }

    [Fact]
    public void Match_policy_refuses_ambiguity_and_reports_not_found()
    {
        Assert.Equal(OutlookMessageMatchStatus.NotFound, OutlookMatchPolicy.Select([]).Status);
        var item = new OutlookItemReference("entry", "store", null, OutlookMatchMethod.InternetMessageId);
        Assert.Equal(OutlookMessageMatchStatus.Found, OutlookMatchPolicy.Select([item]).Status);
        Assert.Equal(OutlookMessageMatchStatus.Ambiguous, OutlookMatchPolicy.Select([item, item with { EntryId = "other" }]).Status);
    }

    [Theory]
    [InlineData(OutlookErrorCodes.Busy, true, 1, OutlookSyncStatus.RetryScheduled, true)]
    [InlineData(OutlookErrorCodes.Busy, true, 8, OutlookSyncStatus.PermanentFailure, false)]
    [InlineData(OutlookErrorCodes.MessageAmbiguous, false, 1, OutlookSyncStatus.AmbiguousMatch, false)]
    [InlineData(OutlookErrorCodes.NotRunning, true, 20, OutlookSyncStatus.OutlookNotRunning, true)]
    public void Retry_policy_is_bounded_except_when_outlook_is_closed(
        string code, bool transient, int attempts, OutlookSyncStatus status, bool shouldRetry)
    {
        var result = OutlookRetryPolicy.Classify(code, transient, attempts);
        Assert.Equal(status, result.Status);
        Assert.Equal(shouldRetry, result.ShouldRetry);
    }

    [Fact]
    public async Task Queue_supersedes_older_desired_state_and_recovers_stale_work()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VigiloDbContext>().UseSqlite(connection).Options;
        await using var db = new VigiloDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var account = new EmailAccount { EmailAddress = "owner@example.com", Username = "owner@example.com" };
        var message = new EmailMessage { AccountId = account.Id, ProviderMessageId = "provider-1" };
        db.AddRange(account, message);
        await db.SaveChangesAsync();
        var store = new OutlookSyncStore(new TestDbContextFactory(options), _mapper);

        await store.EnqueueOrSupersedeAsync(message.Id, new HashSet<string> { TrackedItemCategories.Upcoming }, CancellationToken.None);
        await db.SaveChangesAsync();
        await store.EnqueueOrSupersedeAsync(message.Id, new HashSet<string> { TrackedItemCategories.Done }, CancellationToken.None);
        await db.SaveChangesAsync();

        var operations = (await db.OutlookCategorySyncOperations.ToListAsync()).OrderBy(x => x.CreatedAtUtc).ToList();
        Assert.Equal(OutlookSyncStatus.Superseded, operations[0].Status);
        Assert.Equal(OutlookSyncStatus.Pending, operations[1].Status);
        await store.MarkInProgressAsync(operations[1].Id, CancellationToken.None);
        var entity = await db.OutlookCategorySyncOperations.FindAsync(operations[1].Id);
        entity!.UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();

        Assert.Equal(1, await store.RecoverStaleInProgressAsync(DateTimeOffset.UtcNow.AddMinutes(-10), CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(
            OutlookSyncStatus.RetryScheduled,
            (await db.OutlookCategorySyncOperations.FindAsync(operations[1].Id))!.Status);
    }
}
