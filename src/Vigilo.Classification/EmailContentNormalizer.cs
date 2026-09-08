using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vigilo.Classification;

public sealed partial class EmailContentNormalizer(
    ILogger<EmailContentNormalizer>? logger = null) : IEmailContentNormalizer
{
    public const int DefaultLlmPayloadCharacterBudget = 12_000;
    public const int MaxLinks = 25;
    private const int InlineUrlPlaceholderThreshold = 80;
    private const string LinkDomainSeparator = " \u2192 ";
    private const string InternalLinkReferencePrefix = "[link-source:";

    private static readonly string[] ActionLinkTerms =
    [
        "review",
        "approve",
        "approval",
        "sign",
        "pay",
        "payment",
        "invoice",
        "contract",
        "document",
        "form",
        "deadline",
        "complete",
        "submit",
        "upload",
        "verify",
        "respond",
        "reply",
        "schedule",
        "confirm",
        "accept",
        "decline",
        "invitation",
        "appointment",
        "track package",
        "reset password",
        "renew",
        "scadenza",
        "rispondi",
        "conferma",
        "firma",
        "pagamento",
        "documento"
    ];

    private static readonly string[] BoilerplateTerms =
    [
        "view this email in your browser",
        "privacy policy",
        "terms of use",
        "all rights reserved",
        "follow us on",
        "facebook",
        "instagram",
        "linkedin",
        "twitter",
        "x.com"
    ];

    private readonly ILogger<EmailContentNormalizer> _logger = logger ?? NullLogger<EmailContentNormalizer>.Instance;

    public NormalizedEmailContent Normalize(EmailContentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var warnings = new List<string>();
        var attachments = input.Attachments ?? [];
        var plain = NormalizePlainText(input.PlainTextBody);
        HtmlConversionResult html = HtmlConversionResult.Empty;
        var wasHtml = !string.IsNullOrWhiteSpace(input.HtmlBody);

        if (wasHtml)
        {
            try
            {
                html = ConvertHtml(input.HtmlBody!, input.BaseUri, warnings);
            }
            catch (Exception ex)
            {
                warnings.Add("HTML parsing failed; plain text fallback was used when available.");
                _logger.LogWarning(ex, "Email HTML normalization failed. PlainTextLength={PlainTextLength}", plain.Length);
            }
        }

        var usedPlainText = !string.IsNullOrWhiteSpace(plain);
        var selected = SelectBody(plain, html.Text, usedPlainText, warnings);
        var split = SplitQuotedConversation(selected);
        var cleaned = CleanBoilerplate(split.CurrentMessage);
        var referencedText = ReplaceKnownInlineUrlsWithSourceReferences(
            NormalizeLineEndings(cleaned),
            html.Links);
        var finalText = CompactLongInlineUrls(referencedText);
        var selectedLinks = SelectLinks(html.Links, finalText, html.Text);
        var resolvedText = ResolveLinkReferences(finalText, html.Links, selectedLinks);
        var resolvedHtmlText = ResolveLinkReferences(html.Text, html.Links, selectedLinks);
        var resolvedPreviousConversation = string.IsNullOrWhiteSpace(split.PreviousConversation)
            ? null
            : ResolveLinkReferences(NormalizeLineEndings(split.PreviousConversation), html.Links, selectedLinks);

        var content = new NormalizedEmailContent
        {
            PlainText = resolvedText,
            HtmlText = string.IsNullOrWhiteSpace(resolvedHtmlText) ? null : resolvedHtmlText,
            Links = selectedLinks,
            Attachments = attachments,
            Warnings = warnings,
            WasHtml = wasHtml,
            UsedPlainTextAlternative = usedPlainText && ReferenceEquals(selected, plain),
            PreviousConversationText = resolvedPreviousConversation
        };

        _logger.LogInformation(
            "Email content normalized. WasHtml={WasHtml} UsedPlainTextAlternative={UsedPlainTextAlternative} PlainTextLengthBefore={PlainTextLengthBefore} PlainTextLengthAfter={PlainTextLengthAfter} HtmlTextLength={HtmlTextLength} LinkCount={LinkCount} AttachmentCount={AttachmentCount} WarningCount={WarningCount}",
            content.WasHtml,
            content.UsedPlainTextAlternative,
            plain.Length,
            content.PlainText.Length,
            content.HtmlText?.Length ?? 0,
            content.Links.Count,
            content.Attachments.Count,
            content.Warnings.Count);

        return content;
    }

    public string BuildLlmPayload(
        EmailContentMetadata metadata,
        NormalizedEmailContent content,
        int maxCharacterBudget = DefaultLlmPayloadCharacterBudget)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);

        var links = content.Links.Take(MaxLinks).ToArray();
        var warnings = content.Warnings.Take(8).ToArray();
        var attachments = content.Attachments.Take(20).ToArray();
        var previous = content.PreviousConversationText ?? "";
        var body = content.PlainText;
        var truncated = false;

        while (true)
        {
            var payload = ComposePayload(metadata, body, links, attachments, warnings, previous, truncated);
            if (payload.Length <= maxCharacterBudget)
            {
                _logger.LogInformation(
                    "Email LLM payload built. PayloadLength={PayloadLength} LinkCount={LinkCount} AttachmentCount={AttachmentCount} Truncated={Truncated}",
                    payload.Length,
                    links.Length,
                    attachments.Length,
                    truncated);
                return payload;
            }

            truncated = true;
            if (!string.IsNullOrWhiteSpace(previous))
            {
                previous = "";
                continue;
            }

            if (links.Length > 8)
            {
                links = links.Take(8).ToArray();
                continue;
            }

            if (warnings.Length > 4)
            {
                warnings = warnings.Take(4).ToArray();
                continue;
            }

            if (attachments.Length > 10)
            {
                attachments = attachments.Take(10).ToArray();
                continue;
            }

            var fixedPayload = ComposePayload(metadata, "", links, attachments, warnings, "", true);
            var remaining = Math.Max(0, maxCharacterBudget - fixedPayload.Length - 32);
            if (remaining <= 0)
            {
                if (links.Length > 0)
                {
                    links = [];
                    continue;
                }

                if (attachments.Length > 0)
                {
                    attachments = [];
                    continue;
                }

                if (warnings.Length > 0)
                {
                    warnings = [];
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(body))
                {
                    fixedPayload = ComposePayload(metadata, "", [], [], [], "", true);
                    remaining = Math.Max(0, maxCharacterBudget - fixedPayload.Length - 32);
                }

                if (remaining > 0)
                {
                    body = TruncateText(body, remaining);
                    continue;
                }

                var minimalPayload = ComposePayload(metadata, "", [], [], [], "", true);
                return minimalPayload.Length <= maxCharacterBudget
                    ? minimalPayload
                    : TruncateText(minimalPayload, Math.Max(0, maxCharacterBudget));
            }

            body = remaining <= 0 ? "" : TruncateText(body, remaining);
        }
    }

    public static bool IsLlmPayload(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value
            .TrimStart('\ufeff', ' ', '\t', '\r', '\n')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return normalized.StartsWith("Subject:", StringComparison.Ordinal)
            && normalized.Contains("\nBody:\n", StringComparison.Ordinal);
    }

    public static string TruncatePayload(string payload, int maxCharacterBudget)
    {
        if (payload.Length <= maxCharacterBudget)
        {
            return payload;
        }

        const string warning = "\n\nNormalization warnings:\n- Payload truncated before classification.";
        if (maxCharacterBudget <= warning.Length)
        {
            return TruncateText(payload, Math.Max(0, maxCharacterBudget));
        }

        return TruncateText(payload, maxCharacterBudget - warning.Length) + warning;
    }

    private static string ComposePayload(
        EmailContentMetadata metadata,
        string body,
        IReadOnlyList<ExtractedLink> links,
        IReadOnlyList<AttachmentSummary> attachments,
        IReadOnlyList<string> warnings,
        string previousConversation,
        bool truncated)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CleanHeader("Subject", metadata.Subject));
        builder.AppendLine(CleanHeader("From", metadata.From));
        if (!string.IsNullOrWhiteSpace(metadata.To))
        {
            builder.AppendLine(CleanHeader("To", metadata.To));
        }

        if (!string.IsNullOrWhiteSpace(metadata.Cc))
        {
            builder.AppendLine(CleanHeader("Cc", metadata.Cc));
        }

        builder.AppendLine(CleanHeader("Date", metadata.Date.ToString("O", CultureInfo.InvariantCulture)));
        builder.AppendLine("Body:");
        var availableBody = RemoveUnavailableLinkReferences(body, links.Count);
        builder.AppendLine(string.IsNullOrWhiteSpace(availableBody) ? "(empty)" : availableBody.Trim());

        if (links.Count > 0)
        {
            var renderedLinks = links.Select(RenderLinkForClassifier).ToArray();

            if (renderedLinks.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Important links:");
            }

            for (var index = 0; index < renderedLinks.Length; index++)
            {
                builder.Append(index + 1);
                builder.Append(". ");
                builder.AppendLine(renderedLinks[index]);
            }
        }

        if (attachments.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Attachments:");
            foreach (var attachment in attachments)
            {
                builder.Append("- ");
                builder.Append(string.IsNullOrWhiteSpace(attachment.FileName) ? "(unnamed)" : attachment.FileName);
                if (!string.IsNullOrWhiteSpace(attachment.ContentType))
                {
                    builder.Append(" (");
                    builder.Append(attachment.ContentType);
                    builder.Append(')');
                }

                if (attachment.SizeBytes is not null)
                {
                    builder.Append(", ");
                    builder.Append(attachment.SizeBytes.Value.ToString(CultureInfo.InvariantCulture));
                    builder.Append(" bytes");
                }

                if (attachment.IsInline)
                {
                    builder.Append(", inline");
                }

                builder.AppendLine();
            }
        }

        var availablePreviousConversation = RemoveUnavailableLinkReferences(previousConversation, links.Count);
        if (!string.IsNullOrWhiteSpace(availablePreviousConversation))
        {
            builder.AppendLine();
            builder.AppendLine("Previous conversation:");
            builder.AppendLine(TruncateText(availablePreviousConversation, 1200));
        }

        if (warnings.Count > 0 || truncated)
        {
            builder.AppendLine();
            builder.AppendLine("Normalization warnings:");
            foreach (var warning in warnings)
            {
                builder.Append("- ");
                builder.AppendLine(warning);
            }

            if (truncated)
            {
                builder.AppendLine("- Content was truncated to fit the classifier payload budget.");
            }
        }

        return builder.ToString().Trim();
    }

    private static string CleanHeader(string name, string value)
    {
        var clean = SingleLineWhitespaceRegex().Replace(value ?? "", " ").Trim();
        return $"{name}: {clean}";
    }

    private static string SelectBody(string plain, string htmlText, bool hasPlain, List<string> warnings)
    {
        if (!hasPlain)
        {
            return htmlText;
        }

        if (string.IsNullOrWhiteSpace(htmlText))
        {
            return plain;
        }

        if (plain.Length < 120 && htmlText.Length > plain.Length * 4)
        {
            warnings.Add("Plain text alternative was suspiciously short; HTML-derived text was used.");
            return htmlText;
        }

        return plain;
    }

    private static HtmlConversionResult ConvertHtml(string html, Uri? baseUri, List<string> warnings)
    {
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
        using var document = parser.ParseDocument(html);
        RemoveUnsafeAndHiddenElements(document);
        var links = ExtractLinks(document, baseUri, warnings);
        var linkReferences = links
            .Select((link, index) => (Key: GetLinkKey(link), ReferenceId: index + 1))
            .ToDictionary(item => item.Key, item => item.ReferenceId, StringComparer.OrdinalIgnoreCase);
        var text = NormalizeLineEndings(ExtractReadableText(
            document.Body ?? document.DocumentElement,
            baseUri,
            linkReferences));
        return new HtmlConversionResult(text, links);
    }

    private static void RemoveUnsafeAndHiddenElements(IParentNode document)
    {
        const string removableSelector =
            "script,style,noscript,svg,canvas,iframe,form,input,button,template,[hidden],[aria-hidden='true'],[aria-hidden='True'],[aria-hidden='TRUE']";
        foreach (var element in document.QuerySelectorAll(removableSelector).ToArray())
        {
            element.Remove();
        }

        foreach (var element in document.QuerySelectorAll("*").ToArray())
        {
            if (IsHiddenElement(element) || IsTrackingPixel(element))
            {
                element.Remove();
            }
        }
    }

    private static bool IsHiddenElement(IElement element)
    {
        // A body-level font-size or display style is a typographic wrapper, not a concealed
        // element: removing the body would detach every child and empty the whole email.
        var localName = element.LocalName;
        if (localName is "html" or "body")
        {
            return false;
        }

        var style = element.GetAttribute("style");
        if (string.IsNullOrWhiteSpace(style))
        {
            return false;
        }

        if (ConcealedStyleRegex().IsMatch(style))
        {
            return true;
        }

        // font-size:0 is an email spacing/reset trick on wrappers whose children re-enable
        // their own sizes. It only conceals readable content when the element is a leaf
        // holding text directly, which is the classic hidden-preheader pattern.
        return FontSizeZeroStyleRegex().IsMatch(style) && !element.Children.Any();
    }

    private static bool IsTrackingPixel(IElement element)
    {
        if (!element.LocalName.Equals("img", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var width = ParseCssPixelValue(element.GetAttribute("width"));
        var height = ParseCssPixelValue(element.GetAttribute("height"));
        var style = element.GetAttribute("style") ?? "";
        if (width is null && WidthStyleRegex().Match(style) is { Success: true } widthMatch)
        {
            width = ParseCssPixelValue(widthMatch.Groups[1].Value);
        }

        if (height is null && HeightStyleRegex().Match(style) is { Success: true } heightMatch)
        {
            height = ParseCssPixelValue(heightMatch.Groups[1].Value);
        }

        return width is <= 1 && height is <= 1;
    }

    private static int? ParseCssPixelValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = NumberRegex().Match(value);
        return match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    private static string ExtractReadableText(
        INode? node,
        Uri? baseUri,
        IReadOnlyDictionary<string, int> linkReferences)
    {
        if (node is null)
        {
            return "";
        }

        var builder = new StringBuilder();
        AppendNodeText(node, builder, 0, baseUri, linkReferences);
        return builder.ToString();
    }

    private static void AppendNodeText(
        INode node,
        StringBuilder builder,
        int quoteDepth,
        Uri? baseUri,
        IReadOnlyDictionary<string, int> linkReferences)
    {
        if (node is IText text)
        {
            builder.Append(text.Data);
            return;
        }

        if (node is not IElement element && node is not IDocument)
        {
            foreach (var child in node.ChildNodes)
            {
                AppendNodeText(child, builder, quoteDepth, baseUri, linkReferences);
            }

            return;
        }

        var tag = node is IElement e ? e.LocalName.ToLowerInvariant() : "";
        if (node is IHtmlAnchorElement anchor)
        {
            var href = anchor.GetAttribute("href")?.Trim();
            var rendered = RenderLinkForClassifier(anchor.TextContent, href, baseUri, includeDomain: false);
            var key = GetLinkKey(href, baseUri);
            if (key is not null && linkReferences.TryGetValue(key, out var referenceId))
            {
                if (!string.IsNullOrWhiteSpace(rendered))
                {
                    builder.Append(rendered);
                    builder.Append(' ');
                }

                builder.Append(InternalLinkReferencePrefix);
                builder.Append(referenceId);
                builder.Append(']');
                return;
            }

            if (!string.IsNullOrWhiteSpace(rendered))
            {
                builder.Append(rendered);
                return;
            }
        }

        switch (tag)
        {
            case "br":
                builder.AppendLine();
                return;
            case "li":
                AppendParagraphBreak(builder);
                builder.Append("- ");
                break;
            case "tr":
                AppendParagraphBreak(builder);
                break;
            case "td":
            case "th":
                AppendCellSeparator(builder);
                break;
            case "blockquote":
                AppendParagraphBreak(builder);
                builder.Append("> ");
                quoteDepth++;
                break;
            case "p":
            case "div":
            case "section":
            case "article":
            case "header":
            case "footer":
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
            case "table":
            case "ul":
            case "ol":
                AppendParagraphBreak(builder);
                break;
        }

        foreach (var child in node.ChildNodes)
        {
            AppendNodeText(child, builder, quoteDepth, baseUri, linkReferences);
        }

        switch (tag)
        {
            case "p":
            case "div":
            case "section":
            case "article":
            case "header":
            case "footer":
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
            case "li":
            case "tr":
            case "table":
            case "blockquote":
                AppendParagraphBreak(builder);
                break;
        }
    }

    private static void AppendParagraphBreak(StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return;
        }

        var value = builder.ToString();
        if (!value.EndsWith("\n\n", StringComparison.Ordinal))
        {
            builder.AppendLine();
            builder.AppendLine();
        }
    }

    private static void AppendCellSeparator(StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return;
        }

        var last = builder[^1];
        if (last is not ('\n' or '\r' or '\t' or ' '))
        {
            builder.Append(" | ");
        }
    }

    private static IReadOnlyList<ExtractedLink> ExtractLinks(
        IHtmlDocument document,
        Uri? baseUri,
        List<string> warnings)
    {
        var links = new List<ExtractedLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in document.QuerySelectorAll("a[href]").OfType<IHtmlAnchorElement>())
        {
            var href = anchor.GetAttribute("href")?.Trim();
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var visible = NormalizeInlineText(anchor.TextContent);
            if (string.IsNullOrWhiteSpace(visible))
            {
                visible = href;
            }

            var normalized = NormalizeUrl(href, baseUri, out var domain, out var kind);
            var key = normalized ?? href;
            if (!seen.Add(key))
            {
                continue;
            }

            links.Add(new ExtractedLink(
                visible,
                href,
                normalized,
                domain,
                IsSuspiciousLinkText(visible, href, domain),
                kind));
        }

        if (links.Count > 100)
        {
            warnings.Add("Email contained more than 100 links; only the most relevant links were retained.");
        }

        return links;
    }

    private static IReadOnlyList<ExtractedLink> SelectLinks(
        IReadOnlyList<ExtractedLink> links,
        string plainText,
        string htmlText)
    {
        if (links.Count == 0)
        {
            return [];
        }

        var operational = ContainsActionTerm(plainText) || ContainsActionTerm(htmlText);
        return links
            .Where(link => operational || !IsUnsubscribeLink(link))
            .OrderByDescending(link => ContainsActionTerm(link.VisibleText) || ContainsActionTerm(link.Href))
            .ThenBy(link => IsUnsubscribeLink(link))
            .Take(MaxLinks)
            .ToArray();
    }

    private static string ResolveLinkReferences(
        string value,
        IReadOnlyList<ExtractedLink> extractedLinks,
        IReadOnlyList<ExtractedLink> selectedLinks)
    {
        if (string.IsNullOrWhiteSpace(value) || extractedLinks.Count == 0)
        {
            return value;
        }

        var selectedReferenceIds = selectedLinks
            .Select((link, index) => (Key: GetLinkKey(link), ReferenceId: index + 1))
            .ToDictionary(item => item.Key, item => item.ReferenceId, StringComparer.OrdinalIgnoreCase);

        for (var sourceIndex = 0; sourceIndex < extractedLinks.Count; sourceIndex++)
        {
            var key = GetLinkKey(extractedLinks[sourceIndex]);
            var replacement = selectedReferenceIds.TryGetValue(key, out var referenceId)
                ? $"[link:{referenceId}]"
                : "";
            value = value.Replace(
                $"{InternalLinkReferencePrefix}{sourceIndex + 1}]",
                replacement,
                StringComparison.Ordinal);
        }

        return NormalizeLineEndings(value);
    }

    private static string RemoveUnavailableLinkReferences(string value, int availableLinkCount)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return NumberedLinkReferenceRegex().Replace(value, match =>
        {
            var referenceId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return referenceId <= availableLinkCount ? match.Value : "";
        });
    }

    private static string GetLinkKey(ExtractedLink link) => link.NormalizedUrl ?? link.Href;

    private static string? GetLinkKey(string? href, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        return NormalizeUrl(href, baseUri, out _, out _) ?? href;
    }

    private static string? NormalizeUrl(string href, Uri? baseUri, out string? domain, out LinkKind kind)
    {
        domain = null;
        kind = LinkKind.Other;

        if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            kind = LinkKind.Mailto;
            return href;
        }

        if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
        {
            kind = LinkKind.Tel;
            return href;
        }

        Uri? uri = null;
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
        {
            uri = absolute;
        }
        else if (baseUri is not null && Uri.TryCreate(baseUri, href, out var relative))
        {
            uri = relative;
        }

        if (uri is null)
        {
            return null;
        }

        domain = uri.Host;
        kind = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? LinkKind.Https
            : uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                ? LinkKind.Http
                : LinkKind.Other;
        return uri.AbsoluteUri;
    }

    private static string RenderLinkForClassifier(ExtractedLink link)
    {
        var rendered = RenderLinkForClassifier(link.VisibleText, link.NormalizedUrl ?? link.Href, null, link.IsSuspicious);
        return string.IsNullOrWhiteSpace(rendered) ? "" : rendered;
    }

    private static string RenderLinkForClassifier(
        string? anchorText,
        string? href,
        Uri? baseUri,
        bool isSuspicious = false,
        bool includeDomain = true)
    {
        var text = NormalizeInlineText(anchorText ?? "");
        var visibleDomain = ExtractDomainLikeText(text);
        if (!string.IsNullOrWhiteSpace(visibleDomain)
            && text.Contains('/', StringComparison.Ordinal))
        {
            text = visibleDomain;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (!includeDomain || string.IsNullOrWhiteSpace(href))
        {
            return text;
        }

        var domain = TryExtractMeaningfulDomain(href, baseUri);
        if (string.IsNullOrWhiteSpace(domain))
        {
            return text;
        }

        if (!isSuspicious && !ContainsActionTerm(text))
        {
            return text;
        }

        return text.EndsWith(domain, StringComparison.OrdinalIgnoreCase)
            ? text
            : text + LinkDomainSeparator + domain;
    }

    private static string? TryExtractMeaningfulDomain(string href, Uri? baseUri)
    {
        Uri? uri = null;
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
        {
            uri = absolute;
        }
        else if (baseUri is not null && Uri.TryCreate(baseUri, href, out var relative))
        {
            uri = relative;
        }

        if (uri is null)
        {
            return null;
        }

        var destination = TryExtractRedirectDestination(uri);
        if (destination is not null)
        {
            uri = destination;
        }

        var host = uri.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        if (host.Equals("pay.stripe.com", StringComparison.OrdinalIgnoreCase))
        {
            host = "stripe.com";
        }

        return string.IsNullOrWhiteSpace(host) ? null : host.ToLowerInvariant();
    }

    private static Uri? TryExtractRedirectDestination(Uri uri)
    {
        if (string.IsNullOrWhiteSpace(uri.Query))
        {
            return null;
        }

        foreach (var (name, value) in EnumerateQueryParameters(uri.Query))
        {
            if (!IsRedirectParameterName(name)
                || string.IsNullOrWhiteSpace(value)
                || !Uri.TryCreate(value, UriKind.Absolute, out var destination))
            {
                continue;
            }

            if (destination.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || destination.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return destination;
            }
        }

        return null;
    }

    private static IEnumerable<(string Name, string Value)> EnumerateQueryParameters(string query)
    {
        var trimmed = query.TrimStart('?');
        foreach (var parameter in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equalsIndex = parameter.IndexOf('=', StringComparison.Ordinal);
            var rawName = equalsIndex < 0 ? parameter : parameter[..equalsIndex];
            var rawValue = equalsIndex < 0 ? "" : parameter[(equalsIndex + 1)..];
            yield return (SafeUnescapeQueryValue(rawName), SafeUnescapeQueryValue(rawValue));
        }
    }

    private static string SafeUnescapeQueryValue(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value).Replace('+', ' ');
        }
        catch (UriFormatException)
        {
            return value.Replace('+', ' ');
        }
    }

    private static bool IsRedirectParameterName(string name) =>
        name.Equals("url", StringComparison.OrdinalIgnoreCase)
        || name.Equals("u", StringComparison.OrdinalIgnoreCase)
        || name.Equals("redirect", StringComparison.OrdinalIgnoreCase)
        || name.Equals("redirect_url", StringComparison.OrdinalIgnoreCase)
        || name.Equals("target", StringComparison.OrdinalIgnoreCase)
        || name.Equals("target_url", StringComparison.OrdinalIgnoreCase)
        || name.Equals("destination", StringComparison.OrdinalIgnoreCase)
        || name.Equals("destination_url", StringComparison.OrdinalIgnoreCase)
        || name.Equals("r", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuspiciousLinkText(string visible, string href, string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        var visibleDomain = ExtractDomainLikeText(visible);
        if (string.IsNullOrWhiteSpace(visibleDomain))
        {
            return false;
        }

        return !visibleDomain.EndsWith(domain, StringComparison.OrdinalIgnoreCase)
            && !domain.EndsWith(visibleDomain, StringComparison.OrdinalIgnoreCase)
            && !href.Contains(visibleDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractDomainLikeText(string value)
    {
        var match = DomainLikeTextRegex().Match(value);
        return match.Success ? match.Value.TrimEnd('.', ',', ';', ':', ')').ToLowerInvariant() : null;
    }

    private static bool IsUnsubscribeLink(ExtractedLink link) =>
        ContainsAny(link.VisibleText, "unsubscribe", "manage preferences", "email preferences")
        || ContainsAny(link.Href, "unsubscribe", "preferences", "optout", "opt-out");

    private static QuotedSplit SplitQuotedConversation(string value)
    {
        var match = QuotedReplyRegex().Match(value);
        if (!match.Success)
        {
            return new QuotedSplit(value, null);
        }

        var current = value[..match.Index].Trim();
        var previous = value[match.Index..].Trim();
        return string.IsNullOrWhiteSpace(current)
            ? new QuotedSplit(value, null)
            : new QuotedSplit(current, previous);
    }

    private static string CleanBoilerplate(string value)
    {
        var lines = value.Split('\n');
        var kept = new List<string>(lines.Length);
        var inSignature = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                if (!inSignature)
                {
                    kept.Add("");
                }

                continue;
            }

            if (IsSignatureLine(line))
            {
                inSignature = true;
                continue;
            }

            if (ContainsActionTerm(line) || ContainsDeadlineLanguage(line))
            {
                inSignature = false;
                kept.Add(line);
                continue;
            }

            if (inSignature || IsBoilerplateLine(line))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    private static bool IsBoilerplateLine(string line)
    {
        if (line.Length > 500 && ContainsAny(line, "confidential", "intended recipient", "privileged"))
        {
            return true;
        }

        return ContainsAny(line, BoilerplateTerms);
    }

    private static bool IsSignatureLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("--", StringComparison.Ordinal)
            || trimmed.Equals("Sent from my iPhone", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Sender signature", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsDeadlineLanguage(string value) =>
        ContainsAny(value, "due", "deadline", "before", "by ", "entro", "scadenza", "urgent");

    private static bool ContainsActionTerm(string value) => ContainsAny(value, ActionLinkTerms);

    private static bool ContainsAny(string value, params string[] terms) => ContainsAny(value, (IReadOnlyList<string>)terms);

    private static bool ContainsAny(string value, IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
        {
            if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePlainText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return NormalizeLineEndings(WebUtility.HtmlDecode(text));
    }

    private static string NormalizeLineEndings(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        value = WebUtility.HtmlDecode(value)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u00a0', ' ')
            .Replace("\u034f", "", StringComparison.Ordinal)
            .Replace("\u00ad", "", StringComparison.Ordinal)
            .Replace("\u200b", "", StringComparison.Ordinal)
            .Replace("\u200c", "", StringComparison.Ordinal)
            .Replace("\u200d", "", StringComparison.Ordinal)
            .Replace("\u200e", "", StringComparison.Ordinal)
            .Replace("\u200f", "", StringComparison.Ordinal)
            .Replace("\ufeff", "", StringComparison.Ordinal)
            .Normalize(NormalizationForm.FormKC);
        value = CssCommentRegex().Replace(value, " ");
        value = CssRuleBlockRegex().Replace(value, " ");
        value = HorizontalWhitespaceRegex().Replace(value, " ");
        value = SpaceBeforePunctuationRegex().Replace(value, "$1");
        value = RemoveCssNoiseLines(value);
        value = BlankLinesRegex().Replace(value, "\n\n");
        return value.Trim();
    }

    private static string RemoveCssNoiseLines(string value)
    {
        var lines = value.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                kept.Add("");
                continue;
            }

            if (!IsCssNoiseLine(line))
            {
                kept.Add(line);
            }
        }

        return string.Join('\n', kept);
    }

    private static bool IsCssNoiseLine(string line)
    {
        if (line.StartsWith("@media", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("@supports", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("@font-face", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("@keyframes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (line.Contains('{', StringComparison.Ordinal)
            && line.Contains('}', StringComparison.Ordinal)
            && CssPropertyRegex().IsMatch(line))
        {
            return true;
        }

        return line.Length > 80
            && CssDeclarationFragmentRegex().Matches(line).Count >= 3;
    }

    private static string NormalizeInlineText(string value) =>
        SingleLineWhitespaceRegex().Replace(NormalizeLineEndings(value), " ").Trim();

    private static string ReplaceKnownInlineUrlsWithSourceReferences(
        string value,
        IReadOnlyList<ExtractedLink> extractedLinks)
    {
        if (string.IsNullOrWhiteSpace(value) || extractedLinks.Count == 0)
        {
            return value;
        }

        var sourceReferenceIds = extractedLinks
            .Select((link, index) => (Key: GetLinkKey(link), ReferenceId: index + 1))
            .ToDictionary(item => item.Key, item => item.ReferenceId, StringComparer.OrdinalIgnoreCase);

        return InlineUrlRegex().Replace(value, match =>
        {
            var candidate = match.Value;
            var trailing = "";
            while (candidate.Length > 0 && IsTrailingUrlPunctuation(candidate[^1]))
            {
                trailing = candidate[^1] + trailing;
                candidate = candidate[..^1];
            }

            var key = GetLinkKey(candidate, null);
            return key is not null && sourceReferenceIds.TryGetValue(key, out var referenceId)
                ? $"{InternalLinkReferencePrefix}{referenceId}]{trailing}"
                : match.Value;
        });
    }

    private static string CompactLongInlineUrls(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return InlineUrlRegex().Replace(value, match =>
        {
            var candidate = match.Value;
            if (candidate.Length < InlineUrlPlaceholderThreshold)
            {
                return candidate;
            }

            var trailing = "";
            while (candidate.Length > 0 && IsTrailingUrlPunctuation(candidate[^1]))
            {
                trailing = candidate[^1] + trailing;
                candidate = candidate[..^1];
            }

            if (candidate.Length < InlineUrlPlaceholderThreshold
                || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || string.IsNullOrWhiteSpace(uri.Host))
            {
                return match.Value;
            }

            return $"[link: {uri.Host}]{trailing}";
        });
    }

    private static bool IsTrailingUrlPunctuation(char value) =>
        value is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}';

    private static string TruncateText(string value, int maxCharacters)
    {
        if (value.Length <= maxCharacters)
        {
            return value;
        }

        if (maxCharacters <= 0)
        {
            return "";
        }

        const string marker = " [truncated]";
        if (maxCharacters <= marker.Length)
        {
            return value[..maxCharacters];
        }

        return value[..(maxCharacters - marker.Length)].TrimEnd() + marker;
    }

    [GeneratedRegex(@"(?im)(^|\n)\s*(On .+ wrote:|El .+ escribió:|Il .+ ha scritto:|Le .+ a écrit:|Am .+ schrieb:|From:\s.+\nSent:\s.+\nTo:\s.+\nSubject:\s.+|-----Original Message-----|_{5,}\s*)")]
    private static partial Regex QuotedReplyRegex();

    [GeneratedRegex(@"(?i)(display\s*:\s*none|visibility\s*:\s*hidden|opacity\s*:\s*0|mso-hide\s*:\s*all)")]
    private static partial Regex ConcealedStyleRegex();

    [GeneratedRegex(@"(?i)font-size\s*:\s*0")]
    private static partial Regex FontSizeZeroStyleRegex();

    [GeneratedRegex(@"(?i)width\s*:\s*([0-9]+)")]
    private static partial Regex WidthStyleRegex();

    [GeneratedRegex(@"(?i)height\s*:\s*([0-9]+)")]
    private static partial Regex HeightStyleRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SingleLineWhitespaceRegex();

    [GeneratedRegex(@"\s+([,.;:!?])")]
    private static partial Regex SpaceBeforePunctuationRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();

    [GeneratedRegex(@"(?i)\b(?:https?://)?(?:[a-z0-9-]+\.)+[a-z]{2,}\b")]
    private static partial Regex DomainLikeTextRegex();

    [GeneratedRegex(@"https?://[^\s<>""]+")]
    private static partial Regex InlineUrlRegex();

    [GeneratedRegex(@"\[link:(\d+)\]")]
    private static partial Regex NumberedLinkReferenceRegex();

    [GeneratedRegex(@"(?i)[a-z-]+\s*:\s*[^;{}]+[;}]")]
    private static partial Regex CssPropertyRegex();

    [GeneratedRegex(@"(?i)(^|[;{]\s*)[a-z-]+\s*:\s*[^;{}]+;")]
    private static partial Regex CssDeclarationFragmentRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssCommentRegex();

    [GeneratedRegex(@"(?i)(?:^|[\s;])(?:[.#]?[a-z_][\w.-]*|\*|[a-z][\w-]*)(?:\s*,\s*(?:[.#]?[a-z_][\w.-]*|\*|[a-z][\w-]*))*\s*\{[^{}]*\}")]
    private static partial Regex CssRuleBlockRegex();

    private sealed record HtmlConversionResult(string Text, IReadOnlyList<ExtractedLink> Links)
    {
        public static HtmlConversionResult Empty { get; } = new("", []);
    }

    private sealed record QuotedSplit(string CurrentMessage, string? PreviousConversation);
}

public static class EmailTextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var looksHtml = text.Contains('<', StringComparison.Ordinal) && text.Contains('>', StringComparison.Ordinal);
        var normalizer = new EmailContentNormalizer();
        var content = normalizer.Normalize(looksHtml
            ? new EmailContentInput(null, text)
            : new EmailContentInput(text, null));
        var singleLine = Regex.Replace(content.PlainText, @"\s+", " ").Trim();
        return singleLine.Length <= 12_000 ? singleLine : singleLine[..12_000];
    }
}
