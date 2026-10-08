using Koldera;

// PDF remediation demo: analyze a synthetic PDF, fix a copy, download it, and say plainly what still needs a person.
// Needs a running koldera-core: KOLDERA_URL and KOLDERA_TOKEN in the environment (see README.md).
var input = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "synthetic-report.pdf");
var outputDir = args.Length > 1 ? args[1] : "out";

using var client = new KolderaClient(new KolderaClientOptions());
var info = await client.InfoAsync();
Console.WriteLine($"koldera-core {info.Version}, features: {string.Join(", ", info.Features)}");
Console.WriteLine($"Input: {input}");

// 1. Analyze: validate only, nothing is changed.
var pdf = await File.ReadAllBytesAsync(input);
var analyzed = await client.Pdf.CreateJobAsync(new MemoryStream(pdf), PdfJobModes.Analyze, new PdfJobOptions { FileName = Path.GetFileName(input), Wait = 60, UntilFinished = true });
Console.WriteLine();
Console.WriteLine($"== Analysis (job {analyzed.Id}): {analyzed.State}, machine validation {analyzed.Validation?.Status}");
foreach (var rule in analyzed.Validation?.FailedRules ?? [])
{
    Console.WriteLine($"   failed rule {rule.RuleId} x{rule.Count}");
}

// 2. Fix: the server writes a separate repaired copy and validates it again. The input file is never modified.
var job = await client.Pdf.CreateJobAsync(new MemoryStream(pdf), PdfJobModes.Fix, new PdfJobOptions { FileName = Path.GetFileName(input), ExplicitLanguage = "en", Wait = 120, UntilFinished = true });
Console.WriteLine();
Console.WriteLine($"== Fix (job {job.Id}): {job.State}, remediation {job.RemediationStatus}");
if (job.State == JobStates.Failed)
{
    Console.WriteLine($"   job failed: {job.Error?.Code} {job.Error?.Message}");
    return 3;
}

Console.WriteLine($"   before: {job.BaselineValidation?.Status} ({job.BaselineValidation?.FailedRules.Count ?? 0} failed rules)");
Console.WriteLine($"   after:  {job.FinalValidation?.Status} ({string.Join(", ", job.FinalValidation?.FailedRules.Select(r => r.RuleId) ?? [])})");

Console.WriteLine();
Console.WriteLine("== Repairs applied");
foreach (var action in (await client.Jobs.ListActionsAsync(job.Id)).Where(a => a.Outcome == "applied"))
{
    Console.WriteLine($" - {action.RuleId}: {action.Reason}");
}

var findings = await client.Jobs.ListFindingsAsync(job.Id);
Console.WriteLine();
Console.WriteLine("== Machine findings");
foreach (var finding in findings.Where(f => !f.NeedsHumanReview))
{
    Console.WriteLine($" - {finding.RuleId} [{finding.Resolution ?? "n/a"}]");
}

// 3. Download the copy. Size and SHA-256 are verified before the file is moved into place.
var artifact = (await client.Jobs.ListArtifactsAsync(job.Id)).FirstOrDefault(a => a.Kind == ArtifactKinds.Remediated);
if (artifact is not null)
{
    Directory.CreateDirectory(outputDir);
    var target = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(input) + ".fixed.pdf");
    File.Delete(target);
    await client.Artifacts.DownloadToFileAsync(artifact, target);
    Console.WriteLine();
    Console.WriteLine($"== Fixed copy: {target} ({artifact.SizeBytes} bytes, SHA-256 {artifact.Sha256}, verified)");
}
else
{
    Console.WriteLine("No repaired copy was kept (see the remediation status above).");
}

// 4. What automation cannot decide.
Console.WriteLine();
Console.WriteLine($"== Manual review required: {job.ManualReviewRequired}");
foreach (var finding in findings.Where(f => f.NeedsHumanReview))
{
    Console.WriteLine($" - {finding.RuleId}: {finding.Message}");
}

Console.WriteLine();
Console.WriteLine("The fixed copy is NOT declared PDF/UA. koldera-core does not write the PDF/UA-1 identifier (pdfuaid:part=1)");
Console.WriteLine("on its own; it is written only through an explicit opt-in after a person has reviewed the document, which this");
Console.WriteLine("demo does not use. That is why rule 5-1 stays in the final machine check.");
Console.WriteLine("A machine check is not an accessibility statement: no automated run makes a document fully accessible.");
Console.WriteLine("A person still has to check reading order, headings, tables, alternative text, link purpose and language.");
return 0;
