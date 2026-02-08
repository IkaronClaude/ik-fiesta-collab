using Microsoft.Extensions.DependencyInjection;
using Mimir.Core.Providers;

namespace Mimir.ShineTable;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMimirShineTable(this IServiceCollection services)
    {
        services.AddSingleton<IDataProvider, ShineTableDataProvider>();
        return services;
    }
}
