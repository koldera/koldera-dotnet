using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Koldera;

/// <summary>Registers the Koldera client with dependency injection.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IKolderaClient"/> as a typed client of <see cref="IHttpClientFactory"/> and binds
    /// <see cref="KolderaClientOptions"/>, validated on start. The returned builder accepts handlers such as retry or logging.
    /// The factory client gets an infinite <c>HttpClient.Timeout</c>: the SDK applies its own per-request timeout.
    /// </summary>
    public static IHttpClientBuilder AddKoldera(this IServiceCollection services, Action<KolderaClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return Register(services, configure);
    }

    /// <summary>
    /// As <see cref="AddKoldera(IServiceCollection, Action{KolderaClientOptions})"/>, reading BaseUrl, Token, Timeout,
    /// StorageTimeout (time spans such as 00:00:30) and UserAgentSuffix from a configuration section.
    /// </summary>
    public static IHttpClientBuilder AddKoldera(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);
        return Register(services, options =>
        {
            if (section["BaseUrl"] is { Length: > 0 } url)
            {
                options.BaseUrl = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : throw new ConfigurationException("BaseUrl is not an absolute URL");
            }

            if (section["Token"] is { Length: > 0 } token)
            {
                options.Token = token;
            }

            if (section["UserAgentSuffix"] is { Length: > 0 } suffix)
            {
                options.UserAgentSuffix = suffix;
            }

            if (section["Timeout"] is { Length: > 0 } timeout)
            {
                options.Timeout = Span(timeout, "Timeout");
            }

            if (section["StorageTimeout"] is { Length: > 0 } storage)
            {
                options.StorageTimeout = Span(storage, "StorageTimeout");
            }
        });
    }

    private static TimeSpan Span(string text, string name) =>
        TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : throw new ConfigurationException($"{name} is not a time span");

    private static IHttpClientBuilder Register(IServiceCollection services, Action<KolderaClientOptions> configure)
    {
        services.TryAddSingleton<SharedStorageClient>();
        services.AddOptions<KolderaClientOptions>().Configure(configure).ValidateOnStart();
        services.AddOptions<KolderaClientOptions>().PostConfigure<SharedStorageClient>((options, storage) => options.StorageHttpClient ??= storage.Http);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<KolderaClientOptions>, KolderaOptionsValidator>());
        return services.AddHttpClient<IKolderaClient, KolderaClient>(client => client.Timeout = Timeout.InfiniteTimeSpan);
    }

    private sealed class KolderaOptionsValidator : IValidateOptions<KolderaClientOptions>
    {
        public ValidateOptionsResult Validate(string? name, KolderaClientOptions options)
        {
            try
            {
                KolderaClient.Resolve(options);
                return ValidateOptionsResult.Success;
            }
            catch (ConfigurationException error)
            {
                return ValidateOptionsResult.Fail(error.Message);
            }
        }
    }
}
