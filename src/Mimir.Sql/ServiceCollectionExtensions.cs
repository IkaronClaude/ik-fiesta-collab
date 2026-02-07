using Microsoft.Extensions.DependencyInjection;

namespace Mimir.Sql;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMimirSql(this IServiceCollection services)
    {
        services.AddTransient<ISqlEngine, SqlEngine>();
        return services;
    }
}
