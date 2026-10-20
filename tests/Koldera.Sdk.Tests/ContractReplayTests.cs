using System.Text.Json.Nodes;

namespace Koldera.Tests;

/// <summary>Every golden fixture is replayed through the SDK; a fixture without a case here fails the run.</summary>
public sealed class ContractReplayTests
{
    private static readonly Guid Image = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly string[] PdfUpload = ["upload-ticket-pdf", "upload-put-pdf"];
    private static readonly string[] HtmlUpload = ["upload-ticket-html", "upload-put-html"];

    public static IEnumerable<object[]> FixtureNames => GoldenHandler.Names.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task ReplaysFixture(string name)
    {
        Assert.True(Cases.TryGetValue(name, out var run), $"no replay case for fixture {name}");
        using var env = GoldenHandler.Get(name).Auth == "session" ? Env.Session() : new Env();
        await run!(env);
    }

    private static void AssertDeclarationWithheld(Job job)
    {
        Assert.Equal(JobStates.Completed, job.State);
        Assert.True(job.ManualReviewRequired);
        foreach (var v in new[] { job.Validation, job.FinalValidation })
        {
            Assert.Equal(ValidationStatuses.DeclarationWithheld, v?.Status);
            Assert.NotEqual(ValidationStatuses.Passed, v?.Status);
            var rule = Assert.Single(v!.FailedRules);
            Assert.Equal(("ISO 14289-1:2014 5-1", 1), (rule.RuleId, rule.Count));
        }
    }

    private static MemoryStream PdfBytes() => Env.Stream(GoldenHandler.RequestBytes("upload-put-pdf"));

    private static MemoryStream HtmlBytes() => Env.Stream(GoldenHandler.RequestBytes("upload-put-html"));

    private static Task<Job> CreatePdf(Env e, string mode, PdfJobOptions? options = null) => e.Client.Pdf.CreateJobAsync(PdfBytes(), mode, options);

    private static Task<Job> CreateHtml(Env e, string mode, HtmlToPdfJobOptions? options = null) => e.Client.HtmlToPdf.CreateJobAsync(HtmlBytes(), mode, options);

    private static void ExpectPdfJob(Env e, string fixture) =>
        e.Handler.Expect(GoldenHandler.Get(fixture).Auth == "session" ? ["upload-ticket-session", "upload-put-pdf", fixture] : [.. PdfUpload, fixture]);

    private static async Task<T> Fails<T>(Func<Task> call, int status, string code)
        where T : ApiException
    {
        var error = await Assert.ThrowsAsync<T>(call);
        Assert.Equal(status, error.Status);
        Assert.Equal(code, error.Code);
        return error;
    }

    private static Artifact ArtifactOf(string fixture, string kind)
    {
        var f = GoldenHandler.Get(fixture);
        return new Artifact { Kind = kind, DownloadUrl = GoldenHandler.BaseUrl + f.Request.Path, Sha256 = f.Response.Sha256 ?? "", SizeBytes = f.Response.RawBody is null ? 45 : GoldenHandler.ResponseBytes(fixture).Length };
    }

    private static StructuredDocument Invoice() => new("Invoice 2026-0042", "en",
    [
        new HeadingBlock(1, "Invoice 2026-0042"),
        new ParagraphBlock(["Thank you ", new StrongInline("very much"), "."]),
        new TableBlock("Items billed", [["Description", "Amount"]], [["Consulting", "1000.00"]]),
        ImageBlock.Described(Image, "Company logo: a blue square"),
    ]);

    private static StructuredDocument BrokenInvoice() => new("T", "en", [new HeadingBlock(3, "Skipped"), ImageBlock.Described(Image, "")]);

    private static async Task ReplayRaw(Env e, string name, string? key)
    {
        var f = GoldenHandler.Get(name);
        e.Handler.Expect(name);
        var transport = new Transport(e.Http, e.Http, new Uri(GoldenHandler.BaseUrl), GoldenHandler.ServerToken, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), "test", null);
        await transport.CallAsync(HttpMethod.Post, f.Request.Path, KolderaJsonContext.Default.UploadTicket, new CallOptions { Body = f.Request.Body!.ToJsonString(), IdempotencyKey = key }, CancellationToken.None);
    }

    private static readonly Dictionary<string, Func<Env, Task>> Cases = new()
    {
        ["actions"] = async e =>
        {
            e.Handler.Expect("actions");
            var actions = await e.Client.Jobs.ListActionsAsync(Env.JobId);
            Assert.NotEmpty(actions);
            Assert.All(actions, a => Assert.False(string.IsNullOrEmpty(a.Outcome)));
        },
        ["artifact-download-not-found"] = async e =>
        {
            e.Handler.Expect("artifact-download-not-found");
            var error = await Assert.ThrowsAsync<StorageException>(() => e.Client.Artifacts.DownloadAsync(ArtifactOf("artifact-download-not-found", "original")));
            Assert.Equal((404, "NOT_FOUND"), (error.Status, error.Code));
        },
        ["artifact-download-original"] = async e =>
        {
            e.Handler.Expect("artifact-download-original");
            var bytes = await e.Client.Artifacts.DownloadAsync(ArtifactOf("artifact-download-original", "original"));
            Assert.Equal(GoldenHandler.ResponseBytes("artifact-download-original"), bytes);
        },
        ["artifact-download-remediated"] = async e =>
        {
            e.Handler.Expect("artifact-download-remediated");
            using var destination = new MemoryStream();
            await e.Client.Artifacts.DownloadAsync(ArtifactOf("artifact-download-remediated", "remediated"), destination);
            Assert.Equal(GoldenHandler.ResponseBytes("artifact-download-remediated"), destination.ToArray());
        },
        ["artifact-download-signature-invalid"] = async e =>
        {
            e.Handler.Expect("artifact-download-signature-invalid");
            var error = await Assert.ThrowsAsync<StorageException>(() => e.Client.Artifacts.DownloadAsync(ArtifactOf("artifact-download-signature-invalid", "remediated")));
            Assert.Equal((403, "SIGNATURE_INVALID"), (error.Status, error.Code));
        },
        ["artifacts-empty"] = async e =>
        {
            e.Handler.Expect("artifacts-empty");
            Assert.Empty(await e.Client.Jobs.ListArtifactsAsync(Env.OtherJobId));
        },
        ["artifacts"] = async e =>
        {
            e.Handler.Expect("artifacts");
            var artifacts = await e.Client.Jobs.ListArtifactsAsync(Env.JobId);
            Assert.Equal(ArtifactKinds.Original, artifacts[0].Kind);
            Assert.Equal(ArtifactKinds.Remediated, artifacts[1].Kind);
            Assert.StartsWith(GoldenHandler.BaseUrl + "/v1/artifacts/", artifacts[0].DownloadUrl, StringComparison.Ordinal);
        },
        ["client-session-defaults"] = async e =>
        {
            e.Handler.Expect("client-session-defaults");
            var session = await e.Client.ClientSessions.CreateAsync(new ClientSessionRequest { AllowedOrigin = "https://app.example.com" });
            Assert.StartsWith("v1.", session.SessionToken, StringComparison.Ordinal);
        },
        ["client-session-forbidden"] = async e =>
        {
            e.Handler.Expect("client-session-forbidden");
            await Fails<PermissionException>(() => e.Client.ClientSessions.CreateAsync(new ClientSessionRequest { AllowedOrigin = "https://app.example.com" }), 403, "FORBIDDEN");
        },
        ["client-session-invalid"] = async e =>
        {
            await Assert.ThrowsAsync<ValidationException>(() => ReplayRaw(e, "client-session-invalid", null));
            await Assert.ThrowsAsync<ConfigurationException>(() => e.Client.ClientSessions.CreateAsync(new ClientSessionRequest { AllowedOrigin = "https://app.example.com/path" }));
        },
        ["client-session-unsupported-media-type"] = async e =>
        {
            e.Handler.Expect("client-session-unsupported-media-type");
            await Fails<UnsupportedMediaTypeException>(() => e.Client.ClientSessions.CreateAsync(new ClientSessionRequest { AllowedOrigin = "https://app.example.com" }), 415, "UNSUPPORTED_MEDIA_TYPE");
        },
        ["client-session"] = async e =>
        {
            e.Handler.Expect("client-session");
            var session = await e.Client.ClientSessions.CreateAsync(new ClientSessionRequest
            {
                AllowedOrigin = "https://app.example.com",
                Scopes = [SessionScopes.JobsCreate, SessionScopes.JobsRead],
                MaxJobs = 3,
                MaxUploads = 6,
                TtlSeconds = 600,
            });
            Assert.Equal(new DateTimeOffset(2026, 10, 5, 10, 10, 0, TimeSpan.Zero), session.ExpiresAt);
        },
        ["document-job-accepted"] = async e =>
        {
            e.Handler.Expect("document-job-accepted");
            var job = await e.Client.Documents.CreateJobAsync(Invoice());
            Assert.Equal(JobKinds.DocumentToPdf, job.Kind);
            Assert.Equal(JobStates.Queued, job.State);
        },
        ["document-job-image-upload-invalid"] = async e =>
        {
            e.Handler.Expect("document-job-image-upload-invalid");
            var error = await Fails<ValidationException>(() => e.Client.Documents.CreateJobAsync(Invoice()), 422, "VALIDATION_ERROR");
            var entry = Assert.Single(error.Errors);
            Assert.Equal(("/body/3/uploadId", "IMAGE_UPLOAD_INVALID"), (entry.Path, entry.Code));
        },
        ["document-job-prefer-wait-finished"] = async e =>
        {
            e.Handler.Expect("document-job-prefer-wait-finished");
            var job = await e.Client.Documents.CreateJobAsync(Invoice(), new CreateOptions { Wait = 30 });
            Assert.True(job.IsFinished);
        },
        ["document-job-validation-errors"] = async e =>
        {
            e.Handler.Expect("document-job-validation-errors");
            var error = await Fails<ValidationException>(() => e.Client.Documents.CreateJobAsync(BrokenInvoice()), 422, "VALIDATION_ERROR");
            Assert.Equal(["HEADING_LEVEL_SKIPPED", "IMAGE_ALT_MISSING"], error.Errors.Select(x => x.Code));
            Assert.Equal(["/body/0/level", "/body/1/alt"], error.Errors.Select(x => x.Path));
        },
        ["findings"] = async e =>
        {
            e.Handler.Expect("findings");
            var findings = await e.Client.Jobs.ListFindingsAsync(Env.JobId);
            Assert.Equal("fixed_in_copy", findings[0].Resolution);
            Assert.True(findings[1].NeedsHumanReview);
            Assert.Null(findings[1].Resolution);
        },
        ["healthz"] = async e =>
        {
            e.Handler.Expect("healthz");
            Assert.Equal("ok", (await e.Client.HealthAsync()).Status);
        },
        ["html-job-accepted"] = async e =>
        {
            e.Handler.Expect([.. HtmlUpload, "html-job-accepted"]);
            var job = await CreateHtml(e, HtmlToPdfJobModes.GenerateFix, new HtmlToPdfJobOptions { FileName = "page.html", ContentSelector = "main", ExplicitLanguage = "en", ExplicitTitle = "Sample page" });
            Assert.Equal(JobKinds.HtmlToPdf, job.Kind);
        },
        ["html-job-analyze"] = async e =>
        {
            e.Handler.Expect([.. HtmlUpload, "html-job-analyze"]);
            Assert.Equal(JobStates.Queued, (await CreateHtml(e, HtmlToPdfJobModes.GenerateAnalyze)).State);
        },
        ["info"] = async e =>
        {
            e.Handler.Expect("info");
            var info = await e.Client.InfoAsync();
            Assert.Equal(["document", "html", "pdf", "web"], info.Features);
            Assert.Equal("0.1.0", info.Version);
        },
        ["info-session"] = async e =>
        {
            e.Handler.Expect("info-session");
            Assert.NotEmpty((await e.Client.InfoAsync()).Features);
        },
        ["job-completed-fix"] = async e =>
        {
            e.Handler.Expect("job-completed-fix");
            var job = await e.Client.Jobs.GetAsync(Env.JobId);
            Assert.True(job.IsFinished);
            Assert.Equal(RemediationStatuses.Applied, job.RemediationStatus);
            Assert.True(job.ManualReviewRequired);
        },
        ["job-completed-fix-declaration-withheld"] = async e =>
        {
            e.Handler.Expect("job-completed-fix-declaration-withheld");
            AssertDeclarationWithheld(await e.Client.Jobs.GetAsync(Guid.Parse("4d2d5a6e-2b1c-4e4a-9d6b-1f3c5e7a9b03")));
        },
        ["job-completed-document-declaration-withheld"] = async e =>
        {
            e.Handler.Expect("job-completed-document-declaration-withheld");
            AssertDeclarationWithheld(await e.Client.Jobs.GetAsync(Guid.Parse("4d2d5a6e-2b1c-4e4a-9d6b-1f3c5e7a9b04")));
        },
        ["job-completed-session"] = async e =>
        {
            e.Handler.Expect("job-completed-session");
            Assert.Equal(JobStates.Completed, (await e.Client.Jobs.GetAsync(Env.JobId)).State);
        },
        ["job-completed"] = async e =>
        {
            e.Handler.Expect("job-completed");
            var job = await e.Client.Jobs.GetAsync(Env.JobId);
            Assert.Equal(ValidationStatuses.Failed, job.Validation?.Status);
            Assert.NotEmpty(job.Validation!.FailedRules);
            Assert.Equal(Env.JobId, job.Id);
        },
        ["job-failed"] = async e =>
        {
            e.Handler.Expect("job-failed");
            var job = await e.Client.Jobs.GetAsync(Env.JobId);
            Assert.True(job.IsFinished);
            Assert.Equal("WORKER_TIMEOUT", job.Error?.Code);
        },
        ["job-insufficient-scope"] = async e =>
        {
            e.Handler.Expect("job-insufficient-scope");
            await Fails<PermissionException>(() => e.Client.Jobs.GetAsync(Env.JobId), 403, "INSUFFICIENT_SCOPE");
        },
        ["job-not-found"] = async e =>
        {
            e.Handler.Expect("job-not-found");
            await Fails<NotFoundException>(() => e.Client.Jobs.GetAsync(Env.OtherJobId), 404, "NOT_FOUND");
        },
        ["job-queued-retry-after"] = async e =>
        {
            e.Handler.Expect("job-queued-retry-after");
            Assert.False((await e.Client.Jobs.GetAsync(Env.JobId)).IsFinished);
        },
        ["job-running"] = async e =>
        {
            e.Handler.Expect("job-running");
            var job = await e.Client.Jobs.GetAsync(Env.JobId);
            Assert.Equal(JobStates.Running, job.State);
            Assert.False(job.IsFinished);
        },
        ["job-unauthorized"] = async e =>
        {
            e.Handler.Expect("job-unauthorized");
            await Fails<AuthenticationException>(() => e.Client.Jobs.GetAsync(Env.JobId), 401, "UNAUTHORIZED");
        },
        ["pdf-job-accepted-fix"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-accepted-fix");
            var job = await CreatePdf(e, PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf", ExplicitLanguage = "en", ExplicitTitle = "  Sample report  " });
            Assert.Equal("fix", job.Mode);
        },
        ["pdf-job-accepted"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-accepted");
            var job = await CreatePdf(e, PdfJobModes.Analyze, new PdfJobOptions { FileName = "sample.pdf" });
            Assert.Equal((JobKinds.Pdf, JobStates.Queued, true), (job.Kind, job.State, job.ManualReviewRequired));
            var ticketKey = e.Handler.Exchanges[0].Header("Idempotency-Key");
            var jobKey = e.Handler.Exchanges[2].Header("Idempotency-Key");
            Assert.Equal(jobKey + ":upload", ticketKey);
            Assert.StartsWith("koldera-sdk-dotnet/", e.Handler.Exchanges[2].Header("User-Agent"), StringComparison.Ordinal);
        },
        ["pdf-job-feature-off"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-feature-off");
            await Fails<NotFoundException>(() => CreatePdf(e, PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf" }), 404, "NOT_FOUND");
        },
        ["pdf-job-idempotency-key-reused"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-idempotency-key-reused");
            await Fails<ConflictException>(() => CreatePdf(e, PdfJobModes.Analyze, new PdfJobOptions { FileName = "sample.pdf", IdempotencyKey = "order-42" }), 409, "IDEMPOTENCY_KEY_REUSED");
            Assert.Equal("order-42", e.Handler.Exchanges[2].Header("Idempotency-Key"));
            Assert.Equal("order-42:upload", e.Handler.Exchanges[0].Header("Idempotency-Key"));
        },
        ["pdf-job-idempotent-replay"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-idempotent-replay");
            ResponseMeta? meta = null;
            await CreatePdf(e, PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf", OnResponse = m => meta = m });
            Assert.True(meta!.IdempotentReplayed);
            Assert.Equal("/v1/jobs/4d2d5a6e-2b1c-4e4a-9d6b-1f3c5e7a9b01", meta.Location);
            Assert.Equal(202, meta.Status);
        },
        ["pdf-job-insufficient-scope"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-insufficient-scope");
            await Fails<PermissionException>(() => CreatePdf(e, PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf" }), 403, "INSUFFICIENT_SCOPE");
        },
        ["pdf-job-invalid-json"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-invalid-json");
            await Fails<ValidationException>(() => CreatePdf(e, PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf" }), 400, "INVALID_JSON");
        },
        ["pdf-job-prefer-wait-finished"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-prefer-wait-finished");
            ResponseMeta? meta = null;
            var job = await CreatePdf(e, PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf", Wait = 60, OnResponse = m => meta = m });
            Assert.True(job.IsFinished);
            Assert.Equal(JobStates.Completed, job.State);
            Assert.Equal("wait=60", meta!.PreferenceApplied);
            Assert.Equal(200, meta.Status);
        },
        ["pdf-job-prefer-wait-timeout"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-prefer-wait-timeout");
            var job = await CreatePdf(e, PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf", Wait = 5 });
            Assert.False(job.IsFinished);
            Assert.Equal(JobStates.Running, job.State);
        },
        ["pdf-job-queue-full"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-queue-full");
            var error = await Fails<ServiceUnavailableException>(() => CreatePdf(e, PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf" }), 503, "QUEUE_FULL");
            Assert.Equal(TimeSpan.FromSeconds(30), error.RetryAfter);
            Assert.IsAssignableFrom<ServerException>(error);
        },
        ["pdf-job-session-job-limit"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-session-job-limit");
            var error = await Fails<RateLimitException>(() => CreatePdf(e, PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf" }), 429, "SESSION_JOB_LIMIT");
            Assert.Null(error.RetryAfter);
        },
        ["pdf-job-session"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-session");
            Assert.Equal(JobStates.Queued, (await CreatePdf(e, PdfJobModes.Analyze, new PdfJobOptions { FileName = "sample.pdf" })).State);
        },
        ["pdf-job-unauthorized"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-unauthorized");
            await Fails<AuthenticationException>(() => CreatePdf(e, PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf" }), 401, "UNAUTHORIZED");
        },
        ["pdf-job-unsupported-input"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-unsupported-input");
            await Fails<UnsupportedMediaTypeException>(() => CreatePdf(e, PdfJobModes.Fix, new PdfJobOptions { FileName = "sample.pdf" }), 415, "UNSUPPORTED_INPUT");
        },
        ["pdf-job-validation-error"] = async e =>
        {
            ExpectPdfJob(e, "pdf-job-validation-error");
            await Fails<ValidationException>(() => CreatePdf(e, PdfJobModes.Analyze, new PdfJobOptions { FileName = "sample.pdf", ExplicitTitle = "x" }), 422, "VALIDATION_ERROR");
        },
        ["reading-order-not-found"] = async e =>
        {
            e.Handler.Expect("reading-order-not-found");
            await Fails<NotFoundException>(() => e.Client.Jobs.GetReadingOrderAsync(Env.OtherJobId), 404, "NOT_FOUND");
        },
        ["reading-order"] = async e =>
        {
            e.Handler.Expect("reading-order");
            var snapshot = await e.Client.Jobs.GetReadingOrderAsync(Env.JobId);
            Assert.True(snapshot.ReviewRequired);
            Assert.Equal("needs_review", snapshot.ReadingOrderReview);
            Assert.Equal(1, snapshot.SnapshotVersion);
            Assert.Equal(64, snapshot.SourceArtifactSha256.Length);
            Assert.Null(snapshot.Nodes[0].ParentId);
            Assert.Equal("pdf_structure", snapshot.Nodes[0].Origin);
            Assert.NotNull(snapshot.Nodes[1].BoundingBox);
        },
        ["remediation-accepted"] = async e =>
        {
            e.Handler.Expect("remediation-accepted");
            var job = await e.Client.Jobs.RemediateReadingOrderAsync(Env.JobId, Approval(reviewer: "  reviewer@example.com  "));
            Assert.Equal(JobKinds.OrderPdf, job.Kind);
        },
        ["remediation-artifact-expired"] = async e =>
        {
            e.Handler.Expect("remediation-artifact-expired");
            await Fails<ConflictException>(() => e.Client.Jobs.RemediateReadingOrderAsync(Env.JobId, Approval()), 409, "ARTIFACT_EXPIRED");
        },
        ["remediation-stale"] = async e =>
        {
            e.Handler.Expect("remediation-stale");
            await Fails<ConflictException>(() => e.Client.Jobs.RemediateReadingOrderAsync(Env.JobId, Approval()), 409, "STALE_READING_ORDER_SNAPSHOT");
        },
        ["remediation-unsupported"] = async e =>
        {
            e.Handler.Expect("remediation-unsupported");
            await Fails<ValidationException>(() => e.Client.Jobs.RemediateReadingOrderAsync(Env.JobId, Approval(RemediationTargets.SourceHtml)), 422, "ORDER_FIX_UNSUPPORTED");
        },
        ["remediation-without-reviewer"] = async e =>
        {
            e.Handler.Expect("remediation-without-reviewer");
            var job = await e.Client.Jobs.RemediateReadingOrderAsync(Env.JobId, Approval(), new CreateOptions { Wait = 60 });
            Assert.True(job.IsFinished);
        },
        ["upload-put-already-completed"] = async e =>
        {
            e.Handler.ExpectWith("upload-ticket-pdf", new Dictionary<string, string> { ["Idempotent-Replayed"] = "true" });
            e.Handler.Expect("upload-put-already-completed");
            var ticket = await e.Client.Uploads.UploadAsync(PdfBytes(), UploadContentTypes.Pdf, "sample.pdf");
            Assert.Equal(Guid.Parse("11111111-1111-4111-8111-111111111111"), ticket.UploadId);
        },
        ["upload-put-checksum-mismatch"] = e => FailedPut(e, "upload-put-checksum-mismatch", 422, "CHECKSUM_MISMATCH"),
        ["upload-put-expired"] = e => FailedPut(e, "upload-put-expired", 410, "UPLOAD_EXPIRED"),
        ["upload-put-html"] = async e =>
        {
            e.Handler.Expect(HtmlUpload);
            var ticket = await e.Client.Uploads.UploadAsync(HtmlBytes(), UploadContentTypes.Html, "page.html");
            Assert.Equal(Guid.Parse("22222222-2222-4222-8222-222222222222"), ticket.UploadId);
        },
        ["upload-put-image-png"] = async e =>
        {
            e.Handler.Expect("upload-ticket-image-png", "upload-put-image-png");
            var ticket = await e.Client.Documents.UploadImageAsync(Env.Stream(GoldenHandler.RequestBytes("upload-put-image-png")), UploadContentTypes.Png, "logo.png");
            Assert.Equal(Image, ticket.UploadId);
            Assert.Null(ticket.MaxBytes);
        },
        ["upload-put-pdf"] = async e =>
        {
            e.Handler.Expect(PdfUpload);
            var ticket = await e.Client.Uploads.UploadAsync(PdfBytes(), UploadContentTypes.Pdf, "sample.pdf");
            Assert.Equal(26214400, ticket.MaxBytes);
            Assert.Equal("PUT", ticket.Method);
        },
        ["upload-put-signature-invalid"] = e => FailedPut(e, "upload-put-signature-invalid", 403, "SIGNATURE_INVALID"),
        ["upload-ticket-html"] = async e =>
        {
            e.Handler.Expect(HtmlUpload);
            await e.Client.Uploads.UploadAsync(HtmlBytes(), UploadContentTypes.Html, "page.html");
        },
        ["upload-ticket-image-png"] = async e =>
        {
            e.Handler.Expect("upload-ticket-image-png", "upload-put-image-png");
            await e.Client.Uploads.UploadAsync(Env.Stream(GoldenHandler.RequestBytes("upload-put-image-png")), UploadContentTypes.Png, "logo.png");
        },
        ["upload-ticket-pdf"] = async e =>
        {
            e.Handler.Expect(PdfUpload);
            await e.Client.Uploads.UploadAsync(PdfBytes(), UploadContentTypes.Pdf, "sample.pdf");
        },
        ["upload-ticket-session-limit"] = async e =>
        {
            e.Handler.Expect("upload-ticket-session-limit");
            await Fails<RateLimitException>(() => e.Client.Uploads.UploadAsync(PdfBytes(), UploadContentTypes.Pdf, "sample.pdf"), 429, "SESSION_UPLOAD_LIMIT");
        },
        ["upload-ticket-session"] = async e =>
        {
            e.Handler.Expect("upload-ticket-session", "upload-put-pdf");
            await e.Client.Uploads.UploadAsync(PdfBytes(), UploadContentTypes.Pdf, "sample.pdf");
        },
        ["upload-ticket-too-large"] = async e =>
        {
            var error = await Assert.ThrowsAsync<ValidationException>(() => ReplayRaw(e, "upload-ticket-too-large", "k"));
            Assert.Equal((422, "FILE_TOO_LARGE"), (error.Status, error.Code));
        },
        ["web-job-accepted"] = async e =>
        {
            e.Handler.Expect("web-job-accepted");
            var job = await e.Client.Web.CreateScanAsync(new Uri("https://example.com/page"));
            Assert.Equal(JobKinds.Web, job.Kind);
            Assert.DoesNotContain('?', job.TargetUrl ?? "");
        },
        ["web-job-host-not-allowed"] = async e =>
        {
            e.Handler.Expect("web-job-host-not-allowed");
            await Fails<HostNotAllowedException>(() => e.Client.Web.CreateScanAsync(new Uri("https://example.com/page")), 421, "HOST_NOT_ALLOWED");
        },
        ["web-job-url-rejected"] = async e =>
        {
            e.Handler.Expect("web-job-url-rejected");
            await Fails<ValidationException>(() => e.Client.Web.CreateScanAsync(new Uri("https://internal.invalid/")), 422, "VALIDATION_ERROR");
        },
    };

    private static ReadingOrderRemediation Approval(string target = RemediationTargets.PdfTags, string? reviewer = null) => new()
    {
        BaseArtifactSha256 = "fe9a7d44f4318fec2635607dcacc6755fafe3b997406abe640db9cec5f28ab08",
        SnapshotVersion = 1,
        Target = target,
        OrderedNodeIds = ["n2b3c4d5e6f708190", "n1a2b3c4d5e6f7089"],
        Acknowledged = true,
        Reviewer = reviewer,
    };

    private static async Task FailedPut(Env e, string fixture, int status, string code)
    {
        e.Handler.Expect("upload-ticket-pdf", fixture);
        var error = await Assert.ThrowsAsync<StorageException>(() => e.Client.Uploads.UploadAsync(PdfBytes(), UploadContentTypes.Pdf, "sample.pdf"));
        Assert.Equal((status, code), (error.Status, error.Code));
    }
}
