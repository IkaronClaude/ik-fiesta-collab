using Microsoft.Extensions.DependencyInjection;
using Mimir.Core.Providers;
using Mimir.Shn.Crypto;

namespace Mimir.Shn;

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
