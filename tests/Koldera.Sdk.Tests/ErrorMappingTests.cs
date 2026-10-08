using System.Net;

namespace Koldera.Tests;

public sealed class ErrorMappingTests
{
    private static KolderaClient ClientAnswering(HttpStatusCode status, string body)
    {
        var http = new HttpClient(new StubHandler((_, _) => StubHandler.Json(status, body, "application/problem+json")));
        return new KolderaClient(new KolderaClientOptions { BaseUrl = new Uri("http://koldera.test"), Token = "t", StorageHttpClient = http }, http);
    }

    [Fact]
    public async Task TheClassFollowsTheHttpStatusAndTheBodyCodeIsKept()
    {
        using var client = ClientAnswering(HttpStatusCode.NotFound, """{"type":"about:blank","title":"Moved","status":500,"code":"JOB_GONE"}""");
        var error = await Assert.ThrowsAsync<NotFoundException>(() => client.InfoAsync());
        Assert.Equal(404, error.Status);
        Assert.Equal("JOB_GONE", error.Code);
    }

    [Fact]
    public async Task ABodyWithACodeButNoStatusKeepsItsCode()
    {
        using var client = ClientAnswering(HttpStatusCode.TooManyRequests, """{"title":"Slow down","code":"QUEUE_FULL"}""");
        var error = await Assert.ThrowsAsync<RateLimitException>(() => client.InfoAsync());
        Assert.Equal(429, error.Status);
        Assert.Equal("QUEUE_FULL", error.Code);
    }

    [Fact]
    public async Task ABodyWithoutACodeFallsBackToTheStatusClass()
    {
        using var client = ClientAnswering(HttpStatusCode.BadGateway, """{"title":"Bad gateway","status":404}""");
        var error = await Assert.ThrowsAsync<ServerException>(() => client.InfoAsync());
        Assert.Equal(502, error.Status);
        Assert.Equal("INTERNAL_ERROR", error.Code);
    }
}
