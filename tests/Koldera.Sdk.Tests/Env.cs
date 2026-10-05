using System.Net;
using System.Text;

namespace Koldera.Tests;

/// <summary>A client wired to a scripted handler, with polling pauses recorded instead of slept.</summary>
public sealed class Env : IDisposable
{
    public static readonly Guid JobId = Guid.Parse("4d2d5a6e-2b1c-4e4a-9d6b-1f3c5e7a9b01");

    public static readonly Guid OtherJobId = Guid.Parse("4d2d5a6e-2b1c-4e4a-9d6b-1f3c5e7a9b02");

    public Env(string token = GoldenHandler.ServerToken, TimeSpan? timeout = null, HttpMessageHandler? inner = null)
    {
        Handler = new GoldenHandler();
        Http = new HttpClient(inner ?? Handler);
        Client = new KolderaClient(
            new KolderaClientOptions
            {
                BaseUrl = new Uri(GoldenHandler.BaseUrl),
                Token = token,
                Timeout = timeout ?? TimeSpan.FromSeconds(30),
                DelayAsync = (delay, ct) =>
                {
                    Pauses.Add(delay);
                    return Task.CompletedTask;
                },
            },
            Http);
    }

    public GoldenHandler Handler { get; }

    public HttpClient Http { get; }

    public KolderaClient Client { get; }

    public List<TimeSpan> Pauses { get; } = [];

    public static MemoryStream Stream(byte[] bytes) => new(bytes);

    public static Env Session() => new(GoldenHandler.SessionToken);

    public void Dispose()
    {
        Client.Dispose();
        Http.Dispose();
        Handler.AssertConsumed();
    }
}

/// <summary>Answers every request with one scripted response; for cases the golden fixtures do not cover.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, byte[], HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, byte[] Body)> Seen { get; } = [];

    public static HttpResponseMessage Json(HttpStatusCode status, string json, string contentType = "application/json", params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, contentType) };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Seen.Add((request, body));
        return respond(request, body);
    }
}
