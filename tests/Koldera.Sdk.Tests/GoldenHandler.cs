using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Koldera.Tests;

public sealed record Fixture(string Name, string OperationId, string Auth, FixtureRequest Request, FixtureResponse Response);

public sealed record FixtureRequest(string Method, string Path, Dictionary<string, string>? Headers, JsonNode? Body, string? RawBody, string[]? ForbiddenHeaders, string? Sha256);

public sealed record FixtureResponse(int Status, Dictionary<string, string>? Headers, JsonNode? Body, string? RawBody, string? Sha256);

/// <summary>
/// Scripted handler: checks every request the SDK sends against the next golden fixture (method, path,
/// headers, exact body) and answers with the fixture's response.
/// </summary>
public sealed class GoldenHandler : HttpMessageHandler
{
    public const string ServerToken = "test-server-token";
    public const string SessionToken = "v1.eyJmYWtlIjoidHJ1ZSJ9.ZmFrZS1zaWduYXR1cmU";
    public const string BaseUrl = "http://koldera.test";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, Fixture> All = Load();
    private readonly Queue<(Fixture Fixture, Dictionary<string, string> ExtraHeaders)> _script = new();
    private readonly List<(Func<HttpRequestMessage, bool> Match, Exception Error)> _faults = [];

    public List<string> Mismatches { get; } = [];

    public List<Exchange> Exchanges { get; } = [];

    public static IEnumerable<string> Names => All.Keys.Order(StringComparer.Ordinal);

    public static Fixture Get(string name) => All[name];

    public static byte[] RequestBytes(string name) => Encoding.Latin1.GetBytes(All[name].Request.RawBody!);

    public static byte[] ResponseBytes(string name) => Encoding.Latin1.GetBytes(All[name].Response.RawBody!);

    public static string Resolve(string text) => text
        .Replace("{{baseUrl}}", BaseUrl, StringComparison.Ordinal)
        .Replace("{{secretKey}}", ServerToken, StringComparison.Ordinal)
        .Replace("{{sessionToken}}", SessionToken, StringComparison.Ordinal);

    public void Expect(params string[] names)
    {
        foreach (var name in names)
        {
            ExpectWith(name, new Dictionary<string, string>());
        }
    }

    /// <summary>Expects the fixture but adds response headers (for example Idempotent-Replayed) that the fixture does not carry.</summary>
    public void ExpectWith(string name, Dictionary<string, string> extraResponseHeaders)
    {
        _script.Enqueue((All.TryGetValue(name, out var fixture) ? fixture : throw new ArgumentException("unknown fixture " + name), extraResponseHeaders));
    }

    /// <summary>The next request matching the predicate is recorded and then fails with this exception, without consuming a fixture.</summary>
    public void FailNext(Func<HttpRequestMessage, bool> match, Exception error) => _faults.Add((match, error));

    /// <summary>The next request is recorded and then fails with this exception, without consuming a fixture.</summary>
    public void FailNext(Exception error) => FailNext(_ => true, error);

    public void AssertConsumed()
    {
        var problems = Mismatches.Concat(_script.Select(s => "fixture not reached: " + s.Fixture.Name)).ToList();
        Mismatches.Clear();
        _script.Clear();
        if (problems.Count > 0)
        {
            throw new Xunit.Sdk.XunitException(string.Join("\n", problems));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var index = _faults.FindIndex(f => f.Match(request));
        if (index >= 0)
        {
            var fault = _faults[index].Error;
            _faults.RemoveAt(index);
            Exchanges.Add(new Exchange(null, request, body));
            throw fault;
        }

        if (!_script.TryDequeue(out var next))
        {
            Mismatches.Add($"unexpected request {request.Method} {request.RequestUri}");
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }

        var (expected, extra) = next;
        Exchanges.Add(new Exchange(expected, request, body));
        Verify(expected, request, body);
        var response = new HttpResponseMessage((HttpStatusCode)expected.Response.Status);
        var headers = expected.Response.Headers ?? [];
        if (expected.Response.RawBody is not null)
        {
            response.Content = new ByteArrayContent(Encoding.Latin1.GetBytes(expected.Response.RawBody));
        }
        else if (expected.Response.Body is not null)
        {
            response.Content = new StringContent(Resolve(expected.Response.Body.ToJsonString()), Encoding.UTF8);
        }
        else
        {
            response.Content = new ByteArrayContent([]);
        }

        if (headers.TryGetValue("Content-Type", out var contentType))
        {
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        foreach (var (name, value) in headers.Concat(extra))
        {
            if (name is not ("Content-Type" or "Content-Length"))
            {
                response.Headers.TryAddWithoutValidation(name, Resolve(value));
            }
        }

        return response;
    }

    private void Verify(Fixture expected, HttpRequestMessage request, byte[] body)
    {
        var at = expected.Name + ": ";
        if (request.Method.Method != expected.Request.Method)
        {
            Mismatches.Add(at + $"method {request.Method} != {expected.Request.Method}");
        }

        var pathAndQuery = request.RequestUri!.PathAndQuery;
        if (pathAndQuery != expected.Request.Path)
        {
            Mismatches.Add(at + $"path {pathAndQuery} != {expected.Request.Path}");
        }

        foreach (var (name, value) in expected.Request.Headers ?? [])
        {
            var actual = HeaderValue(request, name);
            if (actual is null)
            {
                Mismatches.Add(at + "missing header " + name);
            }
            else if (value == "{{idempotencyKey}}")
            {
                if (actual.Length is < 1 or > 255 || actual.Any(c => c < 0x20 || c > 0x7e))
                {
                    Mismatches.Add(at + "Idempotency-Key is not 1-255 printable ASCII: " + actual);
                }
            }
            else if (actual.Split(';')[0].Trim() != Resolve(value))
            {
                Mismatches.Add(at + $"header {name}: {actual} != {Resolve(value)}");
            }
        }

        foreach (var name in expected.Request.ForbiddenHeaders ?? [])
        {
            if (HeaderValue(request, name) is not null)
            {
                Mismatches.Add(at + "forbidden header present: " + name);
            }
        }

        if (expected.Request.RawBody is not null)
        {
            if (!body.AsSpan().SequenceEqual(Encoding.Latin1.GetBytes(expected.Request.RawBody)))
            {
                Mismatches.Add(at + "raw body differs");
            }
        }
        else if (expected.Request.Body is not null)
        {
            string got;
            try
            {
                got = Canonical(JsonNode.Parse(body)!);
            }
            catch (JsonException)
            {
                Mismatches.Add(at + "body is not JSON");
                return;
            }

            var want = Canonical(expected.Request.Body);
            if (want != got)
            {
                Mismatches.Add(at + $"body {got} != {want}");
            }
        }
        else if (body.Length > 0)
        {
            Mismatches.Add(at + "unexpected request body");
        }
    }

    private static string? HeaderValue(HttpRequestMessage request, string name)
    {
        if (request.Headers.TryGetValues(name, out var values))
        {
            return string.Join(",", values);
        }

        return request.Content is not null && request.Content.Headers.TryGetValues(name, out var contentValues) ? string.Join(",", contentValues) : null;
    }

    private static string Canonical(JsonNode node) => node switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + (p.Value is null ? "null" : Canonical(p.Value)))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(e => e is null ? "null" : Canonical(e))) + "]",
        _ => node.ToJsonString(),
    };

    private static Dictionary<string, Fixture> Load()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "golden");
        var result = new Dictionary<string, Fixture>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(file, Encoding.UTF8), Json) ?? throw new InvalidOperationException(file);
            result[fixture.Name] = fixture;
        }

        return result;
    }
}

public sealed record Exchange(Fixture? Fixture, HttpRequestMessage Request, byte[] Body)
{
    public string? Header(string name) => Request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}
