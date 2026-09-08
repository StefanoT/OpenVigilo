using Vigilo.Core;

namespace Vigilo.Classification;

public sealed class HarnessPolicy : IHarnessPolicy
{
    public const string CurrentVersion = "vigilo-harness-policy-v3";
    public string Version => CurrentVersion;

    public ClassificationResult Synthesize(
        SingleVerdictAssessment verdict,
        bool unresolvedMaterialUncertainty,
        out string policyPath)
    {
        if (unresolvedMaterialUncertainty)
        {
            policyPath = "semantic-uncertainty->needs-review";
            return ClassificationResult.NeedsReviewFallback(Reason(verdict)) with
            {
                MessageType = verdict.MessageType,
                ActionSummary = FirstNonEmpty(verdict.ActionSummary, "Review this email")
            };
        }

        var hasObligation = verdict.HasObligation == SemanticVerdict.Yes;
        var requiresReply = verdict.RequiresReply == SemanticVerdict.Yes;
        var isEscalation = verdict.MayEscalate == SemanticVerdict.Yes;
        var actionable = hasObligation || requiresReply || isEscalation;
        policyPath = actionable
            ? hasObligation ? "obligation->track" : requiresReply ? "required-reply->track" : "escalation->track"
            : "no-active-recipient-work->ignore";

        // Deadlines attach with or without an obligation: a deadline tied to required work
        // accompanies an obligation, while an offer expiry is surfaced on its own so the
        // user can see when a promotion or newsletter offer lapses.
        var hasLinkedDeadline = verdict.HasDeadline == SemanticVerdict.Yes
            && verdict.NormalizedDeadline is not null;
        return new ClassificationResult
        {
            IsActionable = actionable,
            MessageType = verdict.MessageType,
            HasUserSpecificObligation = hasObligation || requiresReply,
            UserReplyRequired = requiresReply,
            UserObligationMayEscalate = isEscalation,
            UserActionDeadline = hasLinkedDeadline ? verdict.NormalizedDeadline : null,
            UserActionDeadlineEvidence = hasLinkedDeadline ? verdict.DeadlineExpression : null,
            ActionSummary = actionable
                ? FirstNonEmpty(verdict.ActionSummary, "Review required email")
                : verdict.MessageType == ClassificationMessageType.Promotion
                    ? FirstNonEmpty(verdict.ActionSummary, "Commercial offer")
                    : "",
            Reason = Reason(verdict),
            Tags = []
        };
    }

    private static string Reason(SingleVerdictAssessment verdict)
    {
        if (!string.IsNullOrWhiteSpace(verdict.DecisionReason))
        {
            return verdict.DecisionReason.Trim();
        }

        var quote = (verdict.Evidence ?? []).OfType<EvidenceClaim>()
            .FirstOrDefault(evidence => evidence.Role == EvidenceRole.Primary)?.Quote;
        if (string.IsNullOrWhiteSpace(quote))
        {
            return "No current recipient obligation was established by the validated analysis.";
        }

        var trimmed = quote.Trim();
        if (trimmed.Length > 180)
        {
            trimmed = trimmed[..180].TrimEnd() + "…";
        }

        return $"Source evidence: \"{trimmed}\"";
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;
}
