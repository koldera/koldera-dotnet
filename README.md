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

Full documentation follows with the first release.
