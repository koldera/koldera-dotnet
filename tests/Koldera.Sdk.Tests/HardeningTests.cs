using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Koldera.Tests;

public sealed class HardeningTests
{
    private const string Ticket = """{"uploadId":"11111111-1111-4111-8111-111111111111","method":"PUT","uploadUrl":"http://koldera.test/u?exp=1&sig=s","expiresAt":"2026-10-05T10:10:00Z"}""";
    private const string Info = """{"features":[]}""";

    private static KolderaClient ClientOn(HttpClient http, Action<KolderaClientOptions>? tweak = null)
    {
        var options = new KolderaClientOptions { BaseUrl = new Uri("http://koldera.test"), Token = "t" };
        options.StorageHttpClient = http;
        tweak?.Invoke(options);
        return new KolderaClient(options, http);
    }

    private static async Task<HttpClient> UsedClient(HttpMessageHandler handler, TimeSpan timeout)
    {
        var http = new HttpClient(handler) { Timeout = timeout };
        using var probe = await http.GetAsync("http://koldera.test/probe");
        return http;
    }

    // ---- HttpClient timeout ------------------------------------------------------------------------

    [Fact]
    public void ASuppliedHttpClientKeepsItsTimeout()
    {
        using var http = new HttpClient(new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, Info))) { Timeout = TimeSpan.FromSeconds(5) };
        using var client = ClientOn(http);
        Assert.Equal(TimeSpan.FromSeconds(5), http.Timeout);
    }

    [Fact]
    public async Task AUsedHttpClientWithAShortTimeoutRefusesCallsThatNeedMoreBeforeSending()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, Info));
        using var http = await UsedClient(stub, TimeSpan.FromSeconds(100));
        using var client = ClientOn(http);
        Assert.Equal(TimeSpan.FromSeconds(100), http.Timeout);
        var seen = stub.Seen.Count;
        var error = await Assert.ThrowsAsync<ConfigurationException>(() => client.Web.CreateScanAsync(new Uri("https://example.com/"), new CreateOptions { Wait = 900 }));
        Assert.Contains("HttpClient", error.Message, StringComparison.Ordinal);
        Assert.Equal(seen, stub.Seen.Count);
        await client.InfoAsync();
        Assert.Equal(seen + 1, stub.Seen.Count);
    }

    [Fact]
    public async Task AnHttpClientTimeoutIsReportedWithTheHttpClientValue()
    {
        var stub = new StubHandler((request, _) => request.RequestUri!.AbsolutePath == "/probe"
            ? StubHandler.Json(HttpStatusCode.OK, Info)
            : throw new TaskCanceledException("timed out", new TimeoutException()));
        using var http = await UsedClient(stub, TimeSpan.FromSeconds(100));
        using var client = ClientOn(http);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => client.InfoAsync());
        Assert.Contains("HttpClient.Timeout of 00:01:40", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("after 00:00:30", error.Message, StringComparison.Ordinal);
    }

    // ---- storage timeout ---------------------------------------------------------------------------

    private sealed class Storage(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    [Fact]
    public async Task AStorageExchangeUsesTheStorageTimeoutNotTheApiTimeout()
    {
        var slowPut = new Storage(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return StubHandler.Json(HttpStatusCode.Created, Ticket);
            }

            await Task.Delay(300, ct);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(slowPut);
        using var client = ClientOn(http, o => o.Timeout = TimeSpan.FromMilliseconds(50));
        var ticket = await client.Uploads.UploadAsync(Env.Stream([1, 2, 3]), UploadContentTypes.Pdf, "a.pdf");
        Assert.Equal(Guid.Parse("11111111-1111-4111-8111-111111111111"), ticket.UploadId);
    }

    [Fact]
    public async Task AStorageTimeoutThatElapsesIsReported()
    {
        var hangingPut = new Storage(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return StubHandler.Json(HttpStatusCode.Created, Ticket);
            }

            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(hangingPut);
        using var client = ClientOn(http, o => o.StorageTimeout = TimeSpan.FromMilliseconds(60));
        var error = await Assert.ThrowsAsync<TimeoutException>(() => client.Uploads.UploadAsync(Env.Stream([1, 2, 3]), UploadContentTypes.Pdf, "a.pdf"));
        Assert.Contains("00:00:00.0600000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADownloadUsesTheStorageTimeoutToo()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var slow = new Storage(async (_, ct) =>
        {
            await Task.Delay(300, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        using var http = new HttpClient(slow);
        using var client = ClientOn(http, o => o.Timeout = TimeSpan.FromMilliseconds(50));
        var artifact = new Artifact { Kind = "original", DownloadUrl = "http://koldera.test/d", SizeBytes = 3, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        Assert.Equal(bytes, await client.Artifacts.DownloadAsync(artifact));
    }

    [Fact]
    public void TimeoutsMustBePositive()
    {
        Assert.Throws<ConfigurationException>(() => new KolderaClient(new KolderaClientOptions { BaseUrl = new Uri("http://koldera.test"), Token = "t", Timeout = TimeSpan.Zero }));
        Assert.Throws<ConfigurationException>(() => new KolderaClient(new KolderaClientOptions { BaseUrl = new Uri("http://koldera.test"), Token = "t", StorageTimeout = TimeSpan.FromSeconds(-1) }));
    }

    // ---- credentials on signed URLs ----------------------------------------------------------------

    [Fact]
    public async Task SignedUrlsNeverReceiveTheDefaultAuthorizationHeaderOfTheCallersHttpClient()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var seen = new List<(string Method, string? Authorization)>();
        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var context = await listener.GetContextAsync();
                seen.Add((context.Request.HttpMethod, context.Request.Headers.AllKeys.Contains("Authorization", StringComparer.OrdinalIgnoreCase) ? "present" : null));
                if (context.Request.HttpMethod == "GET")
                {
                    context.Response.OutputStream.Write([1, 2, 3]);
                }

                context.Response.StatusCode = context.Request.HttpMethod == "GET" ? 200 : 204;
                context.Response.Close();
            }
        });
        var origin = $"http://127.0.0.1:{port}";
        var tickets = new Storage((request, _) => Task.FromResult(StubHandler.Json(HttpStatusCode.Created, Ticket.Replace("http://koldera.test", origin, StringComparison.Ordinal))));
        using var http = new HttpClient(tickets);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "callers-own-secret");
        using var client = new KolderaClient(new KolderaClientOptions { BaseUrl = new Uri("http://koldera.test"), Token = "t" }, http);
        await client.Uploads.UploadAsync(Env.Stream([1, 2, 3]), UploadContentTypes.Pdf, "a.pdf");
        var artifact = new Artifact { Kind = "original", DownloadUrl = origin + "/d", SizeBytes = 3, Sha256 = Convert.ToHexString(SHA256.HashData([1, 2, 3])).ToLowerInvariant() };
        await client.Artifacts.DownloadAsync(artifact);
        await serving;
        Assert.Equal(["PUT", "GET"], seen.Select(s => s.Method));
        Assert.All(seen, s => Assert.True(s.Authorization is null, $"{s.Method} carried an Authorization header"));
    }

    [Fact]
    public async Task ApiCallsKeepTheSdkTokenEvenWhenTheHttpClientHasADefault()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, Info));
        using var http = new HttpClient(stub);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "callers-own-secret");
        using var client = ClientOn(http);
        await client.InfoAsync();
        Assert.Equal("t", stub.Seen[0].Request.Headers.Authorization!.Parameter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SdkOwnedStorageClientsRefuseRedirects(bool sharedByDependencyInjection)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var paths = new List<string>();

        // HttpListener.Stop closes the request queue before IsListening turns false, so a pending GetContextAsync can
        // fail with "operation aborted" while IsListening is still true. Use our own flag, set before Stop, instead.
        using var stopping = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var context = await listener.GetContextAsync();
                    lock (paths)
                    {
                        paths.Add(context.Request.Url!.AbsolutePath);
                    }

                    if (context.Request.Url!.AbsolutePath == "/d")
                    {
                        context.Response.StatusCode = 302;
                        context.Response.RedirectLocation = $"http://127.0.0.1:{port}/elsewhere";
                    }
                    else
                    {
                        context.Response.OutputStream.Write([1, 2, 3]);
                    }

                    context.Response.Close();
                }
            }
            catch (Exception) when (stopping.IsCancellationRequested)
            {
            }
        });
        using var shared = new SharedStorageClient();
        var options = new KolderaClientOptions { BaseUrl = new Uri("http://koldera.test"), Token = "t" };
        if (sharedByDependencyInjection)
        {
            options.StorageHttpClient = shared.Http;
        }

        using var api = new HttpClient(new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, Info)));
        using var client = new KolderaClient(options, api);
        var artifact = new Artifact { Kind = "original", DownloadUrl = $"http://127.0.0.1:{port}/d", SizeBytes = 3, Sha256 = Convert.ToHexString(SHA256.HashData([1, 2, 3])).ToLowerInvariant() };
        var error = await Assert.ThrowsAsync<StorageException>(() => client.Artifacts.DownloadAsync(artifact));
        stopping.Cancel();
        listener.Stop();
        await serving;
        Assert.Equal(302, error.Status);
        Assert.Equal(["/d"], paths);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task DownloadToFileRefusesAMissingDirectoryEarly()
    {
        var (env, artifact, _) = Download();
        using (env)
        {
            var target = Path.Combine(Path.GetTempPath(), "koldera-missing-" + Guid.NewGuid().ToString("N"), "out.pdf");
            await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Artifacts.DownloadToFileAsync(artifact, target));
            Assert.Empty(env.Handler.Exchanges);
        }
    }

    [Fact]
    public async Task StorageNeverUsesTheCallersClientOrItsHandlers()
    {
        var api = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.Created, Ticket));
        using var http = new HttpClient(api);
        using var client = new KolderaClient(new KolderaClientOptions { BaseUrl = new Uri("http://koldera.test"), Token = "t" }, http);
        var artifact = new Artifact { Kind = "original", DownloadUrl = "http://127.0.0.1:1/d", SizeBytes = 3, Sha256 = new string('0', 64) };
        await Assert.ThrowsAnyAsync<Exception>(() => client.Artifacts.DownloadAsync(artifact));
        Assert.Empty(api.Seen);
    }

    // ---- files -------------------------------------------------------------------------------------

    private static (Env Env, Artifact Artifact, byte[] Bytes) Download()
    {
        var bytes = GoldenHandler.ResponseBytes("artifact-download-original");
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var artifact = new Artifact { Kind = "original", DownloadUrl = "http://koldera.test/d", SizeBytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        return (new Env(inner: stub), artifact, bytes);
    }

    private static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "koldera-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task DownloadToFileWritesTheVerifiedBytesAndLeavesNoTemporaryFile()
    {
        var (env, artifact, bytes) = Download();
        var dir = TempDirectory();
        try
        {
            using (env)
            {
                var target = Path.Combine(dir, "out.pdf");
                await env.Client.Artifacts.DownloadToFileAsync(artifact, target);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
                Assert.Equal([target], Directory.GetFileSystemEntries(dir));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DownloadToFileNeverOverwritesAndCleansUp()
    {
        var (env, artifact, _) = Download();
        var dir = TempDirectory();
        try
        {
            using (env)
            {
                var target = Path.Combine(dir, "out.pdf");
                await File.WriteAllTextAsync(target, "mine");
                await Assert.ThrowsAsync<IOException>(() => env.Client.Artifacts.DownloadToFileAsync(artifact, target));
                Assert.Equal("mine", await File.ReadAllTextAsync(target));
                Assert.Equal([target], Directory.GetFileSystemEntries(dir));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DownloadToFileWritesNothingWhenTheHashDiffers()
    {
        var (env, artifact, _) = Download();
        var dir = TempDirectory();
        try
        {
            using (env)
            {
                var target = Path.Combine(dir, "out.pdf");
                await Assert.ThrowsAsync<StorageException>(() => env.Client.Artifacts.DownloadToFileAsync(artifact with { Sha256 = new string('0', 64) }, target));
                Assert.Empty(Directory.GetFileSystemEntries(dir));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- version, DI -------------------------------------------------------------------------------

    [Fact]
    public async Task TheUserAgentVersionComesFromTheAssemblyWithoutTheCommitSuffix()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, Info));
        using var http = new HttpClient(stub);
        using var client = ClientOn(http);
        await client.InfoAsync();
        Assert.Equal("koldera-sdk-dotnet/" + KolderaClient.SdkVersion, stub.Seen[0].Request.Headers.UserAgent.ToString());
        Assert.DoesNotContain('+', KolderaClient.SdkVersion);
        Assert.Equal("0.1.0", KolderaClient.SdkVersion);
    }

    [Fact]
    public void InvalidOptionsFailOnStartAndNotJustOnFirstUse()
    {
        var services = new ServiceCollection();
        services.AddKoldera(o =>
        {
            o.BaseUrl = new Uri("ftp://koldera.test");
            o.Token = "t";
        });
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        var negative = new ServiceCollection();
        negative.AddKoldera(o =>
        {
            o.BaseUrl = new Uri("http://koldera.test");
            o.Token = "t";
            o.Timeout = TimeSpan.FromSeconds(-1);
        });
        using var second = negative.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => second.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void ABadKolderaUrlVariableRaisesInsteadOfBeingIgnored()
    {
        var saved = Environment.GetEnvironmentVariable("KOLDERA_URL");
        try
        {
            Environment.SetEnvironmentVariable("KOLDERA_URL", "not a url");
            var error = Assert.Throws<ConfigurationException>(() => new KolderaClient(new KolderaClientOptions { Token = "t" }));
            Assert.Contains("KOLDERA_URL", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KOLDERA_URL", saved);
        }
    }

    [Fact]
    public async Task AddKolderaReadsAConfigurationSection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Koldera:BaseUrl"] = "https://koldera.example.com/",
            ["Koldera:Token"] = "cfg-token",
            ["Koldera:Timeout"] = "00:00:12",
            ["Koldera:StorageTimeout"] = "00:03:00",
            ["Koldera:UserAgentSuffix"] = "cfg",
        }).Build();
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, Info));
        var services = new ServiceCollection();
        services.AddKoldera(configuration.GetSection("Koldera")).ConfigurePrimaryHttpMessageHandler(() => stub);
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
        var options = provider.GetRequiredService<IOptions<KolderaClientOptions>>().Value;
        Assert.Equal((TimeSpan.FromSeconds(12), TimeSpan.FromMinutes(3)), (options.Timeout, options.StorageTimeout));
        await provider.GetRequiredService<IKolderaClient>().InfoAsync();
        Assert.Equal("cfg-token", stub.Seen[0].Request.Headers.Authorization!.Parameter);
        Assert.Equal("https://koldera.example.com/v1/info", stub.Seen[0].Request.RequestUri!.ToString());
    }
}
