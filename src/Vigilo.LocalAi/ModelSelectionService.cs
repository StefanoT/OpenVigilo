using Microsoft.Extensions.Logging;
using Vigilo.Configuration;
using Vigilo.Core;

namespace Vigilo.LocalAi;

public sealed class ModelSelectionService : IModelSelectionService, IDisposable
{
    private const int ModelSettingsSchemaVersion = 1;
    private readonly ModelOptions options;
    private readonly ILogger<ModelSelectionService> logger;
    private readonly IAtomicConfigurationRepository configurationRepository;
    private readonly ConfigurationFileDefinition<PersistedModelSettings> modelSettingsDefinition;
    private readonly SemaphoreSlim selectionGate = new(1, 1);

    public ModelSelectionService(
        ModelOptions options,
        ModelSettingsStore settingsStore,
        ILogger<ModelSelectionService> logger,
        IAtomicConfigurationRepository configurationRepository)
    {
        this.options = options;
        this.logger = logger;
        this.configurationRepository = configurationRepository;
        modelSettingsDefinition = new(
            settingsStore.Path,
            ModelSettingsSchemaVersion,
            () => CreateDocument(options.Preset),
            static settings => settings.SchemaVersion,
            static (settings, targetVersion) => Migrate(settings, targetVersion),
            ValidateDocument);
    }

    public IReadOnlyList<AiModelProfile> GetProfiles() => ModelProfileCatalog.Profiles;

    public AiModelProfile GetCurrentProfile() => ModelProfileCatalog.GetProfile(options.Preset);

    public async Task SelectProfileAsync(string preset, CancellationToken cancellationToken)
    {
        var profile = ModelProfileCatalog.Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Preset, preset, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown local AI model preset '{preset}'.");

        await selectionGate.WaitAsync(cancellationToken);
        try
        {
            var previousPreset = options.Preset;
            await configurationRepository.WriteAsync(
                modelSettingsDefinition,
                CreateDocument(profile.Preset),
                cancellationToken);

            options.Preset = profile.Preset;
            options.ModelRoot = "";
            options.EndpointBaseUrl = "";
            options.ChatModel = "";
            ModelProfileCatalog.ApplyPreset(options);

            logger.LogInformation(
                "AI model profile selected. PreviousPreset={PreviousPreset} Preset={Preset} Backend={Backend} Version={Version}",
                previousPreset,
                options.Preset,
                options.Backend,
                options.Version);
        }
        finally
        {
            selectionGate.Release();
        }
    }

    private static PersistedModelSettings CreateDocument(string preset) =>
        new(ModelSettingsSchemaVersion, new PersistedModelOptions(preset));

    private static PersistedModelSettings Migrate(PersistedModelSettings settings, int targetVersion)
    {
        if (settings.SchemaVersion is not 0)
        {
            throw new InvalidDataException(
                $"Model settings schema version {settings.SchemaVersion} cannot be migrated.");
        }

        return settings with { SchemaVersion = targetVersion };
    }

    private static void ValidateDocument(PersistedModelSettings settings)
    {
        if (settings.Model is null
            || !ModelProfileCatalog.Profiles.Any(profile =>
                string.Equals(profile.Preset, settings.Model.Preset, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Model settings contain an unknown model preset.");
        }
    }

    public void Dispose() => selectionGate.Dispose();

    private sealed record PersistedModelSettings(int SchemaVersion, PersistedModelOptions? Model);

    private sealed record PersistedModelOptions(string Preset);
}

public sealed record ModelSettingsStore(string Path);
