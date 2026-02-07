using Microsoft.Extensions.DependencyInjection;

namespace Mimir.RawTables;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMimirRawTables(this IServiceCollection services)
    {
        // TODO: Register raw table providers when implemented
        return services;
    }
}
