using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigilo.Core;

namespace Vigilo.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddVigiloStorage(this IServiceCollection services, StorageOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(options.ProtectedSettingsPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(options.ModelSettingsPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(options.SettingsTransactionJournalPath)!);

        services.AddSingleton(options);
        services.AddSingleton<LiveReportChangeNotifier>();
        services.AddDbContextFactory<VigiloDbContext>(db => db.UseSqlite($"Data Source={options.DatabasePath}"));
        services.AddScoped<IAccountSettingsService, AccountSettingsService>();
        services.AddScoped<ISettingsCoordinator, SettingsCoordinator>();
        services.AddScoped<SettingsTransactionRecoveryService>();
        services.AddScoped<ISenderRuleService, SenderRuleService>();
        services.AddScoped<IEmailProcessingService, EmailProcessingService>();
        services.AddScoped<ILiveReportService, LiveReportService>();
        services.AddScoped<ILocalDataMaintenanceService, LocalDataMaintenanceService>();
        services.AddScoped<ISentReplyDetectionService, SentReplyDetectionService>();
        services.AddSingleton<IOutlookCategoryMapper, OutlookCategoryMapper>();
        services.AddScoped<IOutlookSyncStore, OutlookSyncStore>();
        services.AddScoped<IAppBootstrapper, AppBootstrapper>();
        services.AddScoped<IAppBootstrapper, SettingsTransactionRecoveryBootstrapper>();
        return services;
    }
}
