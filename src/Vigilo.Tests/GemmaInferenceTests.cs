using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vigilo.Core;
using Vigilo.LocalAi;

namespace Vigilo.Tests;

public sealed class GemmaInferenceTests
{
    private const string FinalAnswer = "{\"hasUserSpecificObligation\":false}";

    [Theory]
    [InlineData(ModelProfileCatalog.Gemma4E2BLiteRtPreset)]
    [InlineData(ModelProfileCatalog.Gemma4E4BLiteRtPreset)]
    public async Task Gemma_requests_without_thinking_options_keep_thinking_disabled(string preset)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(FinalAnswer)));
        using var client = CreateClient(preset, handler);
        var messages = Conversation();

        for (var requestIndex = 0; requestIndex < 6; requestIndex++)
        {
            Assert.Equal(FinalAnswer, await client.CompleteConversationAsync(messages, CancellationToken.None));
        }

        Assert.Equal(6, handler.RequestBodies.Count);
        foreach (var requestBody in handler.RequestBodies)
        {
            using var request = JsonDocument.Parse(requestBody);
            var root = request.RootElement;
            Assert.Equal("none", root.GetProperty("reasoning_effort").GetString());
            var enableThinking = root
                .GetProperty("extra_context")
                .GetProperty("enable_thinking");
            Assert.Equal(JsonValueKind.False, enableThinking.ValueKind);
            Assert.False(enableThinking.GetBoolean());
            Assert.Equal(client.InferenceCapabilities.MaximumOutputTokens, root.GetProperty("max_tokens").GetInt32());
        }
    }

    [Theory]
    [InlineData(ModelProfileCatalog.Gemma4E2BLiteRtPreset)]
    [InlineData(ModelProfileCatalog.Gemma4E4BLiteRtPreset)]
    public async Task Gemma_requests_with_thinking_enable_it_and_reserve_the_answer_budget(string preset)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(FinalAnswer)));
        using var client = CreateClient(preset, handler);

        await client.CompleteConversationAsync(
            Conversation(),
            null,
            new LocalAiThinkingOptions(Enabled: true),
            CancellationToken.None);

        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = request.RootElement;
        Assert.Equal("low", root.GetProperty("reasoning_effort").GetString());
        Assert.True(root.GetProperty("extra_context").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(
            client.InferenceCapabilities.MaximumOutputTokens + 256,
            root.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Gemma_thinking_options_control_effort_and_thinking_budget()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(FinalAnswer)));
        using var client = CreateClient(ModelProfileCatalog.Gemma4E2BLiteRtPreset, handler);

        await client.CompleteConversationAsync(
            Conversation(),
            null,
            new LocalAiThinkingOptions(Enabled: true, ReasoningEffort: "medium", MaximumThinkingTokens: 128),
            CancellationToken.None);

        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = request.RootElement;
        Assert.Equal("medium", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal(
            client.InferenceCapabilities.MaximumOutputTokens + 128,
            root.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Ordinary_Gemma_final_answer_is_returned_unchanged()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(FinalAnswer)));
        using var client = CreateClient(ModelProfileCatalog.Gemma4E2BLiteRtPreset, handler);

        var result = await client.CompleteConversationAsync(Conversation(), CancellationToken.None);

        Assert.Equal(FinalAnswer, result);
    }

    [Fact]
    public async Task Non_empty_Gemma_thought_channel_is_excluded_from_the_answer_without_leaking()
    {
        const string privateReasoning = "PRIVATE_REASONING_MUST_NOT_LEAK";
        var generated = $"<|channel>thought\n{privateReasoning}<channel|>{FinalAnswer}";
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(generated)));
        var logger = new RecordingLogger<OnnxGenAiClient>();
        using var client = CreateClient(ModelProfileCatalog.Gemma4E2BLiteRtPreset, handler, logger);

        var result = await client.CompleteConversationAsync(
            Conversation(),
            null,
            new LocalAiThinkingOptions(Enabled: true),
            CancellationToken.None);

        Assert.Equal(FinalAnswer, result);
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Message.Contains(privateReasoning, StringComparison.Ordinal)
            || entry.ExceptionMessage.Contains(privateReasoning, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_Gemma_thought_channel_does_not_corrupt_the_final_answer()
    {
        var generated = $"<|channel>thought\n<channel|>{FinalAnswer}";
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(generated)));
        using var client = CreateClient(ModelProfileCatalog.Gemma4E4BLiteRtPreset, handler);

        var result = await client.CompleteConversationAsync(
            Conversation(),
            null,
            new LocalAiThinkingOptions(Enabled: true),
            CancellationToken.None);

        Assert.Equal(FinalAnswer, result);
    }

    [Fact]
    public async Task Malformed_Gemma_thought_channel_returns_content_free_structured_failure()
    {
        const string privateReasoning = "PRIVATE_REASONING_MUST_NOT_LEAK";
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(
            $"<|channel>thought\n{privateReasoning}")));
        using var client = CreateClient(ModelProfileCatalog.Gemma4E2BLiteRtPreset, handler);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.CompleteConversationAsync(Conversation(), CancellationToken.None));

        Assert.DoesNotContain(privateReasoning, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_Gemma_OpenAi_request_does_not_receive_Gemma_template_options()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(FinalAnswer)));
        using var client = CreateClient(ModelProfileCatalog.Phi4MiniPreset, handler);

        await client.CompleteConversationAsync(
            Conversation(),
            null,
            new LocalAiThinkingOptions(Enabled: true),
            CancellationToken.None);

        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.False(request.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(request.RootElement.TryGetProperty("extra_context", out _));
        Assert.False(request.RootElement.TryGetProperty("response_format", out _));
        Assert.Equal(
            client.InferenceCapabilities.MaximumOutputTokens,
            request.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(ModelProfileCatalog.OnnxBackend,
            ModelProfileCatalog.GetProfile(ModelProfileCatalog.Phi4MiniPreset).Backend);
    }

    [Fact]
    public async Task OpenAi_request_uses_the_exact_caller_supplied_json_schema()
    {
        const string schema = """{"type":"object","required":["value"],"properties":{"value":{"type":"string"}},"additionalProperties":false}""";
        var handler = new RecordingHandler((_, _) => Task.FromResult(Response(FinalAnswer)));
        using var client = CreateClient(ModelProfileCatalog.Gemma4E2BLiteRtPreset, handler);

        await client.CompleteConversationAsync(Conversation(), schema, null, CancellationToken.None);

        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var responseFormat = request.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", responseFormat.GetProperty("type").GetString());
        Assert.Equal(schema, responseFormat.GetProperty("json_schema").GetProperty("schema").GetString());
    }

    [Fact]
    public async Task Gemma_request_preserves_caller_cancellation()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Response(FinalAnswer);
        });
        using var client = CreateClient(ModelProfileCatalog.Gemma4E2BLiteRtPreset, handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CompleteConversationAsync(Conversation(), cancellation.Token));
    }

    private static OnnxGenAiClient CreateClient(
        string preset,
        HttpMessageHandler handler,
        ILogger<OnnxGenAiClient>? logger = null)
    {
        var options = new ModelOptions
        {
            Preset = preset,
            Backend = ModelProfileCatalog.OpenAiCompatibleBackend,
            EndpointBaseUrl = "http://localhost:9379/v1",
            ChatModel = "test-model",
            InferenceTimeoutSeconds = 30
        };
        return new OnnxGenAiClient(
            new InstalledModelManager(),
            new ReadyRuntimeSupervisor(),
            new LocalAiActivityTracker(),
            options,
            logger ?? new RecordingLogger<OnnxGenAiClient>(),
            handler);
    }

    private static IReadOnlyList<LocalAiMessage> Conversation() =>
    [
        new(LocalAiMessageRole.System, "Return JSON."),
        new(LocalAiMessageRole.User, "Classify the labelled email context.")
    ];

    private static HttpResponseMessage Response(string content)
    {
        var json = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { content } }
            }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return await responder(request, cancellationToken);
        }
    }

    private sealed class InstalledModelManager : IModelManager
    {
        public Task<ModelStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ModelStatus(true, "", "test", "Model is installed.", []));

        public Task<ModelInstallResult> EnsureModelAsync(
            CancellationToken cancellationToken,
            IProgress<string>? progress = null) => throw new NotSupportedException();
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception?.Message ?? ""));
    }

    private sealed record LogEntry(LogLevel Level, string Message, string ExceptionMessage);
}
