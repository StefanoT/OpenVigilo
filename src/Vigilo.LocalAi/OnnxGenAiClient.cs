using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;
using Vigilo.Core;

namespace Vigilo.LocalAi;

public sealed class OnnxGenAiClient(
    IModelManager modelManager,
    ILocalAiRuntimeSupervisor runtimeSupervisor,
    LocalAiActivityTracker activityTracker,
    ModelOptions options,
    ILogger<OnnxGenAiClient> logger,
    HttpMessageHandler? httpMessageHandler = null) : ILocalAiClient, IDisposable
{
#if DEBUG
    private static readonly object DebugInferenceCaptureGate = new();
#endif
    private const string JsonSystemMessage =
        "You are a strict JSON API. Return exactly one valid JSON object. Do not use markdown. Quote every string value. Use an empty string for empty string fields.";
    private const string GemmaThoughtChannelStart = "<|channel>thought";
    private const string GemmaChannelEnd = "<channel|>";
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private OnnxRuntimeGenAIChatClient? _client;
    private string _loadedModelKey = "";

    public LocalAiInferenceCapabilities InferenceCapabilities => options.InferenceCapabilities;

    public static bool IsLoopbackEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;

    public Task<string> CompleteAsync(
        string prompt,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null) =>
        CompleteConversationAsync(
            [
                new LocalAiMessage(LocalAiMessageRole.System, JsonSystemMessage),
                new LocalAiMessage(LocalAiMessageRole.User, prompt)
            ],
            null,
            null,
            cancellationToken,
            progress);

    public Task<string> CompleteConversationAsync(
        IReadOnlyList<LocalAiMessage> messages,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null) =>
        CompleteConversationAsync(messages, null, null, cancellationToken, progress);

    public async Task<string> CompleteConversationAsync(
        IReadOnlyList<LocalAiMessage> messages,
        string? responseJsonSchema,
        LocalAiThinkingOptions? thinking,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            throw new ArgumentException("At least one conversation message is required.", nameof(messages));
        }

        var capabilities = InferenceCapabilities;
        var promptLength = messages.Sum(message => message.Content.Length);
        var estimatedInputTokens = capabilities.EstimateInputTokens(messages);
        if (estimatedInputTokens > capabilities.MaximumContextInputTokens)
        {
            throw new InvalidOperationException(
                $"The local AI request is estimated at {estimatedInputTokens} input tokens, exceeding the "
                + $"selected model's safe context input limit of {capabilities.MaximumContextInputTokens} tokens.");
        }

        var stopwatch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.InferenceTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        logger.LogInformation(
            "Local AI inference started. PromptLength={PromptLength} EstimatedInputTokens={EstimatedInputTokens} PreferredInputTokens={PreferredInputTokens} ContextWindowTokens={ContextWindowTokens} MaximumOutputTokens={MaximumOutputTokens} MessageCount={MessageCount} TimeoutSeconds={TimeoutSeconds} Preset={Preset} ModelRoot={ModelRoot}",
            promptLength,
            estimatedInputTokens,
            capabilities.EffectiveInputTokenLimit,
            capabilities.ContextWindowTokens,
            capabilities.MaximumOutputTokens,
            messages.Count,
            options.InferenceTimeoutSeconds,
            options.Preset,
            options.ModelRoot);
        try
        {
            if (!await runtimeSupervisor.EnsureReadyAsync(linked.Token))
            {
                throw new InvalidOperationException(runtimeSupervisor.CurrentStatus.Message);
            }

            if (string.Equals(options.Backend, ModelProfileCatalog.OpenAiCompatibleBackend, StringComparison.OrdinalIgnoreCase))
            {
                return await CompleteOpenAiCompatibleAsync(
                    messages,
                    capabilities,
                    responseJsonSchema,
                    thinking,
                    linked.Token,
                    progress,
                    stopwatch);
            }

            var client = await GetClientAsync(linked.Token, progress);
            progress?.Report("Running classification model");
            var response = await client.GetResponseAsync(
                messages.Select(ToChatMessage).ToArray(),
                new ChatOptions
                {
                    MaxOutputTokens = capabilities.MaximumOutputTokens,
                    Temperature = 0,
                    ResponseFormat = ChatResponseFormat.Json
                },
                linked.Token);

            logger.LogInformation(
                "Local AI inference completed. PromptLength={PromptLength} ResponseLength={ResponseLength} ElapsedMilliseconds={ElapsedMilliseconds}",
                promptLength,
                response.Text.Length,
                stopwatch.ElapsedMilliseconds);
            CaptureDebugInference(messages, responseJsonSchema, response.Text, stopwatch.Elapsed, null);
            progress?.Report("Classification model responded");
            return response.Text;
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            CaptureDebugInference(messages, responseJsonSchema, null, stopwatch.Elapsed, ex);
            logger.LogError(
                ex,
                "Local AI inference timed out. PromptLength={PromptLength} TimeoutSeconds={TimeoutSeconds} ElapsedMilliseconds={ElapsedMilliseconds}",
                promptLength,
                options.InferenceTimeoutSeconds,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            CaptureDebugInference(messages, responseJsonSchema, null, stopwatch.Elapsed, ex);
            logger.LogWarning(
                ex,
                "Local AI inference was canceled. PromptLength={PromptLength} ElapsedMilliseconds={ElapsedMilliseconds}",
                promptLength,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            CaptureDebugInference(messages, responseJsonSchema, null, stopwatch.Elapsed, ex);
            logger.LogError(
                ex,
                "Local AI inference failed. PromptLength={PromptLength} ElapsedMilliseconds={ElapsedMilliseconds}",
                promptLength,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private async Task<OnnxRuntimeGenAIChatClient> GetClientAsync(
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        var modelKey = GetOnnxModelKey();
        if (_client is not null && string.Equals(_loadedModelKey, modelKey, StringComparison.Ordinal))
        {
            progress?.Report("Model ready");
            return _client;
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null && string.Equals(_loadedModelKey, modelKey, StringComparison.Ordinal))
            {
                progress?.Report("Model ready");
                return _client;
            }

            _client?.Dispose();
            _client = null;
            _loadedModelKey = "";

            progress?.Report("Checking classification model");
            var status = await modelManager.GetStatusAsync(cancellationToken);
            if (!status.IsInstalled)
            {
                progress?.Report("Classification model files missing");
                logger.LogWarning(
                    "Cannot load local AI model because required files are missing. ModelPath={ModelPath} Version={Version} MissingFiles={MissingFiles}",
                    status.ModelPath,
                    status.Version,
                    string.Join(';', status.MissingFiles));
                throw new InvalidOperationException(status.Message);
            }

            var stopwatch = Stopwatch.StartNew();
            logger.LogInformation(
                "Loading ONNX Runtime GenAI model. ModelPath={ModelPath} Version={Version}",
                status.ModelPath,
                status.Version);
            progress?.Report("Loading classification model");
            _client = new OnnxRuntimeGenAIChatClient(status.ModelPath, new OnnxRuntimeGenAIChatClientOptions
            {
                EnableCaching = false
            });
            _loadedModelKey = modelKey;
            logger.LogInformation(
                "Loaded ONNX Runtime GenAI model. ModelPath={ModelPath} ElapsedMilliseconds={ElapsedMilliseconds}",
                status.ModelPath,
                stopwatch.ElapsedMilliseconds);
            progress?.Report("Model ready");

            return _client;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<string> CompleteOpenAiCompatibleAsync(
        IReadOnlyList<LocalAiMessage> messages,
        LocalAiInferenceCapabilities capabilities,
        string? responseJsonSchema,
        LocalAiThinkingOptions? thinking,
        CancellationToken cancellationToken,
        IProgress<string>? progress,
        Stopwatch stopwatch)
    {
        if (!IsLoopbackEndpoint(options.EndpointBaseUrl)
            || !Uri.TryCreate(options.EndpointBaseUrl, UriKind.Absolute, out var endpointBase))
        {
            throw new InvalidOperationException(
                "Vigilo only sends email analysis prompts to loopback OpenAI-compatible endpoints.");
        }

        progress?.Report("Checking classification model");
        var status = await modelManager.GetStatusAsync(cancellationToken);
        // Right after startup the local server can still be waking while the status probe's
        // short timeout already reports "not reachable". Re-probe briefly before failing so
        // a startup race does not burn a classification attempt.
        for (var reprobe = 0; !status.IsInstalled && reprobe < 5; reprobe++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            status = await modelManager.GetStatusAsync(cancellationToken);
        }

        if (!status.IsInstalled)
        {
            progress?.Report("Classification model unavailable");
            throw new InvalidOperationException(status.Message);
        }

        using var inferenceActivity = activityTracker.BeginInference();
        progress?.Report("Running classification model");
        var endpoint = $"{endpointBase.ToString().TrimEnd('/')}/chat/completions";
        using var httpClient = new HttpClient(
            httpMessageHandler ?? new HttpClientHandler(),
            disposeHandler: httpMessageHandler is null)
        {
            Timeout = TimeSpan.FromSeconds(options.InferenceTimeoutSeconds)
        };
        // max_tokens covers both channels: a bounded thought plus the full final-answer
        // budget, otherwise thinking consumes the tokens the JSON response needs.
        var maximumGenerationTokens = capabilities.MaximumOutputTokens;
        string? reasoningEffort = null;
        bool? enableThinking = null;
        if (IsGemma4Preset(options.Preset))
        {
            if (thinking?.Enabled == true)
            {
                maximumGenerationTokens += Math.Max(0, thinking.MaximumThinkingTokens);
                reasoningEffort = string.IsNullOrWhiteSpace(thinking.ReasoningEffort) ? "low" : thinking.ReasoningEffort;
                enableThinking = true;
            }
            else
            {
                reasoningEffort = "none";
                enableThinking = false;
            }
        }

        var request = new Dictionary<string, object?>
        {
            ["model"] = options.ChatModel,
            ["messages"] = messages.Select(message => new
            {
                role = message.Role.ToString().ToLowerInvariant(),
                content = message.Content
            }),
            ["max_tokens"] = maximumGenerationTokens,
            ["temperature"] = 0
        };
        if (reasoningEffort is not null)
        {
            request["reasoning_effort"] = reasoningEffort;
            request["extra_context"] = new Dictionary<string, bool>
            {
                ["enable_thinking"] = enableThinking!.Value
            };
        }
        if (!string.IsNullOrWhiteSpace(responseJsonSchema))
        {
            request["response_format"] = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "vigilo_semantic_node",
                    strict = true,
                    schema = responseJsonSchema
                }
            };
        }

        var requestJson = JsonSerializer.Serialize(request);
        using var requestContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(endpoint, requestContent, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Local OpenAI-compatible inference failed. StatusCode={StatusCode} ReasonPhrase={ReasonPhrase} Endpoint={Endpoint} ChatModel={ChatModel} ResponseBody={ResponseBody}",
                (int)response.StatusCode,
                response.ReasonPhrase,
                endpoint,
                options.ChatModel,
                errorBody);
            response.EnsureSuccessStatusCode();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var choice = document.RootElement.GetProperty("choices")[0];
        var text = choice
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?? "";
        var finishReason = choice.TryGetProperty("finish_reason", out var finishReasonElement)
            ? finishReasonElement.GetString()
            : null;
        int? completionTokens = null;
        if (document.RootElement.TryGetProperty("usage", out var usage)
            && usage.TryGetProperty("completion_tokens", out var completionTokensElement)
            && completionTokensElement.TryGetInt32(out var parsedCompletionTokens))
        {
            completionTokens = parsedCompletionTokens;
        }
        if (IsGemma4Preset(options.Preset))
        {
            text = NormalizeGemmaResponse(text);
        }

        CaptureDebugInference(messages, responseJsonSchema, text, stopwatch.Elapsed, null);

        logger.LogInformation(
            "Local OpenAI-compatible inference completed. PromptLength={PromptLength} ResponseLength={ResponseLength} FinishReason={FinishReason} CompletionTokens={CompletionTokens} Endpoint={Endpoint} ChatModel={ChatModel} ElapsedMilliseconds={ElapsedMilliseconds}",
            messages.Sum(message => message.Content.Length),
            text.Length,
            finishReason,
            completionTokens,
            endpoint,
            options.ChatModel,
            stopwatch.ElapsedMilliseconds);
        progress?.Report("Classification model responded");
        return text;
    }

    private string NormalizeGemmaResponse(string text)
    {
        var thoughtStart = text.IndexOf(GemmaThoughtChannelStart, StringComparison.Ordinal);
        var channelEnd = text.IndexOf(GemmaChannelEnd, StringComparison.Ordinal);
        if (thoughtStart < 0 && channelEnd < 0)
        {
            return text;
        }

        var thoughtContentStart = thoughtStart + GemmaThoughtChannelStart.Length;
        var hasAmbiguousDelimiters = thoughtStart < 0
            || channelEnd < thoughtContentStart
            || !string.IsNullOrWhiteSpace(text[..thoughtStart])
            || text.IndexOf(GemmaThoughtChannelStart, thoughtContentStart, StringComparison.Ordinal) >= 0
            || text.IndexOf(GemmaChannelEnd, channelEnd + GemmaChannelEnd.Length, StringComparison.Ordinal) >= 0;
        if (hasAmbiguousDelimiters)
        {
            throw new InvalidDataException("Gemma returned malformed thought-channel delimiters.");
        }

        if (!string.IsNullOrWhiteSpace(text[thoughtContentStart..channelEnd]))
        {
            logger.LogDebug(
                "Gemma thought channel was observed and excluded from classification. Preset={Preset}",
                options.Preset);
        }

        return text[(channelEnd + GemmaChannelEnd.Length)..].TrimStart();
    }

    private static bool IsGemma4Preset(string preset) =>
        string.Equals(preset, ModelProfileCatalog.Gemma4E2BLiteRtPreset, StringComparison.OrdinalIgnoreCase)
        || string.Equals(preset, ModelProfileCatalog.Gemma4E4BLiteRtPreset, StringComparison.OrdinalIgnoreCase);

    private void CaptureDebugInference(
        IReadOnlyList<LocalAiMessage> messages,
        string? responseJsonSchema,
        string? response,
        TimeSpan elapsed,
        Exception? exception)
    {
#if DEBUG
        if (AppContext.GetData("Vigilo.DebugInferenceCapturePath") is not string path
            || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var record = new
        {
            capturedAtUtc = DateTimeOffset.UtcNow,
            preset = options.Preset,
            backend = options.Backend,
            model = options.ChatModel,
            inferenceTimeoutSeconds = options.InferenceTimeoutSeconds,
            elapsedMilliseconds = elapsed.TotalMilliseconds,
            messages = messages.Select(message => new { role = message.Role.ToString(), content = message.Content }),
            responseJsonSchema,
            response,
            exception = exception?.ToString()
        };
        var line = JsonSerializer.Serialize(record) + Environment.NewLine;
        lock (DebugInferenceCaptureGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line, Encoding.UTF8);
        }
#endif
    }

    private static ChatMessage ToChatMessage(LocalAiMessage message) => new(
        message.Role switch
        {
            LocalAiMessageRole.System => ChatRole.System,
            LocalAiMessageRole.User => ChatRole.User,
            LocalAiMessageRole.Assistant => ChatRole.Assistant,
            _ => throw new ArgumentOutOfRangeException(nameof(message), message.Role, "Unsupported local AI message role.")
        },
        message.Content);

    private string GetOnnxModelKey() => $"{options.Backend}|{options.Version}|{options.ModelRoot}";

    public void Dispose()
    {
        _client?.Dispose();
        _semaphore.Dispose();
    }
}
