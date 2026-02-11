using Microsoft.Extensions.DependencyInjection;
using Mimir.Core.Providers;

namespace Mimir.ShineTable;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMimirTextTables(this IServiceCollection services)
    {
        services.AddSingleton<IDataProvider, ShineTableDataProvider>();
        services.AddSingleton<IDataProvider, ConfigTableDataProvider>();
        return services;
    }
}
