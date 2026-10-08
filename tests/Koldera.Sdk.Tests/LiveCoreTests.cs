using System.Security.Cryptography;
using System.Text;
using Xunit.Abstractions;

namespace Koldera.Tests;

/// <summary>Skipped unless KOLDERA_URL and KOLDERA_TOKEN point at a running koldera-core.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KOLDERA_URL")) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KOLDERA_TOKEN")))
        {
            Skip = "set KOLDERA_URL and KOLDERA_TOKEN to run against a real koldera-core";
        }
    }
}

// Run: KOLDERA_URL=http://localhost:8080 KOLDERA_TOKEN=... dotnet test --filter FullyQualifiedName~LiveCoreTests
public sealed class LiveCoreTests(ITestOutputHelper output)
{
    private static readonly CreateOptions Wait = new() { Wait = 120, UntilFinished = true };

    [LiveFact]
    public async Task HealthAndInfo()
    {
        using var client = new KolderaClient(new KolderaClientOptions());
        Assert.Equal("ok", (await client.HealthAsync()).Status);
        Assert.Contains("pdf", (await client.InfoAsync()).Features);
    }

    [LiveFact]
    public async Task AnalyzeAndFixSyntheticPdf()
    {
        using var client = new KolderaClient(new KolderaClientOptions());
        var pdf = await File.ReadAllBytesAsync(Path.Combine(RepoRoot(), "examples", "PdfRemediationDemo", "synthetic-report.pdf"));

        var analyzed = await client.Pdf.CreateJobAsync(new MemoryStream(pdf), PdfJobModes.Analyze, new PdfJobOptions { Wait = 60, UntilFinished = true });
        Assert.Equal(JobStates.Completed, analyzed.State);
        Assert.True(analyzed.ManualReviewRequired);
        Assert.NotEmpty(await client.Jobs.ListFindingsAsync(analyzed.Id));

        var fixedJob = await client.Pdf.CreateJobAsync(new MemoryStream(pdf), PdfJobModes.Fix, new PdfJobOptions { Wait = 120, UntilFinished = true, ExplicitLanguage = "en" });
        Assert.True(fixedJob.IsFinished);
        Assert.True(fixedJob.ManualReviewRequired);
        var artifact = (await client.Jobs.ListArtifactsAsync(fixedJob.Id)).FirstOrDefault(a => a.Kind == ArtifactKinds.Remediated);
        if (artifact is not null)
        {
            Assert.Equal(artifact.Sha256, Sha256(await client.Artifacts.DownloadAsync(artifact)));
        }

        output.WriteLine($"analyze {analyzed.Id} {analyzed.Validation?.Status}; fix {fixedJob.Id} {fixedJob.State} {fixedJob.RemediationStatus} final {fixedJob.FinalValidation?.Status}; remediated sha {artifact?.Sha256}");
    }

    [LiveFact]
    public async Task HtmlToTaggedPdf()
    {
        using var client = new KolderaClient(new KolderaClientOptions());
        var html = "<!doctype html><html lang=\"en\"><head><title>Synthetic page</title></head><body><h1>Synthetic page</h1><p>Invented text for a live test.</p></body></html>";
        var job = await client.HtmlToPdf.CreateJobAsync(new MemoryStream(Encoding.UTF8.GetBytes(html)), HtmlToPdfJobModes.GenerateAnalyze, new HtmlToPdfJobOptions { Wait = 120, UntilFinished = true });
        Assert.Equal(JobStates.Completed, job.State);
        var pdf = Assert.Single(await client.Jobs.ListArtifactsAsync(job.Id), a => a.Kind == ArtifactKinds.GeneratedPdf);
        Assert.Equal(pdf.Sha256, Sha256(await client.Artifacts.DownloadAsync(pdf)));
        output.WriteLine($"html {job.Id} {job.Validation?.Status}");
    }

    [LiveFact]
    public async Task StructuredDocumentToTaggedPdf()
    {
        using var client = new KolderaClient(new KolderaClientOptions());
        var document = new StructuredDocument("Synthetic note", "en", [new HeadingBlock(1, "Synthetic note"), new ParagraphBlock("Invented text for a live test.")]);
        var job = await client.Documents.CreateJobAsync(document, Wait);
        Assert.Equal(JobStates.Completed, job.State);
        var pdf = Assert.Single(await client.Jobs.ListArtifactsAsync(job.Id), a => a.Kind == ArtifactKinds.GeneratedPdf);
        Assert.Equal(pdf.Sha256, Sha256(await client.Artifacts.DownloadAsync(pdf)));
        output.WriteLine($"document {job.Id} {job.Validation?.Status}");
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koldera.Sdk.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
