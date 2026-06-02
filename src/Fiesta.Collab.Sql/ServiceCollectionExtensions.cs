using Microsoft.Extensions.DependencyInjection;

namespace Fiesta.Collab.Sql;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFiestaSql(this IServiceCollection services)
    {
        services.AddTransient<ISqlEngine, SqlEngine>();
        return services;
    }
}
