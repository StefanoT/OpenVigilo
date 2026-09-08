using System.Text;

namespace Vigilo.Core;

public sealed record LocalAiInferenceCapabilities
{
    public static LocalAiInferenceCapabilities Conservative { get; } = new(
        contextWindowTokens: 4_096,
        preferredInputTokens: 3_584,
        maximumOutputTokens: 384,
        reservedContextTokens: 128,
        maximumProgressiveRequests: 4);

    public LocalAiInferenceCapabilities(
        int contextWindowTokens,
        int preferredInputTokens,
        int maximumOutputTokens,
        int reservedContextTokens,
        int maximumProgressiveRequests,
        int estimatedUtf8BytesPerToken = 3,
        int estimatedMessageOverheadTokens = 8,
        bool enforcesResponseJsonSchemas = false)
    {
        if (contextWindowTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextWindowTokens));
        }

        if (preferredInputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredInputTokens));
        }

        if (maximumOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputTokens));
        }

        if (reservedContextTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reservedContextTokens));
        }

        if (maximumProgressiveRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumProgressiveRequests));
        }

        if (estimatedUtf8BytesPerToken <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedUtf8BytesPerToken));
        }

        if (estimatedMessageOverheadTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedMessageOverheadTokens));
        }

        var maximumContextInputTokens = contextWindowTokens - maximumOutputTokens - reservedContextTokens;
        if (maximumContextInputTokens <= 0)
        {
            throw new ArgumentException(
                "The context window must leave room for output and reserved chat-template tokens.",
                nameof(contextWindowTokens));
        }

        ContextWindowTokens = contextWindowTokens;
        PreferredInputTokens = Math.Min(preferredInputTokens, maximumContextInputTokens);
        MaximumOutputTokens = maximumOutputTokens;
        ReservedContextTokens = reservedContextTokens;
        MaximumProgressiveRequests = maximumProgressiveRequests;
        EstimatedUtf8BytesPerToken = estimatedUtf8BytesPerToken;
        EstimatedMessageOverheadTokens = estimatedMessageOverheadTokens;
        EnforcesResponseJsonSchemas = enforcesResponseJsonSchemas;
    }

    public int ContextWindowTokens { get; }

    public int PreferredInputTokens { get; }

    public int MaximumOutputTokens { get; }

    public int ReservedContextTokens { get; }

    public int MaximumProgressiveRequests { get; }

    public int EstimatedUtf8BytesPerToken { get; }

    public int EstimatedMessageOverheadTokens { get; }

    public bool EnforcesResponseJsonSchemas { get; }

    public int MaximumContextInputTokens =>
        ContextWindowTokens - MaximumOutputTokens - ReservedContextTokens;

    public int EffectiveInputTokenLimit =>
        Math.Min(PreferredInputTokens, MaximumContextInputTokens);

    public int EstimateInputTokens(IEnumerable<LocalAiMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Sum(message => EstimateMessageTokens(message.Content));
    }

    public int EstimateMessageTokens(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return (Encoding.UTF8.GetByteCount(content) + EstimatedUtf8BytesPerToken - 1)
            / EstimatedUtf8BytesPerToken
            + EstimatedMessageOverheadTokens;
    }
}
