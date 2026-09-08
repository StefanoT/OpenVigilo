using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vigilo.Core;

namespace Vigilo.Classification;

public sealed class HarnessMessageClassifier(
    IEmailAnalysisHarness harness,
    IEmailAnalysisContextFactory contextFactory,
    IAccountSettingsService accountSettings,
    ILogger<HarnessMessageClassifier> logger) : IMessageClassifier
{
#if DEBUG
    private static readonly object DebugDecisionCaptureGate = new();
#endif

    public async Task<ClassificationResult> ClassifyAsync(
        EmailMessage message,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        progress?.Report("Preparing semantic analysis context");
        var account = await accountSettings.GetAccountAsync(message.AccountId, cancellationToken);
        var address = account?.EmailAddress ?? "mailbox-owner";
        var aliases = new[] { address, account?.Username }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
        var context = contextFactory.Create(message, new MailboxIdentity(address, aliases));
        progress?.Report("Running semantic analysis graph");
        var result = await harness.AnalyzeAsync(context, cancellationToken);
        logger.LogInformation(
            "Harness classification mapped to domain result. MessageId={MessageId} PolicyPath={PolicyPath} Calls={Calls} Actionable={Actionable} NeedsReview={NeedsReview}",
            message.Id,
            result.Trace.PolicyPath,
            result.Trace.ModelCalls,
            result.Classification.IsActionable,
            result.Classification.NeedsReview);
        CaptureDebugDecision(message, context, result);
        return result.Classification;
    }

    /// <summary>
    /// Debug-only per-email decision record. The inference capture holds what the model saw
    /// and answered, but false-negative review needs the opposite view in one greppable
    /// place: the final verdict for every email, the model's own reason, the resolved
    /// evidence, a bounded source excerpt, and every deterministic consistency downgrade —
    /// the pipeline step that can silently remove model-claimed actionability.
    /// </summary>
    private void CaptureDebugDecision(EmailMessage message, EmailAnalysisContext context, EmailAnalysisResult result)
    {
#if DEBUG
        if (AppContext.GetData("Vigilo.DebugDecisionCapturePath") is not string path
            || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var classification = result.Classification;
            var trace = result.Trace;
            var bodyExcerpt = context.Segments.FirstOrDefault(
                    segment => segment.Kind == AnalysisSegmentKind.CurrentBody)?.Text ?? "";
            if (bodyExcerpt.Length > 400)
            {
                bodyExcerpt = bodyExcerpt[..400];
            }

            var record = new
            {
                capturedAtUtc = DateTimeOffset.UtcNow,
                messageId = message.Id,
                accountId = message.AccountId,
                folder = message.Folder,
                providerMessageId = message.ProviderMessageId,
                mailboxOwner = context.Mailbox.Address,
                sender = string.IsNullOrWhiteSpace(message.SenderName)
                    ? message.SenderEmail
                    : $"{message.SenderName} <{message.SenderEmail}>",
                subject = message.Subject,
                receivedAt = message.ReceivedAt,
                messageType = classification.MessageType.ToString(),
                isActionable = classification.IsActionable,
                needsReview = classification.NeedsReview,
                hasObligation = classification.HasUserSpecificObligation,
                requiresReply = classification.UserReplyRequired,
                mayEscalate = classification.UserObligationMayEscalate,
                deadline = classification.UserActionDeadline,
                classificationUnavailable = classification.ClassificationUnavailable,
                status = trace.Status.ToString(),
                policyPath = trace.PolicyPath,
                failureCode = trace.FailureCode,
                modelCalls = trace.ModelCalls,
                cacheHit = trace.CacheHit,
                repaired = trace.Repaired,
                contextTruncated = trace.ContextTruncated,
                emailTimedOut = trace.EmailTimedOut,
                downgrades = trace.DowngradeCodes,
                actionSummary = classification.ActionSummary,
                decisionReason = classification.Reason,
                evidence = result.EvidenceItems.Select(item => new
                {
                    segmentId = item.SegmentId,
                    role = item.Role.ToString(),
                    quote = item.Quote
                }),
                bodyExcerpt
            };
            var line = JsonSerializer.Serialize(record) + Environment.NewLine;
            lock (DebugDecisionCaptureGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Diagnostics must never fail classification.
            logger.LogWarning(
                exception,
                "Debug decision capture write failed and was ignored. CapturePath={CapturePath}",
                path);
        }
#endif
    }
}
