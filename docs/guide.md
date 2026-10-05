# Koldera.Sdk guide

A short walk through the SDK. The [README](../README.md) has the full set of examples and the option tables; the
reference for every type is the XML documentation that ships in the package. Every C# block on this page is compiled
by CI, so the code matches the API.

## Getting started

Install the package and create a client. The server token comes from your koldera-core instance; keep it on the
server.

```sh
dotnet add package Koldera.Sdk
```

```csharp
using Koldera;

using var client = new KolderaClient(new KolderaClientOptions
{
    BaseUrl = new Uri("https://koldera.example.com"),
    Token = Environment.GetEnvironmentVariable("KOLDERA_TOKEN"),
});

var info = await client.InfoAsync();
Console.WriteLine($"koldera-core {info.Version}, features: {string.Join(", ", info.Features)}");
```

Without options the client reads `KOLDERA_URL` and `KOLDERA_TOKEN`. A job comes back in the state the server reports:
`Wait` holds the request for up to 900 seconds and `UntilFinished` keeps polling when the job is still running. A
finished job is a machine check, never a statement that a document is accessible: `ManualReviewRequired` stays `true`.

## Dependency injection

Register the client once and depend on `IKolderaClient`. Options are validated when the host starts, and the returned
`IHttpClientBuilder` lets you add your own message handlers.

```csharp
using Koldera;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddKoldera(options =>
{
    options.BaseUrl = new Uri("https://koldera.example.com");
    options.Token = Environment.GetEnvironmentVariable("KOLDERA_TOKEN");
    options.Timeout = TimeSpan.FromSeconds(30);
});

using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IKolderaClient>();
var health = await client.HealthAsync();
Console.WriteLine(health.Status);
```

To read the settings from configuration, pass a section: `services.AddKoldera(configuration.GetSection("Koldera"))`
with the keys `BaseUrl`, `Token`, `Timeout`, `StorageTimeout` and `UserAgentSuffix`. Resources are interfaces, so a
unit test can replace `IPdfJobs` or `IJobs` with a fake without any HTTP.

## Browser sessions

Browser code never receives the server token. Your backend authenticates the user and mints a short-lived session for
exactly one origin; the browser gets only the session token.

```csharp
using Koldera;

var session = await client.ClientSessions.CreateAsync(new ClientSessionRequest
{
    AllowedOrigin = "https://app.example",
    Scopes = [SessionScopes.JobsCreate, SessionScopes.JobsRead],
    MaxJobs = 3,
    MaxUploads = 3,
    TtlSeconds = 300,
});
Console.WriteLine($"hand {session.ExpiresAt:O} and the token to the page");
```

The origin is checked locally before the request is sent. Server-side origin and CORS checks are a second layer, not a
secret.

## Documents

A structured document is built from typed blocks. You declare the structure; nothing is inferred from appearance. An
image block needs either alternative text (`ImageBlock.Described`) or an explicit decoration
(`ImageBlock.AsDecoration`).

```csharp
using Koldera;

var document = new StructuredDocument("Meeting notes", "en",
[
    new HeadingBlock(1, "Meeting notes"),
    new ListBlock([new ListItem("Budget approved"), new ListItem("Next review in June")]) { Ordered = false },
    new KeyValueBlock([new KeyValueRow("Owner", "Facilities")]),
]);

var job = await client.Documents.CreateJobAsync(document, new CreateOptions { Wait = 60, UntilFinished = true });
Console.WriteLine($"{job.Id} {job.State}");
```

When the server rejects a document, the `ValidationException` lists every problem with its path in the document.

## Errors

Non-2xx answers become `ApiException` subclasses with the stable problem `Code`. Handle the cases you can act on and
let the rest bubble up.

```csharp
using Koldera;

try
{
    var job = await client.Jobs.GetAsync(Guid.NewGuid());
    Console.WriteLine(job.State);
}
catch (NotFoundException error)
{
    Console.WriteLine($"no such job: {error.Code}");
}
catch (RateLimitException error)
{
    await Task.Delay(error.RetryAfter ?? TimeSpan.FromSeconds(5));
}
```

Retries are built in only where they are safe: creation calls repeat with the same idempotency key after a network
error, and reads wait for `Retry-After` on 429 and 503. Cancelling through the `CancellationToken` is never retried.
`ValidationException` and `AuthenticationException` share names with types in `System`; the README shows an alias.
