using System.Text.Json.Nodes;

namespace Koldera;

/// <summary>The client of a koldera-core server; register it with <c>services.AddKoldera(...)</c> and depend on this interface.</summary>
public interface IKolderaClient
{
    /// <summary>Upload tickets (the job creators use them internally; public for advanced flows).</summary>
    IUploads Uploads { get; }

    /// <summary>Job reads, polling, findings, artifacts and reading order.</summary>
    IJobs Jobs { get; }

    /// <summary>PDF jobs.</summary>
    IPdfJobs Pdf { get; }

    /// <summary>Static HTML to tagged PDF jobs.</summary>
    IHtmlToPdfJobs HtmlToPdf { get; }

    /// <summary>Web page scans.</summary>
    IWebJobs Web { get; }

    /// <summary>Structured documents to tagged PDF.</summary>
    IDocumentJobs Documents { get; }

    /// <summary>Verified artifact downloads.</summary>
    IArtifacts Artifacts { get; }

    /// <summary>Browser session minting; server side only.</summary>
    IClientSessions ClientSessions { get; }

    /// <summary>Version and enabled features of the server (GET /v1/info).</summary>
    Task<Info> InfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Liveness probe (GET /healthz); sends no credential.</summary>
    Task<Health> HealthAsync(CancellationToken cancellationToken = default);
}

/// <summary>Upload tickets: issue one and PUT the bytes to storage without any token.</summary>
public interface IUploads
{
    /// <summary>
    /// Reads the content fully (a non-seekable stream is buffered), hashes it, issues a ticket and uploads it.
    /// The content type must be one of <see cref="UploadContentTypes"/>. Returns the consumed ticket.
    /// </summary>
    Task<UploadTicket> UploadAsync(Stream content, string contentType, string fileName, string? idempotencyKey = null, CancellationToken cancellationToken = default);
}

/// <summary>PDF jobs.</summary>
public interface IPdfJobs
{
    /// <summary>Upload a PDF and queue <see cref="PdfJobModes.Analyze"/> or <see cref="PdfJobModes.Fix"/>.</summary>
    Task<Job> CreateJobAsync(Stream pdf, string mode, PdfJobOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Static HTML to tagged PDF jobs. One self-contained HTML file; never a URL.</summary>
public interface IHtmlToPdfJobs
{
    /// <summary>Upload one HTML file and queue the render, inspect and validate pipeline.</summary>
    Task<Job> CreateJobAsync(Stream html, string mode, HtmlToPdfJobOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Web page scans.</summary>
public interface IWebJobs
{
    /// <summary>Queue one automated scan of a public http(s) address.</summary>
    Task<Job> CreateScanAsync(Uri url, CreateOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Structured documents to tagged PDF.</summary>
public interface IDocumentJobs
{
    /// <summary>Queue a structured document for rendering to a tagged PDF. Images reference the upload id of <see cref="UploadImageAsync"/>.</summary>
    Task<Job> CreateJobAsync(StructuredDocument document, CreateOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Queue a document given as raw JSON (the body of POST /v1/documents/jobs), for features this SDK does not model yet.</summary>
    Task<Job> CreateJobAsync(JsonObject document, CreateOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Upload one PNG, JPEG or SVG image for use in an image block. The content type is one of <see cref="UploadContentTypes"/>.</summary>
    Task<UploadTicket> UploadImageAsync(Stream image, string contentType, string? fileName = null, string? idempotencyKey = null, CancellationToken cancellationToken = default);
}

/// <summary>Job reads, polling and reading order.</summary>
public interface IJobs
{
    /// <summary>Current state of a job; retried at most twice on 429 and 503 after Retry-After.</summary>
    Task<Job> GetAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Poll until the job is no longer queued or running and return it. A failed job is returned, not thrown:
    /// read <see cref="Job.Error"/>. Honours Retry-After, otherwise backs off from <see cref="WaitOptions.InitialDelay"/> up to 10 s.
    /// </summary>
    Task<Job> WaitAsync(Guid jobId, WaitOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Findings of a job; machine checks and human-review items are mixed and flagged.</summary>
    Task<IReadOnlyList<Finding>> ListFindingsAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>What each repair rule did on a fix job.</summary>
    Task<IReadOnlyList<RemediationAction>> ListActionsAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Files of the job with fresh signed download addresses; they expire after ten minutes.</summary>
    Task<IReadOnlyList<Artifact>> ListArtifactsAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Reading-order evidence bound to the artifact hash; <see cref="ReadingOrderSnapshot.ReviewRequired"/> is always true.</summary>
    Task<ReadingOrderSnapshot> GetReadingOrderAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queue a correction a person approved. <see cref="ReadingOrderRemediation.Acknowledged"/> must be true.
    /// 409 means the snapshot is stale (load it and review again); 422 means the plan cannot be applied safely.
    /// </summary>
    Task<Job> RemediateReadingOrderAsync(Guid jobId, ReadingOrderRemediation approval, CreateOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Artifact downloads through signed addresses, size and SHA-256 verified.</summary>
public interface IArtifacts
{
    /// <summary>Downloads to memory. Throws <see cref="StorageException"/> and returns nothing when the size or the hash differ.</summary>
    Task<byte[]> DownloadAsync(Artifact artifact, CancellationToken cancellationToken = default);

    /// <summary>Downloads into the destination, writing only after size and SHA-256 matched.</summary>
    Task DownloadAsync(Artifact artifact, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads to a file: written to a temporary file next to the target, verified, then moved without
    /// overwriting an existing file. The temporary file is removed on any failure.
    /// </summary>
    Task DownloadToFileAsync(Artifact artifact, string path, CancellationToken cancellationToken = default);
}

/// <summary>Short-lived sessions for browser code.</summary>
public interface IClientSessions
{
    /// <summary>Mint a scoped session for an end user you authenticated. Call it from your backend only.</summary>
    Task<ClientSession> CreateAsync(ClientSessionRequest request, CancellationToken cancellationToken = default);
}
