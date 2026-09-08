using Microsoft.Extensions.DependencyInjection;
using Vigilo.Core;

namespace Vigilo.LocalAi;

public static class LocalAiServiceCollectionExtensions
{
    public static IServiceCollection AddVigiloLocalAi(
        this IServiceCollection services,
        ModelOptions options,
        string? modelSettingsPath = null)
    {
        ModelProfileCatalog.ApplyPreset(options);

        services.AddSingleton(options);
        services.AddSingleton(new ModelSettingsStore(modelSettingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vigilo",
            "model-settings.json")));
        services.AddSingleton<LocalAiActivityTracker>();
        services.AddSingleton<IModelSelectionService, ModelSelectionService>();
        services.AddSingleton<IModelManager, Phi4MiniModelManager>();
        services.AddSingleton<LocalAiRuntimeSupervisor>();
        services.AddSingleton<ILocalAiRuntimeSupervisor>(provider => provider.GetRequiredService<LocalAiRuntimeSupervisor>());
        services.AddHostedService(provider => provider.GetRequiredService<LocalAiRuntimeSupervisor>());
        services.AddSingleton<ILocalAiClient, OnnxGenAiClient>();
        return services;
    }
}
