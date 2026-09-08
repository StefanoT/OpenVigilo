using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Vigilo.Classification;

public static class PromptIds
{
    public const string SingleVerdict = "single-verdict";
}

public sealed class EmbeddedPromptRegistry : IPromptRegistry
{
    private static readonly string[] PromptAssets = [PromptIds.SingleVerdict];

    private readonly IReadOnlyDictionary<string, PromptDefinition> _definitions;
    private readonly string _compositeVersion;

    public EmbeddedPromptRegistry()
    {
        var common = Load("Common.md");
        var definitions = new Dictionary<string, PromptDefinition>(StringComparer.Ordinal);
        var compositeBuilder = new StringBuilder();
        foreach (var promptId in PromptAssets)
        {
            var assetName = string.Concat(promptId
                .Split('-')
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..])) + ".md";
            var asset = Load(assetName);
            var template = common + Environment.NewLine + Environment.NewLine + asset;
            definitions[promptId] = new PromptDefinition(promptId, DeriveVersion(asset, template), $"Prompt for {promptId}", template);
            compositeBuilder.Append(promptId).Append(':').AppendLine(template);
        }

        _definitions = definitions;
        _compositeVersion = "vigilo-verdict-prompts-" + Hash8(compositeBuilder.ToString());
    }

    public IReadOnlyDictionary<string, string> CurrentVersions =>
        _definitions.ToDictionary(pair => pair.Key, pair => pair.Value.Version, StringComparer.Ordinal);

    public string CompositeVersion => _compositeVersion;

    public PromptDefinition Resolve(string promptId, string version)
    {
        if (!_definitions.TryGetValue(promptId, out var definition) || definition.Version != version)
        {
            throw new KeyNotFoundException($"Prompt '{promptId}' version '{version}' is not registered.");
        }

        return definition;
    }

    public PromptDefinition ResolveCurrent(string promptId) =>
        _definitions.TryGetValue(promptId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Prompt '{promptId}' is not registered.");

    // A prompt's version is its declared header version plus a short hash of the assembled
    // template (common preamble plus prompt asset). Editing a prompt file changes the hash,
    // so response-cache keys and stored prompt versions track prompt content automatically
    // instead of relying on a hand-maintained registry-wide constant.
    private static string DeriveVersion(string asset, string template)
    {
        var headerVersion = asset.Split('\n', 2)[0].Trim().Split(' ').LastOrDefault() ?? "";
        return $"{(headerVersion.Contains('.', StringComparison.Ordinal) ? headerVersion : "0.0.0")}+{Hash8(template)}";
    }

    private static string Hash8(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();

    private static string Load(string fileName)
    {
        var assembly = typeof(EmbeddedPromptRegistry).Assembly;
        var suffix = $".Prompts.{fileName}";
        var name = assembly.GetManifestResourceNames().SingleOrDefault(candidate => candidate.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded prompt asset '{fileName}' was not found.");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded prompt asset '{fileName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
