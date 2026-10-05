using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Koldera.Tests;

/// <summary>The decisions that matter beyond the fixtures: waiting, retries, open enums, verified downloads, keys.</summary>
public sealed class BehaviorTests
{
    private static MemoryStream Pdf() => Env.Stream(GoldenHandler.RequestBytes("upload-put-pdf"));

    private static readonly string[] PdfUpload = ["upload-ticket-pdf", "upload-put-pdf"];

    // ---- Prefer: wait ------------------------------------------------------------------------------

    [Fact]
    public async Task AWaitThatRunsOutReturnsTheRunningJobAndDoesNotPoll()
    {
        using var env = new Env();
        env.Handler.Expect([.. PdfUpload, "pdf-job-prefer-wait-timeout"]);
        var job = await env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf", Wait = 5 });
        Assert.False(job.IsFinished);
        Assert.Equal(3, env.Handler.Exchanges.Count);
        Assert.Equal("wait=5", env.Handler.Exchanges[2].Header("Prefer"));
        Assert.Null(env.Handler.Exchanges[0].Header("Prefer"));
    }

    [Fact]
    public async Task UntilFinishedContinuesPollingAfterA202AndHonoursRetryAfter()
    {
        using var env = new Env();
        env.Handler.Expect([.. PdfUpload, "pdf-job-prefer-wait-timeout", "job-queued-retry-after", "job-running", "job-completed"]);
        var updates = new List<string>();
        var job = await env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf", Wait = 5, UntilFinished = true, Poll = new WaitOptions { OnUpdate = j => updates.Add(j.State) } });
        Assert.Equal(JobStates.Completed, job.State);
        Assert.Equal(["queued", "running", "completed"], updates);
        Assert.Equal(TimeSpan.FromSeconds(2), env.Pauses[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), env.Pauses[1]);
    }

    [Fact]
    public async Task UntilFinishedDoesNotPollWhenTheAnswerIsAlreadyFinal()
    {
        using var env = new Env();
        env.Handler.Expect([.. PdfUpload, "pdf-job-prefer-wait-finished"]);
        var job = await env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf", Wait = 60, UntilFinished = true });
        Assert.True(job.IsFinished);
        Assert.Equal(3, env.Handler.Exchanges.Count);
    }

    [Fact]
    public async Task WaitAsyncBacksOffWithoutRetryAfterUpToTenSeconds()
    {
        var polls = 0;
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK,
            $$"""{"id":"4d2d5a6e-2b1c-4e4a-9d6b-1f3c5e7a9b01","kind":"pdf","state":"{{(++polls < 5 ? "running" : "completed")}}","createdAt":"2026-10-05T10:00:00Z","manualReviewRequired":true}"""));
        using var env = new Env(inner: stub);
        var job = await env.Client.Jobs.WaitAsync(Env.JobId, new WaitOptions { InitialDelay = TimeSpan.FromSeconds(4) });
        Assert.Equal(JobStates.Completed, job.State);
        Assert.Equal([TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)], env.Pauses);
    }

    [Fact]
    public async Task WaitAsyncTimesOutWithTheJobIdAndLeavesTheJobRunning()
    {
        using var env = new Env();
        env.Handler.Expect("job-running");
        var error = await Assert.ThrowsAsync<JobTimeoutException>(() => env.Client.Jobs.WaitAsync(Env.JobId, new WaitOptions { Timeout = TimeSpan.FromMilliseconds(1) }));
        Assert.Equal(Env.JobId, error.JobId);
        Assert.Empty(env.Pauses);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(901)]
    [InlineData(-5)]
    public async Task AnInvalidWaitIsRejectedBeforeAnyRequest(int wait)
    {
        using var env = new Env();
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Analyze, new PdfJobOptions { Wait = wait }));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Web.CreateScanAsync(new Uri("https://example.com/"), new CreateOptions { Wait = wait }));
        Assert.Empty(env.Handler.Exchanges);
    }

    // ---- open enums --------------------------------------------------------------------------------

    [Fact]
    public async Task UnknownEnumValuesFromANewerCoreArePreservedAsStrings()
    {
        var stub = new StubHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/info" => StubHandler.Json(HttpStatusCode.OK, """{"version":"9.9","features":["document","teleport"],"extra":1}"""),
            var path when path.EndsWith("/findings", StringComparison.Ordinal) => StubHandler.Json(HttpStatusCode.OK, """{"items":[{"source":"ppt","ruleId":"X","message":"m","needsHumanReview":false,"resolution":"half_fixed"}]}"""),
            var path when path.EndsWith("/actions", StringComparison.Ordinal) => StubHandler.Json(HttpStatusCode.OK, """{"items":[{"ruleId":"R","outcome":"deferred","reason":"r"}]}"""),
            var path when path.EndsWith("/artifacts", StringComparison.Ordinal) => StubHandler.Json(HttpStatusCode.OK, """{"items":[{"id":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1","kind":"holo_report","sha256":"00","sizeBytes":1,"downloadUrl":"http://x/","expiresAt":"2026-10-05T10:00:00Z"}]}"""),
            _ => StubHandler.Json(HttpStatusCode.OK, """{"id":"4d2d5a6e-2b1c-4e4a-9d6b-1f3c5e7a9b01","kind":"quantum_pdf","mode":"hologram","state":"paused","createdAt":"2026-10-05T10:00:00Z","remediationStatus":"partly","manualReviewRequired":true,"validation":{"profile":"p","status":"maybe","failedRules":[]},"newField":{"a":1}}"""),
        });
        using var env = new Env(inner: stub);
        var job = await env.Client.Jobs.GetAsync(Env.JobId);
        Assert.Equal(("quantum_pdf", "hologram", "paused", "partly", "maybe"), (job.Kind, job.Mode, job.State, job.RemediationStatus, job.Validation!.Status));
        Assert.True(job.IsFinished);
        var info = await env.Client.InfoAsync();
        Assert.Equal(["document", "teleport"], info.Features);
        var finding = Assert.Single(await env.Client.Jobs.ListFindingsAsync(Env.JobId));
        Assert.Equal(("ppt", "half_fixed"), (finding.Source, finding.Resolution));
        Assert.Equal("deferred", Assert.Single(await env.Client.Jobs.ListActionsAsync(Env.JobId)).Outcome);
        Assert.Equal("holo_report", Assert.Single(await env.Client.Jobs.ListArtifactsAsync(Env.JobId)).Kind);
    }

    [Fact]
    public async Task UnknownProblemCodesAndStatusesKeepTheirCodeAndMapToTheBaseClasses()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.Gone, """{"type":"about:blank","title":"Gone","status":410,"code":"BRAND_NEW_CODE","detail":"d"}""", "application/problem+json"));
        using var env = new Env(inner: stub);
        var error = await Assert.ThrowsAsync<ApiException>(() => env.Client.Jobs.GetAsync(Env.JobId));
        Assert.Equal((410, "BRAND_NEW_CODE", "d"), (error.Status, error.Code, error.Detail));
        Assert.Equal(typeof(ApiException), error.GetType());
        Assert.DoesNotContain(GoldenHandler.ServerToken, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400, typeof(ValidationException))]
    [InlineData(401, typeof(AuthenticationException))]
    [InlineData(403, typeof(PermissionException))]
    [InlineData(404, typeof(NotFoundException))]
    [InlineData(409, typeof(ConflictException))]
    [InlineData(415, typeof(UnsupportedMediaTypeException))]
    [InlineData(421, typeof(HostNotAllowedException))]
    [InlineData(422, typeof(ValidationException))]
    [InlineData(429, typeof(RateLimitException))]
    [InlineData(500, typeof(ServerException))]
    [InlineData(502, typeof(ServerException))]
    [InlineData(503, typeof(ServiceUnavailableException))]
    public async Task EveryStatusMapsToItsClass(int status, Type expected)
    {
        var stub = new StubHandler((_, _) => StubHandler.Json((HttpStatusCode)status, $$"""{"type":"about:blank","title":"t","status":{{status}},"code":"SOME_CODE"}""", "application/problem+json"));
        using var env = new Env(inner: stub);
        var error = await Assert.ThrowsAnyAsync<ApiException>(() => env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Analyze, new PdfJobOptions { IdempotencyKey = "k" }));
        Assert.Equal(expected, error.GetType());
        Assert.Equal(status, error.Status);
    }

    [Fact]
    public async Task ANonJsonErrorBodyBecomesAFallbackProblem()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.BadGateway, "<html>bad gateway</html>", "text/html"));
        using var env = new Env(inner: stub);
        var error = await Assert.ThrowsAsync<ServerException>(() => env.Client.InfoAsync());
        Assert.Equal((502, "INTERNAL_ERROR"), (error.Status, error.Code));
        var notFound = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.NotFound, "nope", "text/plain"));
        using var env2 = new Env(inner: notFound);
        var unknown = await Assert.ThrowsAsync<NotFoundException>(() => env2.Client.InfoAsync());
        Assert.Equal("UNKNOWN", unknown.Code);
    }

    [Fact]
    public async Task ProblemErrorsAreParsedAndMalformedEntriesDropped()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.UnprocessableEntity,
            """{"type":"about:blank","title":"Invalid document","status":422,"code":"VALIDATION_ERROR","errors":[{"path":"/body/0","code":"SCHEMA","message":"m"},{"path":"/x"},{"code":"NEW_ONE","path":"","message":"n"}]}""", "application/problem+json"));
        using var env = new Env(inner: stub);
        var error = await Assert.ThrowsAsync<ValidationException>(() => env.Client.Documents.CreateJobAsync(new StructuredDocument("T", "en", [new HeadingBlock(1, "H")])));
        Assert.Equal(["SCHEMA", "NEW_ONE"], error.Errors.Select(e => e.Code));
        Assert.Equal(["/body/0", ""], error.Errors.Select(e => e.Path));
    }

    // ---- downloads ---------------------------------------------------------------------------------

    private static (Env Env, Artifact Artifact) DownloadSetup(Func<byte[], byte[]> serve, Func<byte[], Artifact, Artifact> declare)
    {
        var real = GoldenHandler.ResponseBytes("artifact-download-original");
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(serve(real)) });
        var artifact = declare(real, new Artifact { Kind = "original", DownloadUrl = "http://koldera.test/v1/artifacts/a/content?exp=1&sig=x", Sha256 = Convert.ToHexString(SHA256.HashData(real)).ToLowerInvariant(), SizeBytes = real.Length });
        return (new Env(inner: stub), artifact);
    }

    [Fact]
    public async Task AnArtifactWithTheWrongHashIsNeverReturnedOrWritten()
    {
        var (env, artifact) = DownloadSetup(b => b, (_, a) => a with { Sha256 = new string('0', 64) });
        using (env)
        {
            using var destination = new MemoryStream();
            var error = await Assert.ThrowsAsync<StorageException>(() => env.Client.Artifacts.DownloadAsync(artifact, destination));
            Assert.Contains("SHA-256 mismatch", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, destination.Length);
            await Assert.ThrowsAsync<StorageException>(() => env.Client.Artifacts.DownloadAsync(artifact));
        }
    }

    [Fact]
    public async Task AnArtifactWithTheWrongSizeIsNeverReturnedOrWritten()
    {
        var (shortEnv, shortArtifact) = DownloadSetup(b => b[..^1], (_, a) => a);
        using (shortEnv)
        {
            using var destination = new MemoryStream();
            var error = await Assert.ThrowsAsync<StorageException>(() => shortEnv.Client.Artifacts.DownloadAsync(shortArtifact, destination));
            Assert.Contains("received", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, destination.Length);
        }

        var (longEnv, longArtifact) = DownloadSetup(b => [.. b, 1, 2, 3], (_, a) => a);
        using (longEnv)
        {
            var error = await Assert.ThrowsAsync<StorageException>(() => longEnv.Client.Artifacts.DownloadAsync(longArtifact));
            Assert.Contains("more than the declared", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DownloadsSendNoCredential()
    {
        var (env, artifact) = DownloadSetup(b => b, (_, a) => a);
        using (env)
        {
            var bytes = await env.Client.Artifacts.DownloadAsync(artifact);
            Assert.Equal(artifact.SizeBytes, bytes.Length);
        }
    }

    // ---- retries and idempotency -------------------------------------------------------------------

    [Fact]
    public async Task ACreationPostRetriedAfterANetworkErrorKeepsItsIdempotencyKey()
    {
        using var env = new Env();
        env.Handler.Expect(PdfUpload);
        env.Handler.FailNext(r => r.RequestUri!.AbsolutePath == "/v1/pdf/jobs", new HttpRequestException("connection reset"));
        env.Handler.ExpectWith("pdf-job-idempotent-replay", []);
        var job = await RunWithFaultBetween(env);
        Assert.Equal(JobStates.Queued, job.State);
        var posts = env.Handler.Exchanges.Where(x => x.Request.RequestUri!.AbsolutePath == "/v1/pdf/jobs").ToList();
        Assert.Equal(2, posts.Count);
        Assert.NotNull(posts[0].Header("Idempotency-Key"));
        Assert.Equal(posts[0].Header("Idempotency-Key"), posts[1].Header("Idempotency-Key"));
        Assert.Equal(posts[0].Body, posts[1].Body);
        Assert.Equal(TimeSpan.FromMilliseconds(250), env.Pauses[0]);
    }

    private static async Task<Job> RunWithFaultBetween(Env env)
    {
        // The upload succeeds, the job POST fails once on the network and is sent again.
        return await env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf" });
    }

    [Fact]
    public async Task ANetworkErrorOnTheUploadTicketIsRetriedWithTheSameKey()
    {
        using var env = new Env();
        env.Handler.FailNext(new HttpRequestException("connection reset"));
        env.Handler.Expect("upload-ticket-pdf", "upload-put-pdf");
        await env.Client.Uploads.UploadAsync(Pdf(), UploadContentTypes.Pdf, "sample.pdf", "ticket-key");
        var posts = env.Handler.Exchanges.Where(x => x.Request.RequestUri!.AbsolutePath == "/v1/uploads" && x.Request.Method == HttpMethod.Post).ToList();
        Assert.Equal(["ticket-key", "ticket-key"], posts.Select(p => p.Header("Idempotency-Key")));
    }

    [Fact]
    public async Task NetworkRetriesAreBoundedAndTheLastErrorSurfaces()
    {
        using var env = new Env();
        for (var i = 0; i < 3; i++)
        {
            env.Handler.FailNext(new HttpRequestException("down"));
        }

        await Assert.ThrowsAsync<HttpRequestException>(() => env.Client.Jobs.GetAsync(Env.JobId));
        Assert.Equal(3, env.Handler.Exchanges.Count);
        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)], env.Pauses);
    }

    [Fact]
    public async Task APlainTimeoutIsRetriedWithTheSameKeyAndThenSurfacesAsATimeout()
    {
        var attempts = 0;
        using var env = new Env(timeout: TimeSpan.FromMilliseconds(50), inner: new SlowHandler(() => attempts++));
        await Assert.ThrowsAsync<TimeoutException>(() => env.Client.Web.CreateScanAsync(new Uri("https://example.com/"), new CreateOptions { IdempotencyKey = "k" }));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ATimeoutOfAHeldRequestIsNotRetriedWhileOtherTimeoutsAre()
    {
        var attempts = 0;
        var thrower = new StubHandler((_, _) =>
        {
            attempts++;
            throw new TimeoutException("held request timed out");
        });
        using var env = new Env(inner: thrower);
        await Assert.ThrowsAsync<TimeoutException>(() => env.Client.Web.CreateScanAsync(new Uri("https://example.com/"), new CreateOptions { Wait = 30 }));
        Assert.Equal(1, attempts);
        attempts = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => env.Client.Web.CreateScanAsync(new Uri("https://example.com/")));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task AHeldRequestGetsATimeoutLongerThanItsWait()
    {
        using var env = new Env(timeout: TimeSpan.FromMilliseconds(50), inner: new SlowHandler(() => { }));
        var started = DateTime.UtcNow;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var error = await Record.ExceptionAsync(() => env.Client.Web.CreateScanAsync(new Uri("https://example.com/"), new CreateOptions { Wait = 1, IdempotencyKey = "k" }, cts.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task ACallerCancellationIsNeverRetriedOrReportedAsATimeout()
    {
        var slow = new SlowHandler(() => { });
        using var env = new Env(inner: slow);
        using var cts = new CancellationTokenSource();
        var task = env.Client.Jobs.GetAsync(Env.JobId, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Client.InfoAsync(new CancellationToken(true)));
    }

    [Fact]
    public async Task BusyReadsAreRetriedAfterRetryAfterButCreationsAreNot()
    {
        var calls = 0;
        var stub = new StubHandler((request, _) =>
        {
            calls++;
            return request.Method == HttpMethod.Get && calls < 3
                ? StubHandler.Json(HttpStatusCode.ServiceUnavailable, """{"type":"about:blank","title":"Busy","status":503,"code":"QUEUE_FULL"}""", "application/problem+json", ("Retry-After", "3"))
                : request.Method == HttpMethod.Get
                    ? StubHandler.Json(HttpStatusCode.OK, """{"id":"4d2d5a6e-2b1c-4e4a-9d6b-1f3c5e7a9b01","kind":"pdf","state":"completed","createdAt":"2026-10-05T10:00:00Z","manualReviewRequired":true}""")
                    : StubHandler.Json(HttpStatusCode.ServiceUnavailable, """{"type":"about:blank","title":"Busy","status":503,"code":"QUEUE_FULL"}""", "application/problem+json", ("Retry-After", "30"));
        });
        using var env = new Env(inner: stub);
        Assert.True((await env.Client.Jobs.GetAsync(Env.JobId)).IsFinished);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)], env.Pauses);
        calls = 10;
        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => env.Client.Web.CreateScanAsync(new Uri("https://example.com/")));
        Assert.Equal(TimeSpan.FromSeconds(30), error.RetryAfter);
        Assert.Equal(11, calls);
    }

    [Fact]
    public async Task ACallerKeyIsTrimmedAndTheUploadGetsADerivedKey()
    {
        using var env = new Env();
        env.Handler.Expect([.. PdfUpload, "pdf-job-accepted"]);
        await env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Analyze, new PdfJobOptions { FileName = "sample.pdf", IdempotencyKey = "  order 42/a  " });
        Assert.Equal("order 42/a", env.Handler.Exchanges[2].Header("Idempotency-Key"));
        Assert.Equal("order 42/a:upload", env.Handler.Exchanges[0].Header("Idempotency-Key"));
    }

    [Fact]
    public async Task ALongCallerKeyGetsAShortenedDerivedUploadKeyWithinTheLimit()
    {
        using var env = new Env();
        var key = new string('k', 255);
        env.Handler.Expect([.. PdfUpload, "pdf-job-accepted"]);
        await env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Analyze, new PdfJobOptions { FileName = "sample.pdf", IdempotencyKey = key });
        Assert.Equal(key, env.Handler.Exchanges[2].Header("Idempotency-Key"));
        Assert.True(env.Handler.Exchanges[0].Header("Idempotency-Key")!.Length <= 255);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tab\there")]
    [InlineData("bell\u0007")]
    [InlineData("naïve")]
    [InlineData("日本語")]
    [InlineData("delete\u007f")]
    public async Task InvalidCallerKeysAreRejectedBeforeAnyRequest(string key)
    {
        using var env = new Env();
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Analyze, new PdfJobOptions { IdempotencyKey = key }));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Web.CreateScanAsync(new Uri("https://example.com/"), new CreateOptions { IdempotencyKey = key }));
        Assert.Empty(env.Handler.Exchanges);
    }

    [Fact]
    public async Task AKeyOfTwoHundredFiftySixCharactersIsRejectedAndTwoHundredFiftyFiveIsAccepted()
    {
        using var env = new Env();
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Web.CreateScanAsync(new Uri("https://example.com/"), new CreateOptions { IdempotencyKey = new string('a', 256) }));
        env.Handler.Expect("web-job-accepted");
        await env.Client.Web.CreateScanAsync(new Uri("https://example.com/page"), new CreateOptions { IdempotencyKey = new string('a', 255) });
    }

    [Fact]
    public async Task EveryCreationCallGeneratesADistinctKeyWhenNoneIsGiven()
    {
        using var env = new Env();
        env.Handler.Expect("web-job-accepted", "web-job-accepted");
        await env.Client.Web.CreateScanAsync(new Uri("https://example.com/page"));
        await env.Client.Web.CreateScanAsync(new Uri("https://example.com/page"));
        var keys = env.Handler.Exchanges.Select(x => x.Header("Idempotency-Key")).ToList();
        Assert.NotEqual(keys[0], keys[1]);
        Assert.All(keys, k => Assert.Matches("^[0-9a-f]{32}$", k!));
    }

    [Fact]
    public async Task AReplayedTicketWith409AlreadyCompletedCountsAsStoredOnlyWhenReplayed()
    {
        using (var env = new Env())
        {
            env.Handler.Expect("upload-ticket-pdf", "upload-put-already-completed");
            var error = await Assert.ThrowsAsync<StorageException>(() => env.Client.Uploads.UploadAsync(Pdf(), UploadContentTypes.Pdf, "sample.pdf"));
            Assert.Equal((409, "UPLOAD_ALREADY_COMPLETED"), (error.Status, error.Code));
        }

        using var replayed = new Env();
        replayed.Handler.ExpectWith("upload-ticket-pdf", new Dictionary<string, string> { ["Idempotent-Replayed"] = "true" });
        replayed.Handler.Expect("upload-put-checksum-mismatch");
        var other = await Assert.ThrowsAsync<StorageException>(() => replayed.Client.Uploads.UploadAsync(Pdf(), UploadContentTypes.Pdf, "sample.pdf"));
        Assert.Equal("CHECKSUM_MISMATCH", other.Code);
    }

    // ---- uploads -----------------------------------------------------------------------------------

    [Fact]
    public async Task NonSeekableStreamsAreBufferedAndDisposedOrEmptyStreamsFailBeforeAnyRequest()
    {
        using var env = new Env();
        env.Handler.Expect("upload-ticket-html", "upload-put-html");
        using var forward = new ForwardOnlyStream(GoldenHandler.RequestBytes("upload-put-html"));
        await env.Client.Uploads.UploadAsync(forward, UploadContentTypes.Html, "page.html");
        var disposed = new MemoryStream();
        disposed.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => env.Client.Uploads.UploadAsync(disposed, UploadContentTypes.Pdf, "a.pdf"));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Uploads.UploadAsync(new MemoryStream(), UploadContentTypes.Pdf, "a.pdf"));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Uploads.UploadAsync(Pdf(), "text/plain", "a.txt"));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Uploads.UploadAsync(Pdf(), UploadContentTypes.Pdf, ""));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Uploads.UploadAsync(Pdf(), UploadContentTypes.Pdf, new string('n', 256)));
        Assert.Equal(2, env.Handler.Exchanges.Count);
    }

    [Fact]
    public async Task OnlyImageTypesAreAcceptedForImageUploads()
    {
        using var env = new Env();
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Documents.UploadImageAsync(Pdf(), UploadContentTypes.Pdf));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Documents.UploadImageAsync(Pdf(), "image/gif"));
        Assert.Empty(env.Handler.Exchanges);
        foreach (var type in new[] { UploadContentTypes.Png, UploadContentTypes.Jpeg, UploadContentTypes.Svg })
        {
            var stub = new StubHandler((request, _) => request.Method == HttpMethod.Post
                ? StubHandler.Json(HttpStatusCode.Created, $$"""{"uploadId":"33333333-3333-4333-8333-333333333333","method":"PUT","uploadUrl":"http://koldera.test/v1/uploads/x/content?exp=1&sig=s","expiresAt":"2026-10-05T10:10:00Z","maxBytes":null}""")
                : new HttpResponseMessage(HttpStatusCode.NoContent));
            using var e2 = new Env(inner: stub);
            var ticket = await e2.Client.Documents.UploadImageAsync(Env.Stream([1, 2, 3]), type);
            Assert.Equal(Guid.Parse("33333333-3333-4333-8333-333333333333"), ticket.UploadId);
            var sent = JsonNode.Parse(stub.Seen[0].Body)!;
            Assert.Equal(type, (string?)sent["contentType"]);
            Assert.Equal("image", (string?)sent["fileName"]);
            Assert.Equal(type, stub.Seen[1].Request.Content!.Headers.ContentType!.MediaType);
        }
    }

    [Fact]
    public async Task AFileLargerThanTheTicketLimitIsRefusedBeforeTheBytesAreSent()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.Created, """{"uploadId":"11111111-1111-4111-8111-111111111111","method":"PUT","uploadUrl":"http://koldera.test/u","expiresAt":"2026-10-05T10:10:00Z","maxBytes":2}"""));
        using var env = new Env(inner: stub);
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Uploads.UploadAsync(Env.Stream([1, 2, 3]), UploadContentTypes.Pdf, "a.pdf"));
        Assert.Single(stub.Seen);
    }

    // ---- inputs ------------------------------------------------------------------------------------

    private static StubHandler AcceptingStub() => new((request, _) => request.RequestUri!.AbsolutePath == "/v1/uploads" && request.Method == HttpMethod.Post
        ? StubHandler.Json(HttpStatusCode.Created, """{"uploadId":"11111111-1111-4111-8111-111111111111","method":"PUT","uploadUrl":"http://koldera.test/u","expiresAt":"2026-10-05T10:10:00Z"}""")
        : request.Method == HttpMethod.Put
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : StubHandler.Json(HttpStatusCode.Accepted, """{"id":"4d2d5a6e-2b1c-4e4a-9d6b-1f3c5e7a9b01","kind":"pdf","state":"queued","createdAt":"2026-10-05T10:00:00Z","manualReviewRequired":true}"""));

    [Fact]
    public async Task OptionalFieldsAreAbsentOnTheWireWhenNotSet()
    {
        var stub = AcceptingStub();
        using var env = new Env(inner: stub);
        await env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Analyze);
        var body = (JsonObject)JsonNode.Parse(stub.Seen[2].Body)!;
        Assert.Equal(["mode", "profile", "uploadId"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        await env.Client.HtmlToPdf.CreateJobAsync(Pdf(), HtmlToPdfJobModes.GenerateFix);
        var html = (JsonObject)JsonNode.Parse(stub.Seen[5].Body)!;
        Assert.Equal(["mode", "profile", "uploadId"], html.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TitleLanguageSelectorAndModesAreValidatedBeforeAnyRequest()
    {
        using var env = new Env();
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Pdf.CreateJobAsync(Pdf(), "safe_fix"));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Pdf.CreateJobAsync(Pdf(), "generate_fix"));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.HtmlToPdf.CreateJobAsync(Pdf(), "fix"));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Fix, new PdfJobOptions { ExplicitTitle = "  " }));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Fix, new PdfJobOptions { ExplicitTitle = new string('x', 501) }));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Fix, new PdfJobOptions { ExplicitTitle = "bad\ntitle" }));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Fix, new PdfJobOptions { ExplicitLanguage = "not a tag" }));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.HtmlToPdf.CreateJobAsync(Pdf(), HtmlToPdfJobModes.GenerateFix, new HtmlToPdfJobOptions { ContentSelector = new string('a', 201) }));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Web.CreateScanAsync(new Uri("ftp://example.com/")));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Web.CreateScanAsync(new Uri("/relative", UriKind.Relative)));
        Assert.Empty(env.Handler.Exchanges);
    }

    [Fact]
    public async Task ATitleOf500CodePointsIsAcceptedEvenWhenItNeedsSurrogatePairs()
    {
        var stub = AcceptingStub();
        using var env = new Env(inner: stub);
        var title = string.Concat(Enumerable.Repeat("😀", 500));
        Assert.Equal(1000, title.Length);
        await env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Fix, new PdfJobOptions { ExplicitTitle = "  " + title + "  " });
        Assert.Equal(title, (string?)JsonNode.Parse(stub.Seen[2].Body)!["explicitTitle"]);
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Pdf.CreateJobAsync(Pdf(), PdfJobModes.Fix, new PdfJobOptions { ExplicitTitle = title + "x" }));
    }

    // ---- reading order -----------------------------------------------------------------------------

    private static ReadingOrderRemediation Approval(Action<ReadingOrderRemediationBuilder>? change = null)
    {
        var b = new ReadingOrderRemediationBuilder();
        change?.Invoke(b);
        return new ReadingOrderRemediation
        {
            BaseArtifactSha256 = b.Hash,
            SnapshotVersion = 1,
            Target = RemediationTargets.PdfTags,
            OrderedNodeIds = b.Ids,
            Acknowledged = b.Acknowledged,
            Reviewer = b.Reviewer,
        };
    }

    private sealed class ReadingOrderRemediationBuilder
    {
        public string Hash { get; set; } = new string('a', 64);

        public IReadOnlyList<string> Ids { get; set; } = ["n1", "n2"];

        public bool Acknowledged { get; set; } = true;

        public string? Reviewer { get; set; }
    }

    [Fact]
    public async Task ARemediationIsRefusedLocallyUntilItIsAcknowledgedAndWellFormed()
    {
        using var env = new Env();
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Jobs.RemediateReadingOrderAsync(Env.JobId, Approval(b => b.Acknowledged = false)));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Jobs.RemediateReadingOrderAsync(Env.JobId, Approval(b => b.Ids = ["only"])));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Jobs.RemediateReadingOrderAsync(Env.JobId, Approval(b => b.Ids = ["a", "a"])));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Jobs.RemediateReadingOrderAsync(Env.JobId, Approval(b => b.Hash = "ABC")));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Jobs.RemediateReadingOrderAsync(Env.JobId, Approval(b => b.Hash = new string('A', 64))));
        Assert.Empty(env.Handler.Exchanges);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two\nlines")]
    public async Task ABlankOrControlReviewerIsRefused(string reviewer)
    {
        using var env = new Env();
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Jobs.RemediateReadingOrderAsync(Env.JobId, Approval(b => b.Reviewer = reviewer)));
        await Assert.ThrowsAsync<ConfigurationException>(() => env.Client.Jobs.RemediateReadingOrderAsync(Env.JobId, Approval(b => b.Reviewer = new string('r', 201))));
        Assert.Empty(env.Handler.Exchanges);
    }

    // ---- configuration -----------------------------------------------------------------------------

    [Fact]
    public void ConfigurationComesFromOptionsOrTheEnvironmentAndNoPrefixIsRequired()
    {
        var savedUrl = Environment.GetEnvironmentVariable("KOLDERA_URL");
        var savedToken = Environment.GetEnvironmentVariable("KOLDERA_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("KOLDERA_URL", null);
            Environment.SetEnvironmentVariable("KOLDERA_TOKEN", null);
            Assert.Throws<ConfigurationException>(() => new KolderaClient(new KolderaClientOptions()));
            Assert.Throws<ConfigurationException>(() => new KolderaClient(new KolderaClientOptions { Token = "x" }));
            Assert.Throws<ConfigurationException>(() => new KolderaClient(new KolderaClientOptions { BaseUrl = new Uri(GoldenHandler.BaseUrl) }));
            Assert.Throws<ConfigurationException>(() => new KolderaClient(new KolderaClientOptions { BaseUrl = new Uri(GoldenHandler.BaseUrl), Token = "   " }));
            Assert.Throws<ConfigurationException>(() => new KolderaClient(new KolderaClientOptions { BaseUrl = new Uri("ftp://koldera.test"), Token = "x" }));
            using var shortToken = new KolderaClient(new KolderaClientOptions { BaseUrl = new Uri(GoldenHandler.BaseUrl), Token = "x" });
            Environment.SetEnvironmentVariable("KOLDERA_URL", GoldenHandler.BaseUrl);
            Environment.SetEnvironmentVariable("KOLDERA_TOKEN", "from-env");
            using var fromEnv = new KolderaClient(new KolderaClientOptions());
            Assert.NotNull(fromEnv.Jobs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KOLDERA_URL", savedUrl);
            Environment.SetEnvironmentVariable("KOLDERA_TOKEN", savedToken);
        }
    }

    [Fact]
    public void TheTokenIsNeverPrinted()
    {
        var options = new KolderaClientOptions { BaseUrl = new Uri(GoldenHandler.BaseUrl), Token = "very-secret-token" };
        Assert.DoesNotContain("very-secret-token", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABaseUrlWithAPathPrefixIsKept()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, """{"features":[]}"""));
        using var http = new HttpClient(stub);
        using var client = new KolderaClient(new KolderaClientOptions { BaseUrl = new Uri("https://example.com/koldera/"), Token = "t" }, http);
        await client.InfoAsync();
        Assert.Equal("https://example.com/koldera/v1/info", stub.Seen[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task TheUserAgentSuffixIsAppended()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, """{"features":[]}"""));
        using var http = new HttpClient(stub);
        using var client = new KolderaClient(new KolderaClientOptions { BaseUrl = new Uri("https://example.com"), Token = "t", UserAgentSuffix = "my-app/2.1" }, http);
        await client.InfoAsync();
        Assert.Equal("koldera-sdk-dotnet/0.1.0 my-app/2.1", stub.Seen[0].Request.Headers.UserAgent.ToString());
    }

    private sealed class SlowHandler(Action onSend) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
