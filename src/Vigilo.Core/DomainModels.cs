namespace Vigilo.Core;

public sealed class EmailAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string EmailAddress { get; set; } = "";
    public string ImapHost { get; set; } = "imap.mail.yahoo.com";
    public int ImapPort { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string FoldersToMonitor { get; set; } = "Inbox";
    public string SentFoldersToMonitor { get; set; } = "Sent;Sent Items";
    public int PollingFallbackMinutes { get; set; } = 15;
    public bool UseImapIdle { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    // Retained for compatibility with existing databases; digest scheduling has been removed.
    public TimeSpan DigestTime { get; set; } = new(8, 30, 0);
    public int DataRetentionDays { get; set; } = 180;
    // Retained for compatibility with existing databases; message-body storage is always enabled.
    public bool StoreMessageBodies { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name;
            }

            return string.IsNullOrWhiteSpace(EmailAddress)
                ? $"{ProviderName} account"
                : EmailAddress;
        }
    }

    public string ProviderName
    {
        get
        {
            if (ImapHost.Contains("gmail", StringComparison.OrdinalIgnoreCase))
            {
                return "Gmail";
            }

            if (ImapHost.Contains("yahoo", StringComparison.OrdinalIgnoreCase))
            {
                return "Yahoo";
            }

            if (ImapHost.Contains("office365", StringComparison.OrdinalIgnoreCase)
                || ImapHost.Contains("outlook", StringComparison.OrdinalIgnoreCase))
            {
                return "Outlook";
            }

            if (ImapHost.Contains("mail.me.com", StringComparison.OrdinalIgnoreCase))
            {
                return "iCloud";
            }

            if (ImapHost.Contains("aol", StringComparison.OrdinalIgnoreCase))
            {
                return "AOL";
            }

            if (ImapHost.Contains("fastmail", StringComparison.OrdinalIgnoreCase))
            {
                return "Fastmail";
            }

            if (ImapHost.Contains("zoho", StringComparison.OrdinalIgnoreCase))
            {
                return "Zoho";
            }

            return "Email";
        }
    }

    public IReadOnlyList<string> Folders =>
        FoldersToMonitor.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class ProcessedEmail
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string Folder { get; set; } = "";
    public long UidValidity { get; set; }
    public long ImapUid { get; set; }
    public string? MessageIdHeader { get; set; }
    public string SubjectHash { get; set; } = "";
    public string BodyHash { get; set; } = "";
    public string FlagsHash { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? LastProcessedAt { get; set; }
    public ProcessingStatus ProcessingStatus { get; set; }
    public string ClassificationVersion { get; set; } = "";
    public string ClassifierModelVersion { get; set; } = "";
    public string? ClassificationHash { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? EmailMessageId { get; set; }
    public int ClassificationAttemptCount { get; set; }
    public DateTimeOffset? NextClassificationAttemptAt { get; set; }
    public string LastClassificationFailureKind { get; set; } = "";
    public string LastClassificationRecoveryKind { get; set; } = "";
}

public enum ProcessingStatus
{
    New,
    Fetched,
    Classified,
    Failed,
    Skipped,
    NeedsReclassification,
    DeletedFromMailbox
}

public sealed class EmailMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string Folder { get; set; } = "";
    public string ProviderMessageId { get; set; } = "";
    public string? MessageIdHeader { get; set; }
    public string SenderName { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public string Snippet { get; set; } = "";
    public string? NormalizedBody { get; set; }
    public string? OriginalTextBody { get; set; }
    public string? OriginalHtmlBody { get; set; }
    public string? InReplyTo { get; set; }
    public string? References { get; set; }
    public string ThreadKey { get; set; } = "";
    public bool HasAttachments { get; set; }
    public bool IsRead { get; set; }
    public DateTimeOffset LastScannedAt { get; set; }
}

public sealed record ClassificationResult
{
    public bool IsActionable { get; init; }
    public ClassificationMessageType MessageType { get; init; }
    public bool HasUserSpecificObligation { get; init; }
    public DateTimeOffset? UserActionDeadline { get; init; }
    public string? UserActionDeadlineEvidence { get; init; }
    public bool UserObligationMayEscalate { get; init; }
    public bool UserReplyRequired { get; init; }
    public string ActionSummary { get; init; } = "";
    public string Reason { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool NeedsReview { get; init; }
    public bool ClassificationUnavailable { get; init; }
    public ClassificationRecoveryKind RecoveryKind { get; init; }
    public ClassificationFailureKind FailureKind { get; init; }
    public string RawModelOutput { get; init; } = "";

    public static ClassificationResult NeedsReviewFallback(string reason) => new()
    {
        IsActionable = true,
        NeedsReview = true,
        ActionSummary = "Review this email",
        Reason = reason,
        Tags = ["needs-review"]
    };

    public static ClassificationResult UnavailableFallback(
        string reason,
        ClassificationFailureKind failureKind = ClassificationFailureKind.ModelUnavailable) =>
        new()
        {
            ClassificationUnavailable = true,
            FailureKind = failureKind,
            ActionSummary = "",
            Reason = reason,
            Tags = ["classification-unavailable"]
        };
}

public enum ClassificationRecoveryKind
{
    None,
    RecoveredLocally,
    ReviewedByModel
}

public enum ClassificationFailureKind
{
    None,
    ModelUnavailable,
    UnusableModelOutput
}

public enum ClassificationMessageType
{
    Unknown,
    Newsletter,
    Promotion,
    Transactional,
    Personal
}

public sealed class TrackedItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmailMessageId { get; set; }
    public TrackedItemStatus Status { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public bool IsEscalation { get; set; }
    public bool RequiresReply { get; set; }
    public ClassificationMessageType MessageType { get; set; }
    public string ThreadKey { get; set; } = "";
    public DateTimeOffset? SentReplyDetectedAt { get; set; }
    public string? SentReplyMessageId { get; set; }
    public string ActionSummary { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
    public string? SectionOverride { get; set; }
    public string Tags { get; set; } = "";
}

public enum TrackedItemStatus
{
    Open,
    Done,
    Dismissed,
    Snoozed,
    Expired,
    NeedsReview
}

public sealed class LiveReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset GeneratedAt { get; set; }
    public int DueTodayCount { get; set; }
    public int UpcomingCount { get; set; }
    public int WaitingReplyCount { get; set; }
    public int EscalationCount { get; set; }
    public int NeedsReviewCount { get; set; }
    public int CommercialOfferCount { get; set; }
    public int ProcessedMessageCount { get; set; }
    public DateTimeOffset? LastMessageProcessedAt { get; set; }
}

public sealed class NotificationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TrackedItemId { get; set; }
    public NotificationKind Kind { get; set; }
    public string DedupeKey { get; set; } = "";
    public DateTimeOffset SentAt { get; set; }
}

public sealed class SenderRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string SenderEmail { get; set; } = "";
    public string NormalizedSenderEmail { get; set; } = "";
    public SenderRuleKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum SenderRuleKind
{
    Vip,
    Ignored
}

public sealed record SenderRuleView(
    Guid Id,
    Guid AccountId,
    string AccountDisplayName,
    string SenderEmail,
    SenderRuleKind Kind,
    DateTimeOffset UpdatedAt)
{
    public string KindDisplayName => Kind == SenderRuleKind.Vip ? "VIP" : "Ignored";
}

public enum NotificationKind
{
    NewActionableItem,
    DeadlineDueToday,
    PossibleEscalation
}

public sealed record FetchedEmail(
    Guid AccountId,
    string Folder,
    long UidValidity,
    long ImapUid,
    EmailMessage Message,
    string SubjectHash,
    string BodyHash,
    string FlagsHash);

public sealed record EmailScanProgress(
    string AccountName,
    int AccountIndex,
    int AccountCount,
    string FolderName,
    int FolderIndex,
    int FolderCount,
    int ProcessedMessages,
    int TotalMessages,
    string Stage);

public sealed record ScanResult(int Fetched, int Classified, int NeedsReview, int Failed)
{
    public static ScanResult Empty { get; } = new(0, 0, 0, 0);

    public ScanResult Add(ScanResult result) => new(
        Fetched + result.Fetched,
        Classified + result.Classified,
        NeedsReview + result.NeedsReview,
        Failed + result.Failed);

    public ScanResult Add(EmailProcessingOutcome outcome) => outcome switch
    {
        EmailProcessingOutcome.Classified => this with { Fetched = Fetched + 1, Classified = Classified + 1 },
        EmailProcessingOutcome.NeedsReview => this with { Fetched = Fetched + 1, NeedsReview = NeedsReview + 1 },
        EmailProcessingOutcome.Failed => this with { Fetched = Fetched + 1, Failed = Failed + 1 },
        EmailProcessingOutcome.Skipped => this with { Fetched = Fetched + 1 },
        EmailProcessingOutcome.Unchanged or EmailProcessingOutcome.FlagsOnlyUpdated => this,
        _ => this
    };
}

public enum EmailProcessingOutcome
{
    Unchanged,
    FlagsOnlyUpdated,
    Classified,
    NeedsReview,
    Failed,
    Skipped
}

public enum TrackedItemExpirationState
{
    None,
    NearExpiration,
    Expired
}

public sealed record TrackedItemView(
    Guid Id,
    Guid EmailMessageId,
    string AccountDisplayName,
    string Section,
    TrackedItemStatus Status,
    string ActionSummary,
    string Sender,
    string SenderEmail,
    string Subject,
    DateTimeOffset? Deadline,
    string Reason,
    bool IsEscalation,
    bool RequiresReply,
    DateTimeOffset? SentReplyDetectedAt,
    DateTimeOffset ReceivedAt)
{
    public TrackedItemExpirationState ExpirationState => GetExpirationState(DateTimeOffset.Now);

    public TrackedItemExpirationState GetExpirationState(DateTimeOffset now)
    {
        if (Status is TrackedItemStatus.Done or TrackedItemStatus.Dismissed)
        {
            return TrackedItemExpirationState.None;
        }

        // Commercial offers carry an informational offer-expiry date, not work the user
        // must complete, so they never get urgency highlighting.
        if (Section == TrackedItemCategories.CommercialOffers)
        {
            return TrackedItemExpirationState.None;
        }

        if (Status == TrackedItemStatus.Expired || Deadline is { } deadline && deadline <= now)
        {
            return TrackedItemExpirationState.Expired;
        }

        return Deadline is { } upcomingDeadline && upcomingDeadline - now < TimeSpan.FromHours(72)
            ? TrackedItemExpirationState.NearExpiration
            : TrackedItemExpirationState.None;
    }
}

public sealed record TrackedMessageDetail(
    Guid TrackedItemId,
    Guid EmailMessageId,
    string Section,
    TrackedItemStatus Status,
    string ActionSummary,
    string Reason,
    string Tags,
    bool IsEscalation,
    bool RequiresReply,
    DateTimeOffset? Deadline,
    DateTimeOffset? SentReplyDetectedAt,
    string AccountDisplayName,
    string Folder,
    string SenderName,
    string SenderEmail,
    string Subject,
    DateTimeOffset ReceivedAt,
    string Snippet,
    string Body,
    bool HasAttachments,
    bool IsRead,
    string ThreadKey,
    string ProcessingStatus,
    string? OriginalTextBody = null,
    string? OriginalHtmlBody = null,
    string OutlookStatusText = "Outlook: Disabled",
    string OutlookCategories = "",
    Guid AccountId = default,
    SenderRuleKind? SenderRule = null)
{
    public bool HasOriginalHtmlBody => !string.IsNullOrWhiteSpace(OriginalHtmlBody);

    public bool HasOriginalTextBody => !string.IsNullOrWhiteSpace(OriginalTextBody);

    public string OriginalBodyKind => HasOriginalHtmlBody
        ? "HTML"
        : HasOriginalTextBody
            ? "Plain text"
            : "Not stored";
}

public sealed record ModelStatus(
    bool IsInstalled,
    string ModelPath,
    string Version,
    string Message,
    IReadOnlyList<string> MissingFiles);

public sealed record ModelInstallResult(bool Success, string Message, ModelStatus Status);

public sealed record LocalDataMaintenanceResult(int DeletedMessages, int DeletedProcessedRows);

public static class ClassificationMetadata
{
    // Fallback for composition roots that do not register IClassificationVersionSource;
    // the App composition root uses the live source, which composes the harness version
    // with the content-derived prompt version instead of these hand-maintained constants.
    public const string CurrentVersion = "vigilo-single-verdict-v3";
    public const string CurrentPromptVersion = "vigilo-verdict-prompts-v1";
}
