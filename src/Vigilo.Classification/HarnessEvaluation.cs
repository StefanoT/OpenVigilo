using System.Diagnostics;
using System.Text.Json;
using Vigilo.Core;

namespace Vigilo.Classification;

public sealed record HarnessEvaluationExpected(
    bool RecipientAction,
    bool RequiredReply,
    bool Deadline,
    bool Escalation,
    bool Track);

public sealed record HarnessEvaluationFixture(
    string Id,
    string Language,
    string Mailbox,
    string Sender,
    string Subject,
    string Body,
    DateTimeOffset ReferenceTimestamp,
    HarnessEvaluationExpected Expected,
    string? ExpectedEvidence = null,
    string? SemanticNotes = null);

public sealed record EvaluationCounts(int TruePositive, int FalsePositive, int TrueNegative, int FalseNegative)
{
    public double Precision => Divide(TruePositive, TruePositive + FalsePositive);
    public double Recall => Divide(TruePositive, TruePositive + FalseNegative);
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
    private static double Divide(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;
}

public sealed record HarnessEvaluationReport(
    string ModelId,
    string HarnessVersion,
    string PolicyVersion,
    string PromptVersion,
    IReadOnlyDictionary<string, EvaluationCounts> Dimensions,
    double AverageLatencyMilliseconds,
    double P50LatencyMilliseconds,
    double P95LatencyMilliseconds,
    double AverageModelCalls,
    IReadOnlyList<string> FalsePositiveFixtureIds,
    IReadOnlyList<string> FalseNegativeFixtureIds);

public sealed record HarnessEvaluationComparison(
    string BaselineModelId,
    string CandidateModelId,
    IReadOnlyDictionary<string, double> PrecisionDelta,
    IReadOnlyDictionary<string, double> RecallDelta,
    IReadOnlyDictionary<string, double> F1Delta,
    double AverageLatencyDeltaMilliseconds,
    double AverageCallDelta);

public sealed class HarnessEvaluationRunner(
    IEmailAnalysisHarness harness,
    IEmailAnalysisContextFactory contextFactory)
{
    public async Task<HarnessEvaluationReport> RunJsonLinesAsync(Stream jsonLines, CancellationToken cancellationToken)
    {
        var fixtures = await ReadFixturesAsync(jsonLines, cancellationToken);
        if (fixtures.Count == 0)
        {
            throw new InvalidDataException("The evaluation dataset contains no fixtures.");
        }

        var observations = new List<Observation>(fixtures.Count);
        HarnessTrace? lastTrace = null;
        foreach (var fixture in fixtures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = new EmailMessage
            {
                Id = StableGuid(fixture.Id),
                AccountId = StableGuid(fixture.Mailbox),
                Folder = "Inbox",
                ProviderMessageId = fixture.Id,
                SenderEmail = fixture.Sender,
                Subject = fixture.Subject,
                ReceivedAt = fixture.ReferenceTimestamp,
                OriginalTextBody = fixture.Body,
                NormalizedBody = fixture.Body,
                Snippet = fixture.Body.Length <= 220 ? fixture.Body : fixture.Body[..220]
            };
            var context = contextFactory.Create(message, new MailboxIdentity(fixture.Mailbox, [fixture.Mailbox]));
            var stopwatch = Stopwatch.StartNew();
            var result = await harness.AnalyzeAsync(context, cancellationToken);
            stopwatch.Stop();
            lastTrace = result.Trace;
            var actual = new HarnessEvaluationExpected(
                result.Classification.HasUserSpecificObligation,
                result.Classification.UserReplyRequired,
                result.Classification.UserActionDeadline is not null,
                result.Classification.UserObligationMayEscalate,
                result.Classification.IsActionable || result.Classification.NeedsReview);
            observations.Add(new Observation(fixture, actual, stopwatch.Elapsed.TotalMilliseconds, result.Trace.ModelCalls));
        }

        var dimensions = new Dictionary<string, EvaluationCounts>(StringComparer.Ordinal)
        {
            ["recipientAction"] = Count(observations, item => item.Fixture.Expected.RecipientAction, item => item.Actual.RecipientAction),
            ["requiredReply"] = Count(observations, item => item.Fixture.Expected.RequiredReply, item => item.Actual.RequiredReply),
            ["deadline"] = Count(observations, item => item.Fixture.Expected.Deadline, item => item.Actual.Deadline),
            ["escalation"] = Count(observations, item => item.Fixture.Expected.Escalation, item => item.Actual.Escalation),
            ["track"] = Count(observations, item => item.Fixture.Expected.Track, item => item.Actual.Track)
        };
        var latencies = observations.Select(item => item.LatencyMilliseconds).Order().ToArray();
        return new HarnessEvaluationReport(
            lastTrace!.ModelId,
            lastTrace.HarnessVersion,
            lastTrace.PolicyVersion,
            lastTrace.PromptVersion,
            dimensions,
            latencies.Average(),
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            observations.Average(item => item.ModelCalls),
            observations.Where(item => !item.Fixture.Expected.Track && item.Actual.Track).Select(item => item.Fixture.Id).ToArray(),
            observations.Where(item => item.Fixture.Expected.Track && !item.Actual.Track).Select(item => item.Fixture.Id).ToArray());
    }

    public static HarnessEvaluationComparison Compare(HarnessEvaluationReport baseline, HarnessEvaluationReport candidate)
    {
        var keys = baseline.Dimensions.Keys.Intersect(candidate.Dimensions.Keys, StringComparer.Ordinal).ToArray();
        return new HarnessEvaluationComparison(
            baseline.ModelId,
            candidate.ModelId,
            keys.ToDictionary(key => key, key => candidate.Dimensions[key].Precision - baseline.Dimensions[key].Precision),
            keys.ToDictionary(key => key, key => candidate.Dimensions[key].Recall - baseline.Dimensions[key].Recall),
            keys.ToDictionary(key => key, key => candidate.Dimensions[key].F1 - baseline.Dimensions[key].F1),
            candidate.AverageLatencyMilliseconds - baseline.AverageLatencyMilliseconds,
            candidate.AverageModelCalls - baseline.AverageModelCalls);
    }

    private static async Task<IReadOnlyList<HarnessEvaluationFixture>> ReadFixturesAsync(Stream stream, CancellationToken cancellationToken)
    {
        var fixtures = new List<HarnessEvaluationFixture>();
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            fixtures.Add(JsonSerializer.Deserialize<HarnessEvaluationFixture>(line, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException("An evaluation fixture could not be parsed."));
        }

        return fixtures;
    }

    private static EvaluationCounts Count(
        IEnumerable<Observation> observations,
        Func<Observation, bool> expected,
        Func<Observation, bool> actual)
    {
        var tp = 0; var fp = 0; var tn = 0; var fn = 0;
        foreach (var item in observations)
        {
            var e = expected(item); var a = actual(item);
            if (e && a) tp++;
            else if (!e && a) fp++;
            else if (!e) tn++;
            else fn++;
        }

        return new EvaluationCounts(tp, fp, tn, fn);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static Guid StableGuid(string value)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes);
    }

    private sealed record Observation(
        HarnessEvaluationFixture Fixture,
        HarnessEvaluationExpected Actual,
        double LatencyMilliseconds,
        int ModelCalls);
}
