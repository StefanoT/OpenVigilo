using Microsoft.Extensions.DependencyInjection;
using Vigilo.Core;

namespace Vigilo.Outlook;

public static class OutlookServiceCollectionExtensions
{
    public static IServiceCollection AddVigiloOutlook(this IServiceCollection services)
    {
        services.AddSingleton<IOutlookStaDispatcher, OutlookStaDispatcher>();
        services.AddSingleton<IOutlookClient, ClassicOutlookComClient>();
        services.AddHostedService<OutlookCategorySyncWorker>();
        return services;
    }
}
