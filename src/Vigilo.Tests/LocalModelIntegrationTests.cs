using Microsoft.Extensions.Logging.Abstractions;
using Vigilo.Classification;
using Vigilo.Core;
using Vigilo.LocalAi;

namespace Vigilo.Tests;

public sealed class LocalModelIntegrationTests
{
    [Fact]
    public async Task Installed_Gemma_model_with_thinking_returns_only_the_final_answer()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VIGILO_RUN_GEMMA_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var preset = Environment.GetEnvironmentVariable("VIGILO_GEMMA_PRESET");
        if (string.IsNullOrWhiteSpace(preset))
        {
            preset = ModelProfileCatalog.Gemma4E2BLiteRtPreset;
        }

        Assert.Contains(preset, new[]
        {
            ModelProfileCatalog.Gemma4E2BLiteRtPreset,
            ModelProfileCatalog.Gemma4E4BLiteRtPreset
        });

        var options = new ModelOptions
        {
            Preset = preset,
            InferenceTimeoutSeconds = 600
        };
        ModelProfileCatalog.ApplyPreset(options);
        using var client = new OnnxGenAiClient(
            new AvailableServerModelManager(options),
            new ReadyRuntimeSupervisor(),
            new LocalAiActivityTracker(),
            options,
            NullLogger<OnnxGenAiClient>.Instance);

        var output = await client.CompleteConversationAsync(
            [
                new LocalAiMessage(
                    LocalAiMessageRole.System,
                    "Return exactly one JSON object with a boolean property named hasUserSpecificObligation."),
                new LocalAiMessage(
                    LocalAiMessageRole.User,
                    "Classify this email: Weekly product newsletter. No reply or action is requested.")
            ],
            null,
            new LocalAiThinkingOptions(Enabled: true),
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(output));
        Assert.DoesNotContain("<|channel>thought", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<channel|>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Installed_local_model_returns_validated_single_verdict_classification()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("VIGILO_RUN_MODEL_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var options = CreateModelOptions();
        var missingFiles = options.RequiredFiles
            .Where(file => !File.Exists(Path.Combine(options.ModelRoot, file)))
            .ToArray();
        if (missingFiles.Length > 0)
        {
            return;
        }

        using var client = new OnnxGenAiClient(
            new InstalledModelManager(options),
            new ReadyRuntimeSupervisor(),
            new LocalAiActivityTracker(),
            options,
            NullLogger<OnnxGenAiClient>.Instance);
        var harnessOptions = new HarnessOptions
        {
            PerCallTimeout = TimeSpan.FromMinutes(10),
            PerEmailTimeout = TimeSpan.FromMinutes(40)
        };
        var prompts = new EmbeddedPromptRegistry();
        var evidence = new EvidenceLocator();
        var invoker = new SerializedLanguageModelInvoker(client, options, harnessOptions);
        var harness = new EmailAnalysisHarness(
            new SingleVerdictClassifier(prompts, invoker, evidence),
            invoker,
            new InMemoryModelResponseCache(harnessOptions),
            prompts,
            new HarnessPolicy(),
            harnessOptions,
            NullLogger<EmailAnalysisHarness>.Instance);
        var message = new EmailMessage
        {
            SenderName = "Clinic",
            SenderEmail = "clinic@example.com",
            Subject = "Please confirm your appointment",
            ReceivedAt = DateTimeOffset.UtcNow,
            Snippet = "Please confirm your appointment by tomorrow.",
            NormalizedBody = "Please confirm your appointment by tomorrow.",
            ThreadKey = "integration-test",
            LastScannedAt = DateTimeOffset.UtcNow
        };
        var context = new EmailAnalysisContextFactory(
            new EmailContentNormalizer(),
            harnessOptions).Create(
                message,
                new MailboxIdentity("owner@example.test", ["owner@example.test"]),
                TimeZoneInfo.Utc);

        var result = await harness.AnalyzeAsync(context, CancellationToken.None);

        Assert.False(result.Classification.ClassificationUnavailable, result.Classification.Reason);
        Assert.False(result.Classification.NeedsReview, result.Classification.Reason);
        Assert.True(result.Trace.Status is VerdictExecutionStatus.Valid or VerdictExecutionStatus.Uncertain);
        Assert.InRange(result.Trace.ModelCalls, 1, 2);
    }

    private sealed class ReadyRuntimeSupervisor : ILocalAiRuntimeSupervisor
    {
        public event EventHandler<LocalAiRuntimeStatus>? StatusChanged
        {
            add { }
            remove { }
        }
        public LocalAiRuntimeStatus CurrentStatus { get; } = new(LocalAiRuntimeState.Ready, "Ready.");
        public Task<bool> EnsureReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static ModelOptions CreateModelOptions()
    {
        var options = new ModelOptions();
        options.InferenceTimeoutSeconds = 600;
        var configuredRoot = Environment.GetEnvironmentVariable("VIGILO_MODEL_ROOT");
        options.ModelRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vigilo",
                "Models",
                options.Version)
            : configuredRoot;
        return options;
    }

    private sealed class InstalledModelManager(ModelOptions options) : IModelManager
    {
        public Task<ModelStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            var missingFiles = options.RequiredFiles
                .Where(file => !File.Exists(Path.Combine(options.ModelRoot, file)))
                .ToArray();
            return Task.FromResult(new ModelStatus(
                missingFiles.Length == 0,
                options.ModelRoot,
                options.Version,
                missingFiles.Length == 0 ? "Model is installed." : "Model files are missing.",
                missingFiles));
        }

        public Task<ModelInstallResult> EnsureModelAsync(CancellationToken cancellationToken, IProgress<string>? progress = null) =>
            throw new NotSupportedException();
    }

    private sealed class AvailableServerModelManager(ModelOptions options) : IModelManager
    {
        public Task<ModelStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ModelStatus(
                true,
                options.EndpointBaseUrl,
                options.Version,
                "LiteRT-LM model is available.",
                []));

        public Task<ModelInstallResult> EnsureModelAsync(
            CancellationToken cancellationToken,
            IProgress<string>? progress = null) => throw new NotSupportedException();
    }
}
