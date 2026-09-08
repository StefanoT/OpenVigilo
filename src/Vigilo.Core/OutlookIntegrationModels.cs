using System.Text.Json;

namespace Vigilo.Core;

public static class OutlookManagedCategories
{
    public static IReadOnlyList<string> Ordered { get; } = TrackedItemCategories.All;

    private static IReadOnlySet<string> Legacy { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Action required", "Reply required", "Deadline", "Overdue", "Escalation",
        "Waiting for others", "Informational", "Review required", "Expired",
        "Possible escalation", "Due soon", "Open"
    };

    // These required generic names cannot encode ownership. Every exact case-insensitive match is therefore
    // managed, even when the user or another application originally assigned it; similar names remain untouched.
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(Ordered, StringComparer.OrdinalIgnoreCase);

    public static bool IsManaged(string? category) =>
        !string.IsNullOrWhiteSpace(category) && (All.Contains(category.Trim()) || Legacy.Contains(category.Trim()));

    public static bool IsDesired(string? category) =>
        !string.IsNullOrWhiteSpace(category) && All.Contains(category.Trim());
}

public static class TrackedItemCategories
{
    public const string DueToday = "Due today";
    public const string Upcoming = "Upcoming";
    public const string WaitingForMyReply = "Waiting for my reply";
    public const string NeedsReview = "Needs review";
    public const string Snoozed = "Snoozed";
    public const string CommercialOffers = "Commercial offers";
    public const string Dismissed = "Dismissed";
    public const string Done = "Done";

    public static IReadOnlyList<string> All { get; } =
    [
        DueToday, WaitingForMyReply, NeedsReview, Upcoming, CommercialOffers,
        Snoozed, Dismissed, Done
    ];

    public static string Classify(TrackedItem item, DateTimeOffset now)
    {
        // Day boundaries are evaluated in the offset carried by now, never the machine
        // timezone: LocalDateTime would silently re-base dates to the machine and makes the
        // DateTimeOffset constructor reject the mismatch when the two differ (CI, schedulers).
        var today = now.Date;
        if (item.Status == TrackedItemStatus.Done) return Done;
        if (item.Status == TrackedItemStatus.Dismissed) return Dismissed;

        // A user re-classification wins over every derived section. It is cleared when the item is
        // snoozed, so Snoozed is intentionally not reachable while an override is set. Overrides
        // written by older builds are ignored so removed section names cannot reappear as groups.
        if (!string.IsNullOrWhiteSpace(item.SectionOverride) && All.Contains(item.SectionOverride))
        {
            return item.SectionOverride;
        }

        if (item.Status == TrackedItemStatus.NeedsReview) return NeedsReview;
        if (IsCommercialOffer(item.MessageType, item.Deadline)) return CommercialOffers;
        if (item.RequiresReply) return WaitingForMyReply;

        // Overdue deadlines stay urgent: anything due today or earlier is Due today, and the
        // expiration highlight distinguishes the overdue rows. Escalation is a row badge and a
        // summary metric, never a section; it must not hide a deadline.
        if (item.Deadline is { } deadline && deadline.ToOffset(now.Offset).Date <= today) return DueToday;
        return item.Status == TrackedItemStatus.Snoozed ? Snoozed : Upcoming;
    }

    // A promotion is always a commercial offer. A newsletter that surfaces only through a
    // dated offer — an advertised workshop, webinar, or event — belongs there too: its date
    // is informational offer expiry, never recipient work, so it must not occupy the
    // active-work sections even though the classifier types the email as newsletter.
    public static bool IsCommercialOffer(ClassificationMessageType messageType, DateTimeOffset? deadline) =>
        messageType == ClassificationMessageType.Promotion
        || (messageType == ClassificationMessageType.Newsletter && deadline is not null);
}

public static class TrackedItemReclassification
{
    // The sections a user may move a Needs review item into. Terminal (Done, Dismissed) and
    // Snoozed sections are reached through their own actions instead.
    public static IReadOnlySet<string> AllowedTargets { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        TrackedItemCategories.DueToday,
        TrackedItemCategories.Upcoming,
        TrackedItemCategories.WaitingForMyReply,
        TrackedItemCategories.CommercialOffers
    };

    // Records the user's section choice in SectionOverride so later classifier re-runs cannot
    // revert it, and aligns the deadline so expiration behavior matches the chosen section.
    public static void Apply(TrackedItem item, string section, DateTimeOffset now)
    {
        if (!AllowedTargets.Contains(section))
        {
            throw new ArgumentOutOfRangeException(nameof(section), section, "Not a re-classification target section.");
        }

        var today = now.Date;
        item.Status = TrackedItemStatus.Open;
        item.SectionOverride = section;
        item.CompletedAt = null;
        item.DismissedAt = null;
        item.SnoozedUntil = null;
        switch (section)
        {
            case TrackedItemCategories.DueToday:
                // Overdue deadlines are already Due today material and keep their date so the
                // row stays expiration-highlighted.
                item.Deadline = item.Deadline is { } todayDeadline && todayDeadline.ToOffset(now.Offset).Date <= today
                    ? item.Deadline
                    : EndOfDay(today, now);
                break;
            case TrackedItemCategories.Upcoming:
                // Upcoming holds every non-urgent active item, dated or not; only a deadline
                // that would keep the item in Due today is moved, one day past the boundary.
                if (item.Deadline is { } staleDeadline && staleDeadline.ToOffset(now.Offset).Date <= today)
                {
                    item.Deadline = EndOfDay(today.AddDays(1), now);
                }
                break;
            case TrackedItemCategories.CommercialOffers:
                // Offer-expiry logic and the derived classification both branch on the message type.
                item.MessageType = ClassificationMessageType.Promotion;
                break;
        }
    }

    private static DateTimeOffset EndOfDay(DateTime date, DateTimeOffset now) =>
        new(date.AddDays(1).AddTicks(-1), now.Offset);
}

public interface IOutlookCategoryMapper
{
    IReadOnlySet<string> Map(TrackedItem item, DateTimeOffset now);
}

public sealed class OutlookCategoryMapper : IOutlookCategoryMapper
{
    public IReadOnlySet<string> Map(TrackedItem item, DateTimeOffset now) =>
        new HashSet<string>([TrackedItemCategories.Classify(item, now)], StringComparer.OrdinalIgnoreCase);
}

public static class OutlookCategorySet
{
    public static IReadOnlyList<string> Parse(string? value, string separator)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        if (string.IsNullOrEmpty(separator)) separator = ",";
        return value.Split([separator], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static CategoryMergeResult Merge(
        string? currentValue,
        IReadOnlySet<string> desiredManaged,
        string separator)
    {
        var current = Parse(currentValue, separator);
        var unmanaged = current.Where(x => !OutlookManagedCategories.IsManaged(x));
        var desired = OutlookManagedCategories.Ordered.Where(desiredManaged.Contains).ToArray();
        var merged = unmanaged.Concat(desired).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var currentManaged = current.Where(OutlookManagedCategories.IsManaged).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isChanged = !currentManaged.SetEquals(desired)
            || current.Count != merged.Length
            || !current.SequenceEqual(merged, StringComparer.OrdinalIgnoreCase);
        return new CategoryMergeResult(string.Join(separator, merged), isChanged, merged);
    }
}

public sealed record CategoryMergeResult(string Serialized, bool IsChanged, IReadOnlyList<string> Categories);

public static class OutlookMessageId
{
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;
        trimmed = trimmed.Trim('<', '>').Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : $"<{trimmed}>";
    }

    public static bool Equals(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}

public sealed class OutlookStoreBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VigiloMailboxId { get; set; }
    public string OutlookStoreId { get; set; } = "";
    public string OutlookStoreDisplayName { get; set; } = "";
    public string? AccountAddress { get; set; }
    public bool IsEnabled { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OutlookItemBinding
{
    public Guid VigiloMessageId { get; set; }
    public string? InternetMessageId { get; set; }
    public string OutlookEntryId { get; set; } = "";
    public string OutlookStoreId { get; set; } = "";
    public string? LastKnownFolderEntryId { get; set; }
    public DateTimeOffset LastMatchedAtUtc { get; set; }
    public OutlookMatchMethod MatchMethod { get; set; }
}

public sealed class OutlookCategorySyncOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VigiloMessageId { get; set; }
    public string DesiredCategoriesJson { get; set; } = "[]";
    public OutlookSyncStatus Status { get; set; } = OutlookSyncStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessageSanitized { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? SupersededAtUtc { get; set; }

    public IReadOnlySet<string> GetDesiredCategories() =>
        (JsonSerializer.Deserialize<string[]>(DesiredCategoriesJson) ?? [])
        .Where(OutlookManagedCategories.IsDesired)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public enum OutlookSyncStatus
{
    Pending, InProgress, Completed, RetryScheduled, OutlookNotRunning, MailboxNotFound,
    MessageNotFound, AmbiguousMatch, UnsupportedOutlook, PermanentFailure, Superseded
}

public enum OutlookIntegrationStatus
{
    UnsupportedPlatform, ClassicOutlookNotInstalled, OutlookNotRunning, MailboxNotFound,
    Connected, TemporarilyUnavailable, PermissionDenied
}

public enum OutlookMatchMethod { EntryId, InternetMessageId, BoundedFallback }
public enum OutlookMessageMatchStatus { Found, NotFound, Ambiguous }

public sealed record OutlookConnectionInfo(OutlookIntegrationStatus Status, string? Version, string? SanitizedError = null);
public sealed record OutlookStoreInfo(string StoreId, string DisplayName, string? AccountAddress);
public sealed record OutlookMessageIdentity(string? InternetMessageId, string? PersistedEntryId, string? PersistedStoreId, string? SenderAddress, string? Subject, DateTimeOffset? ReceivedAtUtc);
public sealed record OutlookItemReference(string EntryId, string StoreId, string? FolderEntryId, OutlookMatchMethod MatchMethod);
public sealed record OutlookMessageMatchResult(OutlookMessageMatchStatus Status, OutlookItemReference? Item = null);
public sealed record OutlookQueueStatistics(int PendingCount, DateTimeOffset? LastSuccessfulSynchronizationUtc, string? LastErrorSanitized);

public static class OutlookMatchPolicy
{
    public static bool IsValidFastPath(bool isMail, string actualStoreId, string configuredStoreId,
        string? expectedInternetMessageId, string? actualInternetMessageId) =>
        isMail
        && string.Equals(actualStoreId, configuredStoreId, StringComparison.Ordinal)
        && (OutlookMessageId.Normalize(expectedInternetMessageId) is null
            || OutlookMessageId.Equals(expectedInternetMessageId, actualInternetMessageId));

    public static OutlookMessageMatchResult Select(IReadOnlyList<OutlookItemReference> candidates) => candidates.Count switch
    {
        0 => new OutlookMessageMatchResult(OutlookMessageMatchStatus.NotFound),
        1 => new OutlookMessageMatchResult(OutlookMessageMatchStatus.Found, candidates[0]),
        _ => new OutlookMessageMatchResult(OutlookMessageMatchStatus.Ambiguous)
    };
}

public sealed record OutlookRetryDecision(OutlookSyncStatus Status, bool ShouldRetry);

public static class OutlookRetryPolicy
{
    public static OutlookRetryDecision Classify(string code, bool transient, int attempts) => code switch
    {
        OutlookErrorCodes.NotRunning => new(OutlookSyncStatus.OutlookNotRunning, true),
        OutlookErrorCodes.StoreNotFound => new(OutlookSyncStatus.MailboxNotFound, attempts < 8),
        OutlookErrorCodes.MessageNotFound => new(OutlookSyncStatus.MessageNotFound, attempts < 8),
        OutlookErrorCodes.MessageAmbiguous => new(OutlookSyncStatus.AmbiguousMatch, false),
        OutlookErrorCodes.UnsupportedPlatform or OutlookErrorCodes.ClassicNotInstalled => new(OutlookSyncStatus.UnsupportedOutlook, false),
        _ when transient && attempts < 8 => new(OutlookSyncStatus.RetryScheduled, true),
        _ => new(OutlookSyncStatus.PermanentFailure, false)
    };
}

public static class OutlookErrorCodes
{
    public const string UnsupportedPlatform = "OUTLOOK_UNSUPPORTED_PLATFORM";
    public const string ClassicNotInstalled = "OUTLOOK_CLASSIC_NOT_INSTALLED";
    public const string NotRunning = "OUTLOOK_NOT_RUNNING";
    public const string ComUnavailable = "OUTLOOK_COM_UNAVAILABLE";
    public const string Busy = "OUTLOOK_BUSY";
    public const string CallRejected = "OUTLOOK_CALL_REJECTED";
    public const string StoreNotFound = "OUTLOOK_STORE_NOT_FOUND";
    public const string MessageNotFound = "OUTLOOK_MESSAGE_NOT_FOUND";
    public const string MessageAmbiguous = "OUTLOOK_MESSAGE_AMBIGUOUS";
    public const string CategorySetupFailed = "OUTLOOK_CATEGORY_SETUP_FAILED";
    public const string CategoryApplyFailed = "OUTLOOK_CATEGORY_APPLY_FAILED";
    public const string ItemSaveFailed = "OUTLOOK_ITEM_SAVE_FAILED";
    public const string PermissionDenied = "OUTLOOK_PERMISSION_DENIED";
    public const string UnknownComError = "OUTLOOK_UNKNOWN_COM_ERROR";
}

public sealed class OutlookIntegrationException(string code, string sanitizedMessage, bool isTransient, Exception? inner = null)
    : Exception(sanitizedMessage, inner)
{
    public string Code { get; } = code;
    public bool IsTransient { get; } = isTransient;
}
