using Vigilo.Core;

namespace Vigilo.LocalAi;

public static class ModelProfileCatalog
{
    public const string OnnxBackend = "OnnxGenAi";
    public const string OpenAiCompatibleBackend = "OpenAiCompatibleHttp";
    public const string Phi4MiniPreset = "Phi4Mini";
    public const string Phi4FullPreset = "Phi4Full";
    public const string Gemma4E2BLiteRtPreset = "Gemma4E2BLiteRt";
    public const string Gemma4E4BLiteRtPreset = "Gemma4E4BLiteRt";

    private static readonly string[] OnnxRequiredFiles =
    [
        "genai_config.json",
        "model.onnx",
        "model.onnx.data",
        "tokenizer.json",
        "tokenizer_config.json",
        "vocab.json",
        "merges.txt",
        "special_tokens_map.json"
    ];

    public static IReadOnlyList<AiModelProfile> Profiles { get; } =
    [
        new(
            Phi4MiniPreset,
            "Phi-4 Mini ONNX int4",
            OnnxBackend,
            "Default local ONNX Runtime GenAI model.",
            new LocalAiInferenceCapabilities(
                contextWindowTokens: 131_072,
                preferredInputTokens: 7_168,
                maximumOutputTokens: 384,
                reservedContextTokens: 256,
                maximumProgressiveRequests: 6,
                // The ONNX Runtime GenAI chat API exposes only a generic JSON mode, not
                // per-request JSON Schemas, so enforcement stays off for this backend.
                enforcesResponseJsonSchemas: false)),
        new(
            Phi4FullPreset,
            "Phi-4 ONNX int4",
            OnnxBackend,
            "Larger Phi-4 ONNX Runtime GenAI model.",
            new LocalAiInferenceCapabilities(
                contextWindowTokens: 16_384,
                preferredInputTokens: 6_144,
                maximumOutputTokens: 384,
                reservedContextTokens: 256,
                maximumProgressiveRequests: 5,
                enforcesResponseJsonSchemas: false)),
        new(
            Gemma4E2BLiteRtPreset,
            "Google Gemma 4 E2B LiteRT-LM",
            OpenAiCompatibleBackend,
            "Runs through LiteRT-LM's local OpenAI-compatible server; designed for lower-memory devices.",
            new LocalAiInferenceCapabilities(
                contextWindowTokens: 131_072,
                preferredInputTokens: 3_584,
                maximumOutputTokens: 1_024,
                reservedContextTokens: 256,
                maximumProgressiveRequests: 4,
                // LiteRT-LM 0.14.0 accepts but silently ignores OpenAI response_format
                // json_schema requests; flip via ModelOptions when the runtime enforces them.
                enforcesResponseJsonSchemas: false)),
        new(
            Gemma4E4BLiteRtPreset,
            "Google Gemma 4 E4B LiteRT-LM",
            OpenAiCompatibleBackend,
            "Higher-accuracy LiteRT-LM profile that requires more memory and compute than E2B.",
            new LocalAiInferenceCapabilities(
                contextWindowTokens: 131_072,
                preferredInputTokens: 4_608,
                maximumOutputTokens: 1_024,
                reservedContextTokens: 256,
                maximumProgressiveRequests: 5,
                enforcesResponseJsonSchemas: false))
    ];

    public static AiModelProfile GetProfile(string? preset) =>
        Profiles.FirstOrDefault(profile => string.Equals(profile.Preset, preset, StringComparison.OrdinalIgnoreCase))
        ?? Profiles[0];

    public static void ApplyPreset(ModelOptions options)
    {
        var profile = GetProfile(options.Preset);
        options.Preset = profile.Preset;
        options.DisplayName = profile.DisplayName;
        options.Backend = profile.Backend;
        options.InferenceCapabilities = profile.InferenceCapabilities;

        switch (profile.Preset)
        {
            case Phi4FullPreset:
                options.Version = "phi-4-onnx/cpu-int4-rtn-block-32-acc-level-4";
                options.ManifestBaseUrl = "https://huggingface.co/microsoft/phi-4-onnx/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/";
                options.EndpointBaseUrl = "";
                options.ChatModel = "";
                options.SetupInstructions = "";
                options.RequiredFiles = OnnxRequiredFiles;
                break;

            case Gemma4E2BLiteRtPreset:
                options.Version = "litert-community/gemma-4-E2B-it-litert-lm";
                options.ManifestBaseUrl = "";
                // Preserve an explicitly configured endpoint, including a remote one. This is an
                // intentional user-controlled opt-out from Vigilo's local-processing privacy value;
                // the application defaults to localhost but does not enforce loopback-only inference.
                options.EndpointBaseUrl = string.IsNullOrWhiteSpace(options.EndpointBaseUrl)
                    ? "http://localhost:9379/v1"
                    : options.EndpointBaseUrl.TrimEnd('/');
                options.ChatModel = string.IsNullOrWhiteSpace(options.ChatModel)
                    ? "gemma4-e2b"
                    : options.ChatModel;
                options.SetupInstructions =
                    "Install LiteRT-LM, import litert-community/gemma-4-E2B-it-litert-lm as gemma4-e2b, then run litert-lm serve.";
                options.RequiredFiles = [];
                options.ModelRoot = "";
                break;

            case Gemma4E4BLiteRtPreset:
                options.Version = "litert-community/gemma-4-E4B-it-litert-lm";
                options.ManifestBaseUrl = "";
                // Apply the same intentional local-by-default, user-configurable endpoint policy as E2B.
                options.EndpointBaseUrl = string.IsNullOrWhiteSpace(options.EndpointBaseUrl)
                    ? "http://localhost:9379/v1"
                    : options.EndpointBaseUrl.TrimEnd('/');
                options.ChatModel = string.IsNullOrWhiteSpace(options.ChatModel)
                    ? "gemma4-e4b"
                    : options.ChatModel;
                options.SetupInstructions =
                    "Install LiteRT-LM, import litert-community/gemma-4-E4B-it-litert-lm as gemma4-e4b, then run litert-lm serve.";
                options.RequiredFiles = [];
                options.ModelRoot = "";
                break;

            default:
                options.Version = "Phi-4-mini-instruct-onnx/cpu-int4-rtn-block-32-acc-level-4";
                options.ManifestBaseUrl = "https://huggingface.co/microsoft/Phi-4-mini-instruct-onnx/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/";
                options.EndpointBaseUrl = "";
                options.ChatModel = "";
                options.SetupInstructions = "";
                options.RequiredFiles = OnnxRequiredFiles;
                break;
        }

        EnsureModelRoot(options);
    }

    public static void EnsureModelRoot(ModelOptions options)
    {
        if (!string.Equals(options.Backend, OnnxBackend, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.ModelRoot))
        {
            return;
        }

        var baseDirectory = string.IsNullOrWhiteSpace(options.ModelBaseDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vigilo", "Models")
            : options.ModelBaseDirectory;
        options.ModelRoot = Path.Combine(baseDirectory, options.Version);
    }
}
