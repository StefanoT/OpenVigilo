using Microsoft.Extensions.Logging;
using Vigilo.Core;

namespace Vigilo.Classification;

public sealed class SerializedLanguageModelInvoker(
    ILocalAiClient client,
    ModelOptions options,
    HarnessOptions harnessOptions,
    ILogger<SerializedLanguageModelInvoker>? logger = null) : ILanguageModelInvoker
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool EnforceResponseJsonSchemas =>
        options.EnforceResponseJsonSchemas ?? client.InferenceCapabilities.EnforcesResponseJsonSchemas;

    public string ModelId => string.Join(
        ':',
        client.GetType().Name,
        options.Preset,
        options.Backend,
        options.Version,
        options.ChatModel,
        client.InferenceCapabilities.ContextWindowTokens,
        client.InferenceCapabilities.MaximumOutputTokens,
        EnforceResponseJsonSchemas ? "json-schema" : "json-mode",
        "think",
        harnessOptions.Thinking is { Enabled: true } thinking
            ? $"{thinking.ReasoningEffort}-{thinking.MaximumThinkingTokens}"
            : "off");

    public async Task<string> InvokeAsync(
        string systemPrompt,
        string userPrompt,
        string? responseJsonSchema,
        LocalAiThinkingOptions? thinking,
        CancellationToken cancellationToken)
    {
        var capabilities = client.InferenceCapabilities;
        var estimatedInputTokens = capabilities.EstimateMessageTokens(systemPrompt)
            + capabilities.EstimateMessageTokens(userPrompt);
        if (estimatedInputTokens > capabilities.EffectiveInputTokenLimit)
        {
            logger?.LogWarning(
                "Local AI prompt exceeds the selected model's practical input budget. EstimatedInputTokens={EstimatedInputTokens} EffectiveInputTokenLimit={EffectiveInputTokenLimit} PreferredInputTokens={PreferredInputTokens} Preset={Preset} Backend={Backend}",
                estimatedInputTokens,
                capabilities.EffectiveInputTokenLimit,
                capabilities.PreferredInputTokens,
                options.Preset,
                options.Backend);
        }

        // Capability negotiation: the classifier always supplies its exact JSON Schema, but
        // the runtime only receives it when the selected profile (or an explicit user
        // override) advertises enforcement. Runtimes that ignore response_format would
        // otherwise pay grammar overhead for no benefit while gaining nothing over
        // host-side validation.
        var negotiatedSchema = EnforceResponseJsonSchemas ? responseJsonSchema : null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await client.CompleteConversationAsync(
                [
                    new LocalAiMessage(LocalAiMessageRole.System, systemPrompt),
                    new LocalAiMessage(LocalAiMessageRole.User, userPrompt)
                ],
                negotiatedSchema,
                thinking,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
