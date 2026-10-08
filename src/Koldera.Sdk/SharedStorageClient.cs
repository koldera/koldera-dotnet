namespace Koldera;

/// <summary>The one storage client of a service provider: pooled connections, no handlers of the caller, disposed with the provider.</summary>
internal sealed class SharedStorageClient : IDisposable
{
    public SharedStorageClient()
        : this(CreateHandler())
    {
    }

    /// <summary>The handler of every SDK-owned storage client. Redirects are refused: a signed URL must be answered where it points.</summary>
    internal static SocketsHttpHandler CreateHandler() => new() { PooledConnectionLifetime = TimeSpan.FromMinutes(2), AllowAutoRedirect = false };

    internal SharedStorageClient(HttpMessageHandler handler) => Http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

    public HttpClient Http { get; }

    public void Dispose() => Http.Dispose();
}
