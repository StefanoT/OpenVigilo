using Microsoft.Extensions.DependencyInjection;
using Vigilo.Core;

namespace Vigilo.Email;

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddVigiloEmail(this IServiceCollection services)
    {
        services.AddSingleton<EmailScanQueue>();
        services.AddSingleton<IEmailScanQueue>(sp => sp.GetRequiredService<EmailScanQueue>());
        services.AddSingleton<IEmailScanProgressNotifier, EmailScanProgressNotifier>();
        services.AddScoped<Core.IEmailScanner, ImapEmailScanner>();
        services.AddHostedService<EmailMonitorService>();
        services.AddHostedService<RetentionSchedulerService>();
        return services;
    }
}
