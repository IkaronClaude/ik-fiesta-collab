using Microsoft.Extensions.DependencyInjection;
using Fiesta.Collab.Core.Providers;

namespace Fiesta.Collab.ShineTable;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMimirTextTables(this IServiceCollection services)
    {
        services.AddSingleton<IDataProvider, ShineTableDataProvider>();
        services.AddSingleton<IDataProvider, ConfigTableDataProvider>();
        return services;
    }
}
