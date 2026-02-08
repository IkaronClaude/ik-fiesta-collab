using Microsoft.Extensions.DependencyInjection;
using Mimir.Core.Providers;

namespace Mimir.RawTables;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMimirRawTables(this IServiceCollection services)
    {
        services.AddSingleton<IDataProvider, RawTableDataProvider>();
        return services;
    }
}
