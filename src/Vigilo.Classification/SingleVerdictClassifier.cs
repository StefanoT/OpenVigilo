using System.Text.Json;
using JsonRepairSharp;
using Vigilo.Core;

namespace Vigilo.Classification;

/// <summary>
/// Executes the one model call that classifies an email: renders the segmented context,
/// invokes the model with the verdict schema and thinking options, parses and grounds the
/// response, and spends one bounded repair when validation fails.
/// </summary>
public sealed class SingleVerdictClassifier(
    IPromptRegistry prompts,
    ILanguageModelInvoker invoker,
    IEvidenceLocator evidenceLocator)
{
    private const string CacheDependency = "single-verdict";

    public async Task<VerdictExecutionResult> ExecuteAsync(
        EmailAnalysisContext context,
        HarnessExecutionSession session,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        var prompt = prompts.ResolveCurrent(PromptIds.SingleVerdict);
        var key = HarnessExecutionSession.CacheKey(context, invoker.ModelId, prompt, new { call = CacheDependency });
        var userPrompt = context.RenderForPrompt();
        var invocation = await session.InvokeAsync(
            key,
            prompt.Template,
            userPrompt,
            cancellationToken,
            responseJsonSchema: VerdictResponseSchema.SingleVerdict,
            thinking: options.Thinking);
        if (invocation.Raw is null)
        {
            return Failure(invocation.Status, invocation.FailureCode, invocation.CacheHit);
        }

        var downgrades = new List<string>();
        var validationFailure = TryParseAndValidate(
            invocation.Raw,
            context,
            downgrades,
            out var verdict,
            out var resolved);
        var repaired = false;
        var raw = invocation.Raw;
        if (validationFailure is not null)
        {
            var repairPrompt = $"""
                {userPrompt}

                Generate a fresh response that satisfies the exact JSON contract in the system prompt. Do not add reasoning or markdown. Do not repeat the previous response.
                If you cannot copy a short exact verbatim quote from the source segments that proves a positive conclusion, return "uncertain" for that field instead of inventing evidence.
                Validation failure: {validationFailure}
                """;
            var repair = await session.InvokeAsync(
                key + ":repair",
                prompt.Template,
                repairPrompt,
                cancellationToken,
                isRepair: true,
                responseJsonSchema: VerdictResponseSchema.SingleVerdict,
                thinking: options.Thinking);
            if (repair.Raw is null)
            {
                return Failure(repair.Status, repair.FailureCode ?? validationFailure, false, repaired: true);
            }

            raw = repair.Raw;
            repaired = true;
            validationFailure = TryParseAndValidate(repair.Raw, context, downgrades, out verdict, out resolved);
            if (validationFailure is not null || verdict is null)
            {
                return Failure(VerdictExecutionStatus.InvalidResponse,
                    validationFailure ?? "InvalidStructuredResponse", false, repaired: true);
            }
        }

        session.Store(key, raw);
        var status = IsUncertain(verdict!) ? VerdictExecutionStatus.Uncertain : VerdictExecutionStatus.Valid;
        return new VerdictExecutionResult(status, verdict, resolved, null, invocation.CacheHit, repaired, downgrades);
    }

    private static VerdictExecutionResult Failure(
        VerdictExecutionStatus status,
        string? code,
        bool cacheHit,
        bool repaired = false) =>
        new(status, null, [], code, cacheHit, repaired);

    private static bool HasPositiveConclusion(SingleVerdictAssessment verdict) =>
        verdict.HasObligation == SemanticVerdict.Yes
        || verdict.RequiresReply == SemanticVerdict.Yes
        || verdict.MayEscalate == SemanticVerdict.Yes
        || verdict.HasDeadline == SemanticVerdict.Yes;

    private static bool IsUncertain(SingleVerdictAssessment verdict) =>
        verdict.HasObligation == SemanticVerdict.Uncertain
        || verdict.RequiresReply == SemanticVerdict.Uncertain
        || verdict.MayEscalate == SemanticVerdict.Uncertain
        || verdict.HasDeadline == SemanticVerdict.Uncertain;

    private string? Validate(
        SingleVerdictAssessment verdict,
        EmailAnalysisContext context,
        out IReadOnlyList<ResolvedEvidence> resolved)
    {
        resolved = [];
        // Only an actionable positive (obligation, reply, escalation) demands strict
        // evidence. A deadline without work is display-only: unverifiable claims are
        // dropped there and the deadline then fails its expression-grounding check and
        // is downgraded, instead of deferring the whole email.
        var isActionable = verdict.HasObligation == SemanticVerdict.Yes
            || verdict.RequiresReply == SemanticVerdict.Yes
            || verdict.MayEscalate == SemanticVerdict.Yes;

        var items = new List<ResolvedEvidence>();
        foreach (var claim in (verdict.Evidence ?? []).OfType<EvidenceClaim>().Where(IsWellFormedEvidence))
        {
            if (!evidenceLocator.TryResolve(context, claim, out var match, out var failure))
            {
                if (!isActionable)
                {
                    continue;
                }

                return failure;
            }

            items.Add(match!);
        }

        if (isActionable)
        {
            var primary = items.Where(item => item.Role == EvidenceRole.Primary).ToArray();
            if (primary.Length == 0)
            {
                return "PositiveConclusionMissingPrimaryEvidence";
            }

            if (primary.All(item => context.Segments.Single(segment => segment.Id == item.SegmentId).Kind
                    is AnalysisSegmentKind.ThreadHistory or AnalysisSegmentKind.Metadata))
            {
                return "PositiveConclusionHasOnlyHistoricalOrMetadataEvidence";
            }
        }

        resolved = items;
        return ValidateSemantic(verdict, context, resolved);
    }

    private static string? ValidateSemantic(
        SingleVerdictAssessment verdict,
        EmailAnalysisContext context,
        IReadOnlyList<ResolvedEvidence> resolved)
    {
        if (verdict.MessageType == ClassificationMessageType.Newsletter
            && (verdict.RequiresReply == SemanticVerdict.Yes || verdict.HasObligation == SemanticVerdict.Yes))
        {
            return "NewsletterReplyIsEngagement";
        }

        if (verdict.RequiresReply == SemanticVerdict.Yes && verdict.HasObligation == SemanticVerdict.No)
        {
            return "ReplyRequiresObligation";
        }

        if (verdict.HasDeadline != SemanticVerdict.Yes)
        {
            return verdict.NormalizedDeadline is null ? null : "NonPositiveDeadlineHasNormalizedValue";
        }

        if (string.IsNullOrWhiteSpace(verdict.DeadlineExpression))
        {
            return "DeadlineMissingSourceExpression";
        }

        if (verdict.NormalizedDeadline is null)
        {
            return "DeadlineMissingNormalizedValue";
        }

        var canonicalExpression = EmailAnalysisContextFactory.CanonicalText(verdict.DeadlineExpression);
        if (resolved.Count == 0 || resolved.All(item => !EmailAnalysisContextFactory.CanonicalText(item.Quote)
                .Contains(canonicalExpression, StringComparison.Ordinal)))
        {
            return "DeadlineExpressionNotGroundedByEvidence";
        }

        return verdict.NormalizedDeadline == context.ReferenceTimestamp
            ? "DeadlineEqualsMetadataTimestamp"
            : null;
    }

    private static bool IsWellFormedEvidence(EvidenceClaim claim) =>
        !string.IsNullOrWhiteSpace(claim.Quote);

    /// <summary>
    /// Cross-field verdict rules the local model violates most often: replying requires a
    /// confirmed obligation, a non-positive deadline must not carry a normalized value, and
    /// deadline claims without a normalized date or without a grounded expression cannot be
    /// used. Instead of failing the whole response, downgrade those fields — the
    /// conservative direction, because it can only remove actionability, never invent it.
    /// </summary>
    private static readonly string[] DowngradableConsistencyFailures =
    [
        "NewsletterReplyIsEngagement",
        "ReplyRequiresObligation",
        "NonPositiveDeadlineHasNormalizedValue",
        "DeadlineMissingNormalizedValue",
        "DeadlineExpressionNotGroundedByEvidence",
        "DeadlineEqualsMetadataTimestamp",
        "PositiveConclusionHasOnlyHistoricalOrMetadataEvidence"
    ];

    private static SingleVerdictAssessment? NormalizeConsistency(
        SingleVerdictAssessment verdict,
        string validationFailure)
    {
        var requiresReply = verdict.RequiresReply;
        var hasObligation = verdict.HasObligation;
        var mayEscalate = verdict.MayEscalate;
        var hasDeadline = verdict.HasDeadline;
        string? deadlineExpression = verdict.DeadlineExpression;
        var normalizedDeadline = verdict.NormalizedDeadline;

        if (validationFailure == "NewsletterReplyIsEngagement")
        {
            // A newsletter's engagement CTA ("reply and tell me how it went") is optional
            // engagement per the reviewed policy: the model keeps claiming it as required
            // communication even against the prompt, so the deterministic layer removes the
            // actionability it can never invent. Escalation without work is meaningless.
            requiresReply = SemanticVerdict.No;
            hasObligation = SemanticVerdict.No;
            mayEscalate = SemanticVerdict.No;
        }
        else if (validationFailure == "ReplyRequiresObligation"
            && verdict.RequiresReply == SemanticVerdict.Yes
            && verdict.HasObligation != SemanticVerdict.Yes)
        {
            requiresReply = verdict.HasObligation;
        }
        else if (validationFailure == "NonPositiveDeadlineHasNormalizedValue")
        {
            normalizedDeadline = null;
        }
        else if (validationFailure is "DeadlineMissingNormalizedValue"
            or "DeadlineExpressionNotGroundedByEvidence"
            or "DeadlineEqualsMetadataTimestamp")
        {
            // A deadline claim without a normalized date, whose expression cannot be
            // located verbatim inside the cited evidence, or copied from the email's
            // metadata timestamp is unverifiable or fabricated: drop it whole.
            hasDeadline = SemanticVerdict.No;
            deadlineExpression = null;
            normalizedDeadline = null;
        }
        else if (validationFailure == "PositiveConclusionHasOnlyHistoricalOrMetadataEvidence")
        {
            // The model grounded its obligation only in quoted history or metadata, which
            // cannot prove current work. That is uncertainty about a live obligation, not
            // a technical failure: express it as an explicit review need instead of
            // burning repair calls on the same mistake.
            hasObligation = SemanticVerdict.Uncertain;
            if (requiresReply == SemanticVerdict.Yes)
            {
                requiresReply = SemanticVerdict.Uncertain;
            }

            if (verdict.MayEscalate == SemanticVerdict.Yes)
            {
                mayEscalate = SemanticVerdict.Uncertain;
            }

            // The evidence claims will be dropped with the now-non-positive verdict, so the
            // deadline loses its grounding anchors in the same pass.
            hasDeadline = SemanticVerdict.No;
            deadlineExpression = null;
            normalizedDeadline = null;
        }
        else if (verdict.HasDeadline != SemanticVerdict.Yes && verdict.NormalizedDeadline is not null)
        {
            normalizedDeadline = null;
        }

        return requiresReply == verdict.RequiresReply
            && hasObligation == verdict.HasObligation
            && mayEscalate == verdict.MayEscalate
            && hasDeadline == verdict.HasDeadline
            && deadlineExpression == verdict.DeadlineExpression
            && normalizedDeadline == verdict.NormalizedDeadline
            ? null
            : verdict with
            {
                RequiresReply = requiresReply,
                HasObligation = hasObligation,
                MayEscalate = mayEscalate,
                HasDeadline = hasDeadline,
                DeadlineExpression = deadlineExpression,
                NormalizedDeadline = normalizedDeadline
            };
    }

    /// <summary>
    /// Local models frequently attach a wrong UTC offset to a normalized deadline while
    /// getting the calendar date right (64% of captured claims were off). When the claimed
    /// time sits on a date-boundary sentinel — start of day or 11:59pm — re-anchor it to
    /// the same local date and time in the reference timezone of the Windows PC. Other
    /// times are left untouched because the source clock time cannot be recovered
    /// reliably from a wrong instant.
    /// </summary>
    private static SingleVerdictAssessment ReanchorDeadline(SingleVerdictAssessment verdict, EmailAnalysisContext context)
    {
        if (verdict.NormalizedDeadline is not { } deadline)
        {
            return verdict;
        }

        var timeOfDay = deadline.TimeOfDay;
        var isSentinel = timeOfDay == TimeSpan.Zero
            || (timeOfDay.Hours == 23 && timeOfDay.Minutes == 59);
        if (!isSentinel)
        {
            return verdict;
        }

        // Anchor on the local date and recompute the offset from the reference timezone so
        // daylight-saving boundaries resolve for that specific date.
        var localDate = new DateTime(deadline.Year, deadline.Month, deadline.Day, deadline.Hour, deadline.Minute, deadline.Second);
        var referenceOffset = context.TimeZone.GetUtcOffset(localDate);
        var reanchored = new DateTimeOffset(localDate, referenceOffset);
        return reanchored == deadline ? verdict : verdict with { NormalizedDeadline = reanchored };
    }

    private string? ValidateSafely(
        SingleVerdictAssessment verdict,
        EmailAnalysisContext context,
        out IReadOnlyList<ResolvedEvidence> resolved)
    {
        try
        {
            return Validate(verdict, context, out resolved);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException or ArgumentException)
        {
            resolved = [];
            return "InvalidStructuredResponse";
        }
    }

    private string? TryParseAndValidate(
        string raw,
        EmailAnalysisContext context,
        List<string> downgrades,
        out SingleVerdictAssessment? verdict,
        out IReadOnlyList<ResolvedEvidence> resolved)
    {
        verdict = null;
        resolved = [];
        string? firstValidationFailure = null;
        string? firstDeserializationFailure = null;
        foreach (var candidate in ExtractObjects(raw))
        {
            if (!TryDeserialize(candidate, out var parsed, out var deserializationFailure) || parsed is null)
            {
                firstDeserializationFailure ??= deserializationFailure;
                continue;
            }

            parsed = ReanchorDeadline(parsed, context);
            var validationFailure = ValidateSafely(parsed, context, out var candidateResolved);
            for (var pass = 0;
                validationFailure is not null
                && DowngradableConsistencyFailures.Contains(validationFailure, StringComparer.Ordinal);
                pass++)
            {
                if (pass >= 3)
                {
                    break;
                }

                var normalized = NormalizeConsistency(parsed, validationFailure!);
                if (normalized is null)
                {
                    break;
                }

                downgrades.Add(validationFailure!);
                parsed = normalized;
                validationFailure = ValidateSafely(parsed, context, out candidateResolved);
            }

            if (validationFailure is null)
            {
                verdict = parsed;
                resolved = candidateResolved;
                return null;
            }

            verdict ??= parsed;
            firstValidationFailure ??= validationFailure;
        }

        return firstValidationFailure ?? firstDeserializationFailure ?? "InvalidStructuredResponse";
    }

    private static bool TryDeserialize(string candidate, out SingleVerdictAssessment? verdict, out string? failureCode)
    {
        verdict = null;
        failureCode = null;
        try
        {
            verdict = JsonSerializer.Deserialize<SingleVerdictAssessment>(candidate, VerdictJson.Options);
            return verdict is not null;
        }
        catch (JsonException initialException)
        {
            try
            {
                var repaired = JsonRepair.RepairJson(candidate, JsonRepair.InputType.LLM, throwsException: true);
                verdict = JsonSerializer.Deserialize<SingleVerdictAssessment>(repaired, VerdictJson.Options);
                return verdict is not null;
            }
            catch (JsonException repairedException)
            {
                failureCode = DescribeJsonFailure(repairedException);
                return false;
            }
            catch (Exception)
            {
                failureCode = DescribeJsonFailure(initialException);
                return false;
            }
        }
    }

    private static string DescribeJsonFailure(JsonException exception) =>
        string.IsNullOrWhiteSpace(exception.Path)
            ? "JsonDeserializeFailed"
            : $"JsonDeserializeFailedAt{exception.Path.Replace('.', '-').Replace('[', '-').Replace(']', '-')}";

    private static IReadOnlyList<string> ExtractObjects(string raw)
    {
        var candidates = new List<string>();
        var start = -1;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (start < 0)
            {
                if (character == '{')
                {
                    start = index;
                    depth = 1;
                }

                continue;
            }

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                candidates.Add(raw[start..(index + 1)]);
                start = -1;
            }
        }

        if (start >= 0)
        {
            candidates.Add(raw[start..]);
        }

        return candidates.Count == 0 ? [raw] : candidates;
    }
}
