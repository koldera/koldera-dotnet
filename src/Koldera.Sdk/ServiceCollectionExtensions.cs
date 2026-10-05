using Microsoft.Extensions.DependencyInjection;

namespace Koldera;

/// <summary>Registers the Koldera client with dependency injection.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IKolderaClient"/> as a typed client of <see cref="IHttpClientFactory"/> and binds
    /// <see cref="KolderaClientOptions"/>. The returned builder accepts handlers such as retry or logging.
    /// The factory client gets an infinite <c>HttpClient.Timeout</c>: the SDK applies its own per-request timeout.
    /// </summary>
    public static IHttpClientBuilder AddKoldera(this IServiceCollection services, Action<KolderaClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<KolderaClientOptions>().Configure(configure);
        return services.AddHttpClient<IKolderaClient, KolderaClient>(client => client.Timeout = Timeout.InfiniteTimeSpan);
    }
}
