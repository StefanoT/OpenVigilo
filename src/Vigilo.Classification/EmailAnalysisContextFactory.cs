using System.Security.Cryptography;
using System.Text;
using Vigilo.Core;

namespace Vigilo.Classification;

public interface IEmailAnalysisContextFactory
{
    EmailAnalysisContext Create(EmailMessage message, MailboxIdentity mailbox, TimeZoneInfo? timeZone = null);
}

public sealed class EmailAnalysisContextFactory(
    IEmailContentNormalizer normalizer,
    HarnessOptions options) : IEmailAnalysisContextFactory
{
    private const int MaximumSubjectCharacters = 512;

    public EmailAnalysisContext Create(EmailMessage message, MailboxIdentity mailbox, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(mailbox);

        var normalized = normalizer.Normalize(new EmailContentInput(
            message.OriginalTextBody ?? (EmailContentNormalizer.IsLlmPayload(message.NormalizedBody) ? null : message.NormalizedBody) ?? message.Snippet,
            message.OriginalHtmlBody));
        var (storedCurrent, storedHistory, storedHeaders) = ExtractStoredPayload(message.NormalizedBody);
        var current = string.IsNullOrWhiteSpace(normalized.PlainText) ? storedCurrent : normalized.PlainText;
        var history = normalized.PreviousConversationText ?? storedHistory;
        var metadata = CanonicalText(
            $"{storedHeaders}\nFolder: {message.Folder}\nFrom: {message.SenderName} <{message.SenderEmail}>\nReceived: {message.ReceivedAt:O}");
        var segments = new List<EmailSourceSegment>
        {
            new("subject", AnalysisSegmentKind.Subject, "Subject", Limit(CanonicalText(message.Subject), MaximumSubjectCharacters)),
            new("current-body", AnalysisSegmentKind.CurrentBody, "Current message body", CanonicalText(current))
        };

        if (!string.IsNullOrWhiteSpace(history))
        {
            segments.Add(new(
                "thread-history-1",
                AnalysisSegmentKind.ThreadHistory,
                "Earlier quoted thread content (supporting context only)",
                CanonicalText(history)));
        }

        segments.Add(new(
            "metadata",
            AnalysisSegmentKind.Metadata,
            "Non-body metadata; timestamp is reference context, never a deadline by itself",
            metadata));

        var budget = options.ResolveMaximumContextCharacters();
        var used = 0;
        var truncated = false;
        var budgeted = new List<EmailSourceSegment>(segments.Count);
        foreach (var segment in segments.OrderBy(s => s.Kind switch
                 {
                     AnalysisSegmentKind.Subject => 0,
                     AnalysisSegmentKind.Metadata => 1,
                     AnalysisSegmentKind.CurrentBody => 2,
                     _ => 3
                 }))
        {
            var remaining = Math.Max(0, budget - used);
            var text = segment.Text;
            var segmentTruncated = text.Length > remaining;
            if (segmentTruncated)
            {
                text = remaining == 0 ? "" : text[..remaining].TrimEnd();
                truncated = true;
            }

            used += text.Length;
            budgeted.Add(segment with { Text = text, WasTruncated = segmentTruncated });
        }

        budgeted = budgeted.OrderBy(s => segments.FindIndex(original => original.Id == s.Id)).ToList();
        var hashMaterial = string.Join("\n", new[]
        {
            mailbox.Address,
            string.Join(";", mailbox.Aliases.Order(StringComparer.OrdinalIgnoreCase)),
            message.ReceivedAt.ToString("O"),
            (timeZone ?? TimeZoneInfo.Local).Id,
            string.Join("\n", budgeted.Select(segment => $"{segment.Id}:{segment.Text}"))
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashMaterial)));
        return new EmailAnalysisContext(
            hash,
            message.ReceivedAt,
            timeZone ?? TimeZoneInfo.Local,
            mailbox,
            budgeted,
            truncated);
    }

    internal static string CanonicalText(string? value) =>
        StripEmphasisMarkers(SafeNormalize(EmailTextNormalizer.Normalize(value ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')))
        .Trim();

    /// <summary>
    /// Drops markdown emphasis characters and folds typographic punctuation so model-quoted
    /// rendered text matches stored markup bodies during deadline-expression grounding,
    /// mirroring the evidence locator.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex LinkReferenceMarker =
        new("\\[\\d+\\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripEmphasisMarkers(string value) =>
        LinkReferenceMarker.Replace(value
            .Replace("*", "").Replace("_", "").Replace("`", "").Replace("~", "")
            .Replace('‘', '\'').Replace('’', '\'')
            .Replace('“', '"').Replace('”', '"')
            .Replace('–', '-').Replace('—', '-'), "");

    /// <summary>
    /// Unicode normalization throws ArgumentException on lone surrogates, which damaged
    /// mail encodings can carry; strip them so canonicalization stays total.
    /// </summary>
    private static string SafeNormalize(string value)
    {
        string sanitized;
        if (value.Any(char.IsSurrogate))
        {
            var builder = new StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                    {
                        builder.Append(character).Append(value[index + 1]);
                        index++;
                    }

                    continue;
                }

                if (!char.IsLowSurrogate(character))
                {
                    builder.Append(character);
                }
            }

            sanitized = builder.ToString();
        }
        else
        {
            sanitized = value;
        }

        try
        {
            return sanitized.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return sanitized;
        }
    }

    private static string Limit(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters].TrimEnd();

    private static (string Current, string? History, string Headers) ExtractStoredPayload(string? payload)
    {
        if (!EmailContentNormalizer.IsLlmPayload(payload))
        {
            return ("", null, "");
        }

        var value = payload!.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        const string bodyHeader = "\nBody:\n";
        var bodyStart = value.IndexOf(bodyHeader, StringComparison.Ordinal);
        if (bodyStart < 0)
        {
            return ("", null, "");
        }

        var headers = value[..bodyStart].Split('\n')
            .Where(line => line.StartsWith("To:", StringComparison.Ordinal) || line.StartsWith("Cc:", StringComparison.Ordinal));
        bodyStart += bodyHeader.Length;
        var bodyEnd = value.Length;
        foreach (var section in new[] { "\n\nImportant links:", "\n\nAttachments:", "\n\nPrevious conversation:", "\n\nNormalization warnings:" })
        {
            var index = value.IndexOf(section, bodyStart, StringComparison.Ordinal);
            if (index >= 0 && index < bodyEnd) bodyEnd = index;
        }

        const string historyHeader = "\n\nPrevious conversation:\n";
        var historyStart = value.IndexOf(historyHeader, bodyStart, StringComparison.Ordinal);
        string? history = null;
        if (historyStart >= 0)
        {
            historyStart += historyHeader.Length;
            var historyEnd = value.IndexOf("\n\nNormalization warnings:", historyStart, StringComparison.Ordinal);
            history = value[historyStart..(historyEnd < 0 ? value.Length : historyEnd)].Trim();
        }

        return (value[bodyStart..bodyEnd].Trim(), history, string.Join("\n", headers));
    }
}
