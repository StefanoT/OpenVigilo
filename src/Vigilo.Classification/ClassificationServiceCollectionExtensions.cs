using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vigilo.Core;

namespace Vigilo.Classification;

public static class ClassificationServiceCollectionExtensions
{
    /// <param name="responseCachePath">
    /// Optional file path for the persistent model-response cache, expected to live beside
    /// the local database so local-data maintenance can delete derived responses together
    /// with their source messages. When null, an in-memory cache is used.
    /// </param>
    public static IServiceCollection AddVigiloClassification(
        this IServiceCollection services,
        string? responseCachePath = null)
    {
        services.AddSingleton<HarnessOptions>(provider => new HarnessOptions
        {
            PerCallTimeoutResolver = () => TimeSpan.FromSeconds(
                provider.GetRequiredService<ModelOptions>().InferenceTimeoutSeconds),
            MaximumContextCharactersResolver = () => HarnessOptions.ModelAwareContextCharacters(
                provider.GetRequiredService<ModelOptions>().InferenceCapabilities)
        });
        services.AddSingleton<IPromptRegistry, EmbeddedPromptRegistry>();
        services.AddSingleton<IEvidenceLocator, EvidenceLocator>();
        services.AddSingleton<IModelResponseCache>(provider =>
        {
            if (string.IsNullOrWhiteSpace(responseCachePath))
            {
                return new InMemoryModelResponseCache(provider.GetRequiredService<HarnessOptions>());
            }

            return new PersistentModelResponseCache(
                responseCachePath,
                provider.GetRequiredService<HarnessOptions>(),
                provider.GetService<ILogger<PersistentModelResponseCache>>());
        });
        services.AddSingleton<ILanguageModelInvoker, SerializedLanguageModelInvoker>();
        services.AddSingleton<IHarnessPolicy, HarnessPolicy>();
        services.AddSingleton<IClassificationVersionSource, ClassificationVersionSource>();
        services.AddScoped<IEmailContentNormalizer, EmailContentNormalizer>();
        services.AddScoped<IEmailAnalysisContextFactory, EmailAnalysisContextFactory>();
        services.AddScoped<SingleVerdictClassifier>();
        services.AddScoped<IEmailAnalysisHarness, EmailAnalysisHarness>();
        services.AddScoped<HarnessEvaluationRunner>();
        services.AddScoped<IMessageClassifier, HarnessMessageClassifier>();
        return services;
    }
}
