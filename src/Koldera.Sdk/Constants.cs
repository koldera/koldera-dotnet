namespace Koldera;

/// <summary>Job kinds. The API may send kinds this SDK does not know; they are preserved as strings.</summary>
public static class JobKinds
{
    /// <summary>PDF analysis or fix.</summary>
    public const string Pdf = "pdf";

    /// <summary>HTML to tagged PDF.</summary>
    public const string HtmlToPdf = "html_to_pdf";

    /// <summary>Structured document to tagged PDF.</summary>
    public const string DocumentToPdf = "document_to_pdf";

    /// <summary>Web page scan.</summary>
    public const string Web = "web";

    /// <summary>Reading-order correction of a PDF.</summary>
    public const string OrderPdf = "order_pdf";

    /// <summary>Reading-order correction of HTML.</summary>
    public const string OrderHtml = "order_html";
}

/// <summary>Execution state of a job. Separate from machine validation and from manual review.</summary>
public static class JobStates
{
    /// <summary>Accepted, waiting for a worker.</summary>
    public const string Queued = "queued";

    /// <summary>A worker is processing it.</summary>
    public const string Running = "running";

    /// <summary>Finished; results and artifacts are available.</summary>
    public const string Completed = "completed";

    /// <summary>Finished with an error (see <see cref="Job.Error"/>).</summary>
    public const string Failed = "failed";

    /// <summary>True for every state except queued and running; an unknown state of a newer core counts as final.</summary>
    public static bool IsFinished(string? state) => state is not (Queued or Running);
}

/// <summary>PDF job modes (the profile is always ua1).</summary>
public static class PdfJobModes
{
    /// <summary>Validate only; nothing is written.</summary>
    public const string Analyze = "analyze";

    /// <summary>Validate and write a repaired copy as a remediated artifact; the original is never modified.</summary>
    public const string Fix = "fix";
}

/// <summary>HTML-to-PDF job modes.</summary>
public static class HtmlToPdfJobModes
{
    /// <summary>Render a tagged PDF, inspect its tags and validate.</summary>
    public const string GenerateAnalyze = "generate_analyze";

    /// <summary>As analyze, and also write a repaired copy.</summary>
    public const string GenerateFix = "generate_fix";
}

/// <summary>Reading-order correction targets.</summary>
public static class RemediationTargets
{
    /// <summary>Reorder siblings in a copy of the uploaded static HTML, then render and validate again.</summary>
    public const string SourceHtml = "source_html";

    /// <summary>Reorder the structure-element children of one parent in a copy of the tagged PDF.</summary>
    public const string PdfTags = "pdf_tags";
}

/// <summary>Scopes a client session may carry.</summary>
public static class SessionScopes
{
    /// <summary>Upload files and create jobs.</summary>
    public const string JobsCreate = "jobs:create";

    /// <summary>Read job status, findings and artifacts.</summary>
    public const string JobsRead = "jobs:read";
}

/// <summary>Content types of uploads.</summary>
public static class UploadContentTypes
{
    /// <summary>A PDF document.</summary>
    public const string Pdf = "application/pdf";

    /// <summary>A static HTML file.</summary>
    public const string Html = "text/html";

    /// <summary>A PNG image for a structured document.</summary>
    public const string Png = "image/png";

    /// <summary>A JPEG image for a structured document.</summary>
    public const string Jpeg = "image/jpeg";

    /// <summary>An SVG image for a structured document.</summary>
    public const string Svg = "image/svg+xml";

    internal static readonly string[] Images = [Png, Jpeg, Svg];
    internal static readonly string[] All = [Pdf, Html, Png, Jpeg, Svg];
}

/// <summary>Artifact kinds.</summary>
public static class ArtifactKinds
{
    /// <summary>The uploaded file.</summary>
    public const string Original = "original";

    /// <summary>The repaired copy of a fix job.</summary>
    public const string Remediated = "remediated";

    /// <summary>Validation report of the input.</summary>
    public const string BaselineReport = "baseline_report";

    /// <summary>Validation report of the result.</summary>
    public const string FinalReport = "final_report";

    /// <summary>The PDF rendered from HTML or a structured document.</summary>
    public const string GeneratedPdf = "generated_pdf";

    /// <summary>The HTML source of a job.</summary>
    public const string SourceHtml = "source_html";

    /// <summary>Report of a web scan.</summary>
    public const string WebReport = "web_report";

    /// <summary>The copy produced by a reading-order correction.</summary>
    public const string Reordered = "reordered";
}

/// <summary>Remediation statuses of a job.</summary>
public static class RemediationStatuses
{
    /// <summary>The job was an analysis only.</summary>
    public const string NotRequested = "not_requested";

    /// <summary>No repair rule applied.</summary>
    public const string NoApplicableFix = "no_applicable_fix";

    /// <summary>A repaired copy was written.</summary>
    public const string Applied = "applied";

    /// <summary>Repairs were skipped.</summary>
    public const string Skipped = "skipped";

    /// <summary>A repaired copy failed a gate and was discarded.</summary>
    public const string RolledBack = "rolled_back";
}

/// <summary>Status of a machine validation.</summary>
public static class ValidationStatuses
{
    /// <summary>No rule failed.</summary>
    public const string Passed = "passed";

    /// <summary>At least one rule failed.</summary>
    public const string Failed = "failed";

    /// <summary>The validator could not run to the end.</summary>
    public const string Error = "error";

    /// <summary>The validation was not run.</summary>
    public const string NotRun = "not_run";

    /// <summary>
    /// The only failing check is the missing PDF/UA identifier (veraPDF 5-1, still listed in the failed rules), which was
    /// not written because declaring PDF/UA requires a human review. It is not a pass.
    /// </summary>
    public const string DeclarationWithheld = "declaration_withheld";
}

/// <summary>Problem codes of the API. An unknown code of a newer core is preserved as sent.</summary>
public static class ProblemCodes
{
    /// <summary>The artifact was removed after its retention time.</summary>
    public const string ArtifactExpired = "ARTIFACT_EXPIRED";

    /// <summary>The uploaded bytes do not match the declared hash.</summary>
    public const string ChecksumMismatch = "CHECKSUM_MISMATCH";

    /// <summary>The idempotency key was used with another request body.</summary>
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";

    /// <summary>The token lacks the scope for this call.</summary>
    public const string InsufficientScope = "INSUFFICIENT_SCOPE";

    /// <summary>The job queue is full; retry after the Retry-After delay.</summary>
    public const string QueueFull = "QUEUE_FULL";

    /// <summary>The client session has used up its job budget.</summary>
    public const string SessionJobLimit = "SESSION_JOB_LIMIT";

    /// <summary>The client session has used up its upload budget.</summary>
    public const string SessionUploadLimit = "SESSION_UPLOAD_LIMIT";

    /// <summary>The reading-order snapshot no longer matches the artifact.</summary>
    public const string StaleReadingOrderSnapshot = "STALE_READING_ORDER_SNAPSHOT";

    /// <summary>The upload was already stored.</summary>
    public const string UploadAlreadyCompleted = "UPLOAD_ALREADY_COMPLETED";

    /// <summary>The request or the input is invalid.</summary>
    public const string ValidationError = "VALIDATION_ERROR";
}
