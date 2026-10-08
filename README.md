# Koldera.Sdk

.NET SDK for koldera-core, the commercially licensed, self-hosted service that checks PDFs against
the PDF/UA-1 machine rules, writes fixed copies, renders HTML and structured documents to tagged PDF, and scans web
pages. `KolderaClient` is async, nullable-annotated, trimming and AOT compatible, and every resource is an interface
(`IKolderaClient`, `IJobs`, `IPdfJobs`, ...) so you can fake it in your own tests. It targets .NET 8 and .NET 10.

Everything koldera-core reports is a **machine check**. A person still has to review the result:
`job.ManualReviewRequired` is always `true`, and this SDK never turns a passing machine check into a statement that a
document is accessible.

Status: early release (0.1). The API may change before 1.0.

## Install

```sh
dotnet add package Koldera.Sdk
```

The API reference ships as XML documentation inside the package, so IntelliSense shows it in your editor. There is
also a short [guide](docs/guide.md).

## Quick start (console)

Point the client at your koldera-core instance. `KOLDERA_URL` and `KOLDERA_TOKEN` are read from the environment when
`BaseUrl` and `Token` are not set.

```csharp
using Koldera;

using var client = new KolderaClient(new KolderaClientOptions
{
    BaseUrl = new Uri("https://koldera.example.com"),
    Token = Environment.GetEnvironmentVariable("KOLDERA_TOKEN"),
});

await using var pdf = File.OpenRead("report.pdf");
var job = await client.Pdf.CreateJobAsync(pdf, PdfJobModes.Fix, new PdfJobOptions { Wait = 60, UntilFinished = true });
Console.WriteLine($"{job.State} {job.FinalValidation?.Status} review required: {job.ManualReviewRequired}");
```

## Quick start (ASP.NET Core)

`AddKoldera` registers `IKolderaClient` as a typed client of `IHttpClientFactory` and validates the options on start.
It returns the `IHttpClientBuilder`, so you can add your own handlers.

```csharp
using Koldera;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKoldera(builder.Configuration.GetSection("Koldera"));

var app = builder.Build();
app.MapPost("/reports/check", async (IFormFile file, IKolderaClient koldera, CancellationToken cancellationToken) =>
{
    await using var stream = file.OpenReadStream();
    var job = await koldera.Pdf.CreateJobAsync(stream, PdfJobModes.Analyze, new PdfJobOptions { Wait = 30, UntilFinished = true }, cancellationToken);
    return Results.Ok(new { job.Id, job.State, job.ManualReviewRequired });
});
app.Run();
```

```json
{
  "Koldera": {
    "BaseUrl": "https://koldera.example.com",
    "Token": "set this through user secrets or the environment, not in the file",
    "Timeout": "00:00:30",
    "StorageTimeout": "00:10:00"
  }
}
```

To configure in code instead, use `services.AddKoldera(options => { options.BaseUrl = ...; })`. The factory client gets
an infinite `HttpClient.Timeout` because the SDK applies its own timeouts, see [Timeouts](#timeouts).

## Check or fix a PDF, read the findings, download the copy

`mode` is `PdfJobModes.Analyze` (validate only) or `PdfJobModes.Fix` (validate, write a separate repaired copy,
validate again). Your original file is never modified. For HTML the modes are `HtmlToPdfJobModes.GenerateAnalyze` and
`GenerateFix` (`client.HtmlToPdf.CreateJobAsync`).

`Wait` (1 to 900 seconds) is sent as `Prefer: wait=N`. When the job is still running after the wait, the call returns
it in its real state (`job.IsFinished` is `false`); set `UntilFinished` to keep polling, or call
`client.Jobs.WaitAsync(job.Id)` yourself. A failed job is returned, not thrown: read `job.Error`.

```csharp
using Koldera;

async Task FixAsync(IKolderaClient client, CancellationToken cancellationToken)
{
    await using var pdf = File.OpenRead("report.pdf");
    var job = await client.Pdf.CreateJobAsync(
        pdf,
        PdfJobModes.Fix,
        new PdfJobOptions { Wait = 60, UntilFinished = true, ExplicitLanguage = "en" },
        cancellationToken);

    if (job.State == JobStates.Failed)
    {
        Console.WriteLine($"failed: {job.Error?.Code} {job.Error?.Message}");
        return;
    }

    // Findings are machine checks; some are flagged for a person.
    foreach (var finding in await client.Jobs.ListFindingsAsync(job.Id, cancellationToken))
    {
        Console.WriteLine($"{finding.RuleId} {finding.Message} review: {finding.NeedsHumanReview}");
    }

    // The fixed copy. The download verifies size and SHA-256 before a single byte is written to the target.
    foreach (var artifact in await client.Jobs.ListArtifactsAsync(job.Id, cancellationToken))
    {
        if (artifact.Kind == ArtifactKinds.Remediated)
        {
            await client.Artifacts.DownloadToFileAsync(artifact, "report.fixed.pdf", cancellationToken);
        }
    }
}
```

`DownloadToFileAsync` writes a temporary file beside the target, verifies it, then moves it into place without
overwriting an existing file. `DownloadAsync(artifact)` returns the bytes and `DownloadAsync(artifact, stream)` writes
to a stream, both only after size and hash matched; a mismatch throws `StorageException`. Download addresses expire
after ten minutes, so list the artifacts shortly before you download them.

Other calls: `client.Web.CreateScanAsync(uri)`, `client.Documents.CreateJobAsync(document)`,
`client.Jobs.GetAsync(id)`, `ListActionsAsync`, `GetReadingOrderAsync`, `RemediateReadingOrderAsync`,
`client.InfoAsync()` and `client.HealthAsync()`.

## Client sessions for browsers

A browser never gets your server token. Your backend authenticates the user, then mints a short-lived session for one
origin with limited scopes and budgets, and hands only `SessionToken` to the page (for example to
[koldera-js](https://github.com/koldera/koldera-js)):

```csharp
using Koldera;

async Task<object> MintSessionForSignedInUserAsync(IKolderaClient client, CancellationToken cancellationToken)
{
    // Authenticate your user here before minting anything.
    var session = await client.ClientSessions.CreateAsync(new ClientSessionRequest
    {
        AllowedOrigin = "https://app.example",
        Scopes = [SessionScopes.JobsCreate, SessionScopes.JobsRead],
        MaxJobs = 5,
        TtlSeconds = 600,
    }, cancellationToken);
    return new { sessionToken = session.SessionToken, expiresAt = session.ExpiresAt };
}
```

Origin and CORS checks on the server are a second layer, not a secret.

## Structured documents

A structured document declares semantic blocks with typed records. Structure is declared by you, never inferred.
Images are uploaded first and referenced by upload id, with either alternative text or an explicit decoration.

```csharp
using Koldera;

async Task InvoiceAsync(IKolderaClient client)
{
    await using var logoFile = File.OpenRead("logo.png");
    var logo = await client.Documents.UploadImageAsync(logoFile, UploadContentTypes.Png, "logo.png");

    var document = new StructuredDocument("Invoice 2026-0042", "en",
    [
        new HeadingBlock(1, "Invoice 2026-0042"),
        ImageBlock.Described(logo.UploadId, "Company logo: a blue square with the letters EC"),
        new ParagraphBlock(["Thank you for your ", new StrongInline("prompt"), " payment."]),
        new TableBlock(
            "Items billed",
            [["Description", "Amount"]],
            [["Consulting, 10 hours", "1000.00"]])
        {
            ColumnAlign = ["left", "right"],
        },
    ])
    {
        Page = new DocumentPage { Size = "A4", Margins = "20mm" },
        Footer = [new PageNumberBlock()],
    };

    var job = await client.Documents.CreateJobAsync(document, new CreateOptions { Wait = 60, UntilFinished = true });
    foreach (var artifact in await client.Jobs.ListArtifactsAsync(job.Id))
    {
        if (artifact.Kind == ArtifactKinds.GeneratedPdf)
        {
            await client.Artifacts.DownloadToFileAsync(artifact, "invoice.pdf");
        }
    }
}
```

A rejected document throws `ValidationException` whose `Errors` list `Path`, `Code` and `Message` for each problem. For
features the typed model does not cover yet, pass a `JsonObject` to `CreateJobAsync`. The block rules are in
koldera-core's documents guide.

## Errors

Every non-2xx answer becomes a subclass of `ApiException` with the stable problem `Code`, `Status` and, when the
server sent one, `RetryAfter`. Unknown codes, states and kinds from a newer koldera-core do not crash the client: the
values are plain strings, and `Jobs.WaitAsync` treats any state other than queued or running as final.

```csharp
using Koldera;

async Task RunAsync(IKolderaClient client)
{
    try
    {
        await using var pdf = File.OpenRead("report.pdf");
        await client.Pdf.CreateJobAsync(pdf, PdfJobModes.Fix, new PdfJobOptions { Wait = 60, UntilFinished = true });
    }
    catch (Koldera.ValidationException error)
    {
        foreach (var problem in error.Errors)
        {
            Console.WriteLine($"{problem.Path} {problem.Code} {problem.Message}");
        }
    }
    catch (RateLimitException error)
    {
        Console.WriteLine($"retry in {error.RetryAfter}");
    }
    catch (ApiException error)
    {
        Console.WriteLine($"{error.Status} {error.Code} {error.Message}");
    }
    catch (JobTimeoutException error)
    {
        Console.WriteLine($"still running: {error.JobId}");
    }
    catch (StorageException error)
    {
        Console.WriteLine($"upload or download failed: {error.Message}");
    }
    catch (HttpRequestException error)
    {
        Console.WriteLine($"network failure: {error.Message}");
    }
}
```

The classes are `AuthenticationException` (401), `PermissionException` (403), `NotFoundException` (404),
`ConflictException` (409), `ValidationException` (400, 422), `UnsupportedMediaTypeException` (415),
`HostNotAllowedException` (421), `RateLimitException` (429), `ServiceUnavailableException` (503, a
`ServerException`), `ServerException` (other 5xx), plus `ConfigurationException` (the SDK was used wrongly),
`JobTimeoutException` and `StorageException`. Network failures surface as `HttpRequestException`; an SDK timeout
throws `TimeoutException`. Error messages never contain a credential.

Creation calls carry an `Idempotency-Key` (generated, or set `IdempotencyKey`). A network failure on a creation call is
repeated up to twice with the same key, so a lost response does not create a second job.

### Name collisions

The exception types keep the names of the API error classes, so `ValidationException` and `AuthenticationException`
can collide with `System.ComponentModel.DataAnnotations` and `System.Security.Authentication` when both namespaces are
imported. Use an alias:

```csharp
using KolderaAuthentication = Koldera.AuthenticationException;
using KolderaValidation = Koldera.ValidationException;
```

## Configuration

| Option | Meaning |
| --- | --- |
| `BaseUrl` | origin of koldera-core; env `KOLDERA_URL` |
| `Token` | server token, any non-empty string the server accepts; env `KOLDERA_TOKEN`; never printed |
| `Timeout` | per-request timeout for API calls (default 30 s); a `Wait` stretches it to cover the wait plus 10 s |
| `StorageTimeout` | timeout of one signed upload or download (default 10 minutes) |
| `UserAgentSuffix` | product token appended to the SDK's User-Agent |

Per call on job creation (`CreateOptions` and its subclasses): `Wait`, `UntilFinished`, `Poll` (`WaitOptions`),
`IdempotencyKey` and `OnResponse` (status, `Location`, `Preference-Applied`, `Idempotent-Replayed`). `PdfJobOptions`
and `HtmlToPdfJobOptions` add `FileName`, `ExplicitLanguage` and `ExplicitTitle`.

## Timeouts

- API calls use `Timeout`. A call with `Wait = N` is allowed `N + 10` seconds.
- Signed uploads and downloads use `StorageTimeout` and a separate client owned by the SDK, so they never receive an
  `Authorization` header or your handlers. Your `HttpClient` is never modified. With `AddKoldera` every resolved
  client shares one storage client, which the service provider disposes.
- `AddKoldera` sets the typed client's `HttpClient.Timeout` to infinite, so the SDK's timeouts are the only ones.
- If you pass your own `HttpClient` to the constructor, give it an infinite `Timeout` as well. Otherwise a call that
  needs longer than the client's own timeout (100 s by default) throws `ConfigurationException` before anything is
  sent.

```csharp
using Koldera;

var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
using var client = new KolderaClient(
    new KolderaClientOptions
    {
        BaseUrl = new Uri("https://koldera.example.com"),
        Token = Environment.GetEnvironmentVariable("KOLDERA_TOKEN"),
        Timeout = TimeSpan.FromSeconds(20),
        StorageTimeout = TimeSpan.FromMinutes(5),
    },
    http);
```

## Compatibility

| SDK | koldera-core | .NET |
| --- | --- | --- |
| 0.1.x | 0.1.x | 8.0, 10.0 |

The vendored API description in [`contracts/`](contracts) records the koldera-core commit it was taken from
(`contracts/CORE_VERSION`) and the sdk-contract-kit commit it was synced from (`contracts/KIT_VERSION`), and CI checks
that the copy matches. Run koldera-core as described in its
README and deployment guide. A package icon is planned for
when a brand asset exists; the package has none yet.

## Examples

[examples/PdfRemediationDemo](examples/PdfRemediationDemo) is a runnable console app against a koldera-core you start
locally, with a synthetic input: analyze a PDF, write a fixed copy, download it, list what needs a person. It is not
part of the NuGet package.

## Links

- [Guide](docs/guide.md), [contributing](CONTRIBUTING.md), [security policy](SECURITY.md), [changelog](CHANGELOG.md).

## Licence

This SDK is licensed under the [Apache License 2.0](LICENSE).

koldera-core is commercially licensed, self-hosted software; this SDK is Apache-2.0 and talks to it over its HTTP API.
