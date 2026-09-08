using System.Text;

namespace Vigilo.Classification;

public sealed class EvidenceLocator : IEvidenceLocator
{
    public bool TryResolve(
        EmailAnalysisContext context,
        EvidenceClaim claim,
        out ResolvedEvidence? evidence,
        out string? failureCode)
    {
        evidence = null;
        failureCode = null;
        if (claim is null)
        {
            failureCode = "InvalidEvidenceClaim";
            return false;
        }

        var segment = context.Segments.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, claim.SegmentId, StringComparison.OrdinalIgnoreCase));
        if (segment is not null)
        {
            if (TryResolveInSegment(segment, claim, out evidence, out failureCode))
            {
                return true;
            }

            // Models sometimes mislabel the segment (a subject quote tagged current-body).
            // The named segment failed, so probe the remaining segments before rejecting.
            failureCode = null;
        }

        var recovered = new List<ResolvedEvidence>();
        var fallbackFailures = new List<string>();
        foreach (var candidate in context.Segments)
        {
            if (ReferenceEquals(candidate, segment))
            {
                continue;
            }

            if (TryResolveInSegment(candidate, claim, out var candidateEvidence, out var candidateFailure))
            {
                recovered.Add(candidateEvidence!);
            }
            else if (candidateFailure is not null)
            {
                fallbackFailures.Add(candidateFailure);
            }
        }

        if (recovered.Count == 1)
        {
            evidence = recovered[0];
            return true;
        }

        failureCode = recovered.Count > 1 || fallbackFailures.Contains("EvidenceQuoteNotUnique", StringComparer.Ordinal)
            ? "EvidenceQuoteNotUnique"
            : segment is null ? "UnknownEvidenceSegment" : "EvidenceQuoteNotFound";
        return false;
    }

    private static bool TryResolveInSegment(
        EmailSourceSegment segment,
        EvidenceClaim claim,
        out ResolvedEvidence? evidence,
        out string? failureCode)
    {
        evidence = null;
        failureCode = null;
        var source = CanonicalizeWithMap(segment.Text);
        var quote = Canonicalize(claim.Quote);
        if (quote.Length == 0)
        {
            failureCode = "EmptyEvidenceQuote";
            return false;
        }

        var matches = new List<int>();
        for (var start = 0; start <= source.Text.Length - quote.Length; start++)
        {
            if (source.Text.AsSpan(start, quote.Length).SequenceEqual(quote.AsSpan()))
            {
                matches.Add(start);
            }
        }

        if (matches.Count == 0)
        {
            if (TryResolveWhitespaceInsensitive(source, quote, out var strippedStart, out var strippedEnd))
            {
                var strippedOriginalStart = source.Map[strippedStart];
                var strippedOriginalEnd = source.Map[Math.Min(source.Map.Count - 1, strippedEnd - 1)] + 1;
                evidence = new ResolvedEvidence(
                    segment.Id,
                    strippedOriginalStart,
                    Math.Max(1, strippedOriginalEnd - strippedOriginalStart),
                    claim.Role,
                    claim.Quote);
                return true;
            }

            if (!TryResolveSplicedQuote(segment.Id, source, claim, out var spliced))
            {
                failureCode = "EvidenceQuoteNotFound";
                return false;
            }

            evidence = spliced;
            return true;
        }

        // An ambiguous quote without an explicit occurrence anchors to its first match:
        // the grounding requirement is that the model copied real source text, which any
        // occurrence proves; only an explicit out-of-range occurrence is a model error.
        var occurrence = claim.Occurrence ?? 1;
        if (occurrence <= 0 || occurrence > matches.Count)
        {
            failureCode = "EvidenceQuoteNotUnique";
            return false;
        }

        var canonicalStart = matches[occurrence - 1];
        var originalStart = source.Map[canonicalStart];
        var originalEnd = source.Map[Math.Min(source.Map.Count - 1, canonicalStart + quote.Length - 1)] + 1;
        evidence = new ResolvedEvidence(
            segment.Id,
            originalStart,
            Math.Max(1, originalEnd - originalStart),
            claim.Role,
            claim.Quote);
        return true;
    }

    /// <summary>
    /// Stored bodies sometimes lose inter-word spaces entirely (HTML flattening damage).
    /// When the exact canonical quote is absent, retry with all spaces removed from both
    /// sides: the character sequence still proves the model copied real source text.
    /// Returns the canonical start and end (exclusive) of the match.
    /// </summary>
    private static bool TryResolveWhitespaceInsensitive(
        (string Text, IReadOnlyList<int> Map) source,
        string quote,
        out int canonicalStart,
        out int canonicalEnd)
    {
        canonicalStart = -1;
        canonicalEnd = -1;
        var strippedSource = source.Text.Replace(" ", "", StringComparison.Ordinal);
        var strippedQuote = quote.Replace(" ", "", StringComparison.Ordinal);
        if (strippedQuote.Length == 0)
        {
            return false;
        }

        var index = strippedSource.IndexOf(strippedQuote, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        // Walk the stripped string back onto canonical positions: the k-th non-space
        // character of the canonical text is stripped index k.
        var cursor = 0;
        for (var position = 0; position < index + strippedQuote.Length; position++)
        {
            while (cursor < source.Text.Length && source.Text[cursor] == ' ')
            {
                cursor++;
            }

            if (position == index)
            {
                canonicalStart = cursor;
            }

            if (position == index + strippedQuote.Length - 1)
            {
                canonicalEnd = cursor + 1;
            }

            cursor++;
        }

        return canonicalStart >= 0 && canonicalEnd > canonicalStart;
    }

    /// <summary>
    /// Models sometimes splice two real but non-adjacent sentences into one quote. When the
    /// whole quote fails, split it into sentence fragments: if every fragment matches the
    /// segment verbatim, the quote is grounded — anchor to the span from the first fragment
    /// to the last. Any missing fragment still fails the claim.
    /// </summary>
    private static bool TryResolveSplicedQuote(
        string segmentId,
        (string Text, IReadOnlyList<int> Map) source,
        EvidenceClaim claim,
        out ResolvedEvidence? evidence)
    {
        evidence = null;
        var fragments = SplitSentenceFragments(claim.Quote);
        if (fragments.Count < 2)
        {
            return false;
        }

        var first = -1;
        var lastEnd = -1;
        foreach (var fragment in fragments)
        {
            var index = source.Text.IndexOf(fragment, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            if (first < 0)
            {
                first = index;
            }

            lastEnd = Math.Max(lastEnd, index + fragment.Length);
        }

        var originalStart = source.Map[first];
        var originalEnd = source.Map[Math.Min(source.Map.Count - 1, lastEnd - 1)] + 1;
        evidence = new ResolvedEvidence(
            segmentId,
            originalStart,
            Math.Max(1, originalEnd - originalStart),
            claim.Role,
            claim.Quote);
        return true;
    }

    private static IReadOnlyList<string> SplitSentenceFragments(string quote)
    {
        var fragments = new List<string>();
        foreach (var piece in quote.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var start = 0;
            for (var index = 0; index < piece.Length - 1; index++)
            {
                if (piece[index] is '.' or '!' or '?' && piece[index + 1] == ' ')
                {
                    AddIfCanonicalNonEmpty(fragments, piece[start..(index + 1)]);
                    start = index + 1;
                }
            }

            AddIfCanonicalNonEmpty(fragments, piece[start..]);
        }

        return fragments;
    }

    private static void AddIfCanonicalNonEmpty(List<string> fragments, string candidate)
    {
        if (Canonicalize(candidate).Length > 0)
        {
            fragments.Add(Canonicalize(candidate));
        }
    }

    private static string Canonicalize(string value) => CanonicalizeWithMap(value).Text;

    private static (string Text, IReadOnlyList<int> Map) CanonicalizeWithMap(string value)
    {
        var text = new StringBuilder();
        var map = new List<int>();
        var pendingWhitespace = false;
        var suppressSpaceAfterBracket = false;
        for (var index = 0; index < value.Length; index++)
        {
            var sourceIndex = index;
            var character = value[index];
            if (IsEmphasisMarker(character))
            {
                continue;
            }

            if (character == '[' && TrySkipLinkReferenceMarker(value, ref index))
            {
                continue;
            }

            if (TryFoldPunctuation(character, out var folded))
            {
                if (pendingWhitespace && !suppressSpaceAfterBracket)
                {
                    text.Append(' ');
                    map.Add(sourceIndex);
                }

                pendingWhitespace = false;
                suppressSpaceAfterBracket = false;
                text.Append(folded);
                map.Add(sourceIndex);
                continue;
            }

            var normalized = NormalizeScalar(value, index, out var consumed);
            index += consumed - 1;
            foreach (var normalizedCharacter in normalized)
            {
                if (IsEmphasisMarker(normalizedCharacter))
                {
                    continue;
                }

                if (char.IsWhiteSpace(normalizedCharacter))
                {
                    pendingWhitespace = text.Length > 0;
                    continue;
                }

                if (pendingWhitespace && !suppressSpaceAfterBracket && !IsBracket(normalizedCharacter))
                {
                    text.Append(' ');
                    map.Add(sourceIndex);
                }

                pendingWhitespace = false;
                if (IsBracket(normalizedCharacter))
                {
                    // Whitespace adjacent to brackets and parentheses is insignificant:
                    // rendered link wrappers lose the inner spaces that stored markup
                    // keeps ("( [link:1] )" versus "([link:1])").
                    if (text.Length > 0 && text[^1] == ' ')
                    {
                        text.Length -= 1;
                        map.RemoveAt(map.Count - 1);
                    }
                }

                suppressSpaceAfterBracket = normalizedCharacter is '[' or '(';
                text.Append(normalizedCharacter);
                map.Add(sourceIndex);
            }
        }

        return (text.ToString().Trim(), map);
    }

    /// <summary>
    /// Folds typographic punctuation to its ASCII twin so model quotes match stored text
    /// that uses the other variant (curly vs straight apostrophes, en/em dashes).
    /// </summary>
    private static bool TryFoldPunctuation(char character, out char folded)
    {
        folded = character switch
        {
            '‘' or '’' => '\'',
            '“' or '”' => '"',
            '–' or '—' => '-',
            _ => character
        };
        return folded != character;
    }

    /// <summary>
    /// Skips inline link-reference markers ("[5]") present in stored bodies but absent from
    /// model quotes, where rendering showed a link chip instead. Sets <paramref name="index"/>
    /// to the closing bracket; the loop's increment moves past it.
    /// </summary>
    private static bool TrySkipLinkReferenceMarker(string value, ref int index)
    {
        var scan = index + 1;
        while (scan < value.Length && char.IsDigit(value[scan]))
        {
            scan++;
        }

        if (scan == index + 1 || scan >= value.Length || value[scan] != ']')
        {
            return false;
        }

        index = scan;
        return true;
    }

    /// <summary>
    /// Markdown emphasis markers survive normalization inside <see cref="NormalizeScalar"/>,
    /// so both sources and quotes drop them before matching: models quote rendered text
    /// ("later today at 10 AM PT") while stored bodies carry markup ("**later today**").
    /// </summary>
    private static bool IsEmphasisMarker(char character) =>
        character is '*' or '_' or '`' or '~';

    private static bool IsBracket(char character) =>
        character is '[' or ']' or '(' or ')';

    /// <summary>
    /// Normalizes one Unicode scalar (a BMP character or a surrogate pair). Mail bodies can
    /// carry lone surrogates from upstream encoding damage; <see cref="string.Normalize"/>
    /// throws ArgumentException on them, so they are dropped instead of failing the lookup.
    /// </summary>
    private static string NormalizeScalar(string value, int index, out int consumed)
    {
        var character = value[index];
        if (char.IsHighSurrogate(character))
        {
            if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                consumed = 2;
                return SafeNormalize(value.Substring(index, 2));
            }

            consumed = 1;
            return string.Empty;
        }

        consumed = 1;
        return char.IsLowSurrogate(character) ? string.Empty : SafeNormalize(character.ToString());
    }

    private static string SafeNormalize(string scalar)
    {
        try
        {
            return scalar.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return scalar;
        }
    }
}
