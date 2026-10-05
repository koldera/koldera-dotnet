# Koldera.Sdk

.NET SDK for [koldera-core](https://koldera-accessibility.dev): PDF/UA checks and fixes, HTML and structured
documents to tagged PDF, and web scans.

```sh
dotnet add package Koldera.Sdk
```

```csharp
using Koldera;

using var client = new KolderaClient(new KolderaClientOptions
{
    BaseUrl = new Uri("https://koldera.example.com"),
    Token = "server-token",
});
await using var pdf = File.OpenRead("report.pdf");
var job = await client.Pdf.CreateJobAsync(pdf, PdfJobModes.Fix, new PdfJobOptions { Wait = 60, UntilFinished = true });
Console.WriteLine($"{job.State} {job.FinalValidation?.Status}");
```

## Name collisions

The exception types keep the names of the API error classes, so `ValidationException` and
`AuthenticationException` can collide with `System.ComponentModel.DataAnnotations` and
`System.Security.Authentication` when both namespaces are imported. Use an alias:

```csharp
using KolderaAuthentication = Koldera.AuthenticationException;
using KolderaValidation = Koldera.ValidationException;
```

## Notes

- Signed upload and download addresses are fetched with a separate client owned by the SDK, so they never receive an `Authorization` header or your handlers; your `HttpClient` is never modified.
- API calls use `Timeout` (default 30 s); signed uploads and downloads use `StorageTimeout` (default 10 minutes).
- A package icon is planned for when a brand asset exists; the package has none yet.

Full documentation follows with the first release.
