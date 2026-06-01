using Microsoft.Extensions.DependencyInjection;
using Fiesta.Collab.Core.Providers;
using Fiesta.Collab.Shn.Crypto;

namespace Fiesta.Collab.Shn;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMimirShn(this IServiceCollection services)
    {
        services.AddSingleton<IShnCrypto, ShnCrypto>();
        services.AddSingleton<IDataProvider, ShnDataProvider>();
        services.AddSingleton<IDataProvider, QuestDataProvider>();
        return services;
    }
}
