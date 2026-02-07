using Microsoft.Extensions.DependencyInjection;
using Mimir.Core.Project;

namespace Mimir.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMimirCore(this IServiceCollection services)
    {
        services.AddSingleton<IProjectService, ProjectService>();
        return services;
    }
}
