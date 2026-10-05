using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Koldera.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task AddKolderaRegistersAnInterfaceClientOnTheHttpClientFactory()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, """{"version":"0.1.0","features":["pdf"]}"""));
        var services = new ServiceCollection();
        services.AddKoldera(options =>
        {
            options.BaseUrl = new Uri("https://koldera.example.com/");
            options.Token = "di-token";
            options.UserAgentSuffix = "di-test";
        }).ConfigurePrimaryHttpMessageHandler(() => stub);
        await using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IKolderaClient>();
        var info = await client.InfoAsync();

        Assert.Equal(["pdf"], info.Features);
        var request = stub.Seen[0].Request;
        Assert.Equal("https://koldera.example.com/v1/info", request.RequestUri!.ToString());
        Assert.Equal("di-token", request.Headers.Authorization!.Parameter);
        Assert.EndsWith("di-test", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFactoryClientHasNoCompetingHttpClientTimeout()
    {
        var services = new ServiceCollection();
        services.AddKoldera(options =>
        {
            options.BaseUrl = new Uri("https://koldera.example.com/");
            options.Token = "t";
        });
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        Assert.Equal(Timeout.InfiniteTimeSpan, factory.CreateClient(nameof(IKolderaClient)).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), provider.GetRequiredService<IOptions<KolderaClientOptions>>().Value.Timeout);
    }

    private static ServiceCollection Registered(HttpMessageHandler? storage = null)
    {
        var services = new ServiceCollection();
        if (storage is not null)
        {
            services.AddSingleton(new SharedStorageClient(storage));
        }

        services.AddKoldera(options =>
        {
            options.BaseUrl = new Uri("https://koldera.example.com/");
            options.Token = "di-token";
        });
        return services;
    }

    [Fact]
    public async Task EveryResolutionSharesOneStorageClientThatOutlivesTheClients()
    {
        await using var provider = Registered().BuildServiceProvider();
        KolderaClient first;
        using (var scope = provider.CreateScope())
        {
            first = (KolderaClient)scope.ServiceProvider.GetRequiredService<IKolderaClient>();
        }

        using var other = provider.CreateScope();
        var second = (KolderaClient)other.ServiceProvider.GetRequiredService<IKolderaClient>();
        var shared = provider.GetRequiredService<SharedStorageClient>().Http;

        Assert.NotSame(first, second);
        Assert.Same(shared, first.StorageHttp);
        Assert.Same(shared, second.StorageHttp);
        first.Dispose();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:1/");
        await Assert.ThrowsAsync<HttpRequestException>(() => shared.SendAsync(request));
    }

    [Fact]
    public async Task StorageCallsOfADiClientCarryNoAuthorizationHeader()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var storage = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        await using var provider = Registered(storage).BuildServiceProvider();
        var client = provider.GetRequiredService<IKolderaClient>();
        var artifact = new Artifact { Kind = "original", DownloadUrl = "https://storage.example.com/d", SizeBytes = 3, Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() };

        Assert.Equal(bytes, await client.Artifacts.DownloadAsync(artifact));
        Assert.Null(storage.Seen[0].Request.Headers.Authorization);
    }

    [Fact]
    public void ARegisteredClientWithoutConfigurationFailsOnFirstUseWithAClearError()
    {
        var savedUrl = Environment.GetEnvironmentVariable("KOLDERA_URL");
        var savedToken = Environment.GetEnvironmentVariable("KOLDERA_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("KOLDERA_URL", null);
            Environment.SetEnvironmentVariable("KOLDERA_TOKEN", null);
            var services = new ServiceCollection();
            services.AddKoldera(_ => { });
            using var provider = services.BuildServiceProvider();
            Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IKolderaClient>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("KOLDERA_URL", savedUrl);
            Environment.SetEnvironmentVariable("KOLDERA_TOKEN", savedToken);
        }
    }

    [Fact]
    public void TheInterfaceCanBeFakedBecauseEveryResourceIsAnInterface()
    {
        IKolderaClient client = new FakeClient();
        Assert.IsAssignableFrom<IJobs>(client.Jobs);
        Assert.IsAssignableFrom<IPdfJobs>(client.Pdf);
    }

    private sealed class FakeClient : IKolderaClient
    {
        public IUploads Uploads => null!;

        public IJobs Jobs { get; } = new FakeJobs();

        public IPdfJobs Pdf { get; } = new FakePdf();

        public IHtmlToPdfJobs HtmlToPdf => null!;

        public IWebJobs Web => null!;

        public IDocumentJobs Documents => null!;

        public IArtifacts Artifacts => null!;

        public IClientSessions ClientSessions => null!;

        public Task<Info> InfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Info());

        public Task<Health> HealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Health());
    }

    private sealed class FakePdf : IPdfJobs
    {
        public Task<Job> CreateJobAsync(Stream pdf, string mode, PdfJobOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new Job());
    }

    private sealed class FakeJobs : IJobs
    {
        public Task<Job> GetAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(new Job());

        public Task<Job> WaitAsync(Guid jobId, WaitOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new Job());

        public Task<IReadOnlyList<Finding>> ListFindingsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Finding>>([]);

        public Task<IReadOnlyList<RemediationAction>> ListActionsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RemediationAction>>([]);

        public Task<IReadOnlyList<Artifact>> ListArtifactsAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Artifact>>([]);

        public Task<ReadingOrderSnapshot> GetReadingOrderAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult(new ReadingOrderSnapshot());

        public Task<Job> RemediateReadingOrderAsync(Guid jobId, ReadingOrderRemediation approval, CreateOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new Job());
    }
}
