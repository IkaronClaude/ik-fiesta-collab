using Microsoft.Extensions.DependencyInjection;
using Fiesta.Collab.Core.Project;

namespace Fiesta.Collab.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFiestaCore(this IServiceCollection services)
    {
        services.AddSingleton<IProjectService, ProjectService>();
        return services;
    }
}
