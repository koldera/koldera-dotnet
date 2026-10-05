namespace Koldera;

/// <summary>The one storage client of a service provider: pooled connections, no handlers of the caller, disposed with the provider.</summary>
internal sealed class SharedStorageClient : IDisposable
{
    public SharedStorageClient()
        : this(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
    }

    internal SharedStorageClient(HttpMessageHandler handler) => Http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

    public HttpClient Http { get; }

    public void Dispose() => Http.Dispose();
}
