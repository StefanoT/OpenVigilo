namespace Vigilo.Classification;

public sealed record EmailContentInput(
    string? PlainTextBody,
    string? HtmlBody,
    IReadOnlyList<AttachmentSummary>? Attachments = null,
    Uri? BaseUri = null);

public sealed record NormalizedEmailContent
{
    public string PlainText { get; init; } = "";
    public string? HtmlText { get; init; }
    public IReadOnlyList<ExtractedLink> Links { get; init; } = [];
    public IReadOnlyList<AttachmentSummary> Attachments { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool WasHtml { get; init; }
    public bool UsedPlainTextAlternative { get; init; }
    public string? PreviousConversationText { get; init; }
}

public sealed record ExtractedLink(
    string VisibleText,
    string Href,
    string? NormalizedUrl,
    string? Domain,
    bool IsSuspicious,
    LinkKind Kind);

public sealed record AttachmentSummary(
    string? FileName,
    string? ContentType,
    long? SizeBytes,
    bool IsInline = false);

public sealed record EmailContentMetadata(
    string Subject,
    string From,
    DateTimeOffset Date,
    string? To = null,
    string? Cc = null);

public enum LinkKind
{
    Http,
    Https,
    Mailto,
    Tel,
    Other
}

public interface IEmailContentNormalizer
{
    NormalizedEmailContent Normalize(EmailContentInput input);

    string BuildLlmPayload(
        EmailContentMetadata metadata,
        NormalizedEmailContent content,
        int maxCharacterBudget = EmailContentNormalizer.DefaultLlmPayloadCharacterBudget);
}
