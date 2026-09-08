using Microsoft.Extensions.Logging;
using Vigilo.Core;

namespace Vigilo.Classification;

public sealed class EmailAnalysisHarness(
    SingleVerdictClassifier classifier,
    ILanguageModelInvoker invoker,
    IModelResponseCache cache,
    IPromptRegistry prompts,
    IHarnessPolicy policy,
    HarnessOptions options,
    ILogger<EmailAnalysisHarness> logger) : IEmailAnalysisHarness
{
    public const string CurrentVersion = "vigilo-single-verdict-v3";

    public async Task<EmailAnalysisResult> AnalyzeAsync(
        EmailAnalysisContext context,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.ResolvePerEmailTimeout());
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var session = new HarnessExecutionSession(invoker, cache, options);
        var result = await classifier.ExecuteAsync(context, session, options, linked.Token);
        cancellationToken.ThrowIfCancellationRequested();

        Vigilo.Core.ClassificationResult classification;
        string policyPath;
        if (result.IsUsable)
        {
            classification = policy.Synthesize(result.Verdict!, IsMateriallyUncertain(result.Verdict!), out policyPath);
        }
        else
        {
            policyPath = "technical-failure->deferred";
            logger.LogWarning(
                "Email single-verdict harness deferred classification. ContextHash={ContextHash} Status={Status} FailureCode={FailureCode}",
                context.ContextHash,
                result.Status,
                result.FailureCode ?? "None");
            classification = Vigilo.Core.ClassificationResult.UnavailableFallback(
                "Local semantic analysis could not produce a validated result.",
                Vigilo.Core.ClassificationFailureKind.UnusableModelOutput);
        }

        if (result.DowngradeCodes.Count > 0)
        {
            // Downgrades are the pipeline's silent actionability erasers: each one removed a
            // model claim (deadline, obligation, escalation) that failed validation. Surface
            // them for false-negative review of captured runs.
            logger.LogDebug(
                "Verdict consistency downgrades applied. ContextHash={ContextHash} Downgrades={Downgrades}",
                context.ContextHash,
                string.Join("|", result.DowngradeCodes));
        }

        var trace = new HarnessTrace(
            CurrentVersion,
            policy.Version,
            invoker.ModelId,
            prompts.ResolveCurrent(PromptIds.SingleVerdict).Version,
            result.Status,
            session.Calls,
            result.FailureCode,
            policyPath,
            result.CacheHit,
            result.Repaired,
            context.WasTruncated,
            timeout.IsCancellationRequested,
            result.DowngradeCodes);

        logger.LogInformation(
            "Email single-verdict harness completed. ContextHash={ContextHash} ModelId={ModelId} CallCount={CallCount} Status={Status} PolicyPath={PolicyPath} CacheHit={CacheHit} Repaired={Repaired} ContextTruncated={ContextTruncated} EmailTimedOut={EmailTimedOut}",
            context.ContextHash,
            invoker.ModelId,
            session.Calls,
            result.Status,
            policyPath,
            result.CacheHit,
            result.Repaired,
            context.WasTruncated,
            timeout.IsCancellationRequested);
        return new EmailAnalysisResult(classification, trace, result.Evidence);
    }

    // Uncertainty is material only when it touches recipient work: an unsure obligation or
    // reply must be reviewed, an unsure deadline matters once an obligation is confirmed,
    // and an unsure escalation matters only for confirmed work. Irrelevant uncertainty on
    // a clear negative must not turn a non-actionable promotion into a review item.
    private static bool IsMateriallyUncertain(SingleVerdictAssessment verdict)
    {
        var hasConfirmedRecipientWork = verdict.HasObligation == SemanticVerdict.Yes
            || verdict.RequiresReply == SemanticVerdict.Yes;
        return verdict.HasObligation == SemanticVerdict.Uncertain
            || verdict.RequiresReply == SemanticVerdict.Uncertain
            || (verdict.HasObligation == SemanticVerdict.Yes && verdict.HasDeadline == SemanticVerdict.Uncertain)
            || (hasConfirmedRecipientWork && verdict.MayEscalate == SemanticVerdict.Uncertain);
    }
}
