using System.Text.Json.Serialization;

namespace Koldera;

/// <summary>One failing machine rule and how often it fired.</summary>
public sealed record FailedRule
{
    /// <summary>Rule identifier (veraPDF or axe).</summary>
    [JsonPropertyName("ruleId")] public string RuleId { get; init; } = "";

    /// <summary>Occurrences.</summary>
    [JsonPropertyName("count")] public int Count { get; init; }
}

/// <summary>Automated machine validation (veraPDF or axe rules). It is a check, never a verdict on conformance.</summary>
public sealed record MachineValidation
{
    /// <summary>Validation profile.</summary>
    [JsonPropertyName("profile")] public string Profile { get; init; } = "";

    /// <summary>One of <see cref="ValidationStatuses"/>; unknown values are preserved.</summary>
    [JsonPropertyName("status")] public string Status { get; init; } = "";

    /// <summary>Rules that failed.</summary>
    [JsonPropertyName("failedRules")] public IReadOnlyList<FailedRule> FailedRules { get; init; } = [];
}

/// <summary>Why a job failed. These codes are not problem codes.</summary>
public sealed record JobError
{
    /// <summary>For example WORKER_TIMEOUT, WORKER_ERROR, LIMIT_EXCEEDED or DOCUMENT_VALIDATION_FAILED.</summary>
    [JsonPropertyName("code")] public string Code { get; init; } = "";

    /// <summary>Human-readable explanation.</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

/// <summary>A job as the API reports it. <see cref="ManualReviewRequired"/> is always true.</summary>
public sealed record Job
{
    /// <summary>Job id.</summary>
    [JsonPropertyName("id")] public Guid Id { get; init; }

    /// <summary>One of <see cref="JobKinds"/>; unknown values are preserved.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";

    /// <summary>Mode of the job, when it has one.</summary>
    [JsonPropertyName("mode")] public string? Mode { get; init; }

    /// <summary>One of <see cref="JobStates"/>; unknown values are preserved.</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "";

    /// <summary>When the job was accepted.</summary>
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When a worker started it.</summary>
    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When it finished.</summary>
    [JsonPropertyName("finishedAt")] public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>The final validation when there is one, else the baseline.</summary>
    [JsonPropertyName("validation")] public MachineValidation? Validation { get; init; }

    /// <summary>Validation of the input.</summary>
    [JsonPropertyName("baselineValidation")] public MachineValidation? BaselineValidation { get; init; }

    /// <summary>Validation of the repaired copy.</summary>
    [JsonPropertyName("finalValidation")] public MachineValidation? FinalValidation { get; init; }

    /// <summary>One of <see cref="RemediationStatuses"/>; unknown values are preserved.</summary>
    [JsonPropertyName("remediationStatus")] public string? RemediationStatus { get; init; }

    /// <summary>Always true: a person still has to review what the checks cannot decide.</summary>
    [JsonPropertyName("manualReviewRequired")] public bool ManualReviewRequired { get; init; }

    /// <summary>Version of the engine that processed the job.</summary>
    [JsonPropertyName("engineVersion")] public string? EngineVersion { get; init; }

    /// <summary>Why the job failed, when it did.</summary>
    [JsonPropertyName("error")] public JobError? Error { get; init; }

    /// <summary>Scanned address of a web job, without query or fragment.</summary>
    [JsonPropertyName("targetUrl")] public string? TargetUrl { get; init; }

    /// <summary>The content selector of an HTML job.</summary>
    [JsonPropertyName("contentSelector")] public string? ContentSelector { get; init; }

    /// <summary>True once the job is no longer queued or running.</summary>
    [JsonIgnore] public bool IsFinished => JobStates.IsFinished(State);
}

/// <summary>One finding of a job; <see cref="NeedsHumanReview"/> separates automated checks from items a person must decide.</summary>
public sealed record Finding
{
    /// <summary>pdf, html or web; unknown values are preserved.</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = "";

    /// <summary>Rule identifier.</summary>
    [JsonPropertyName("ruleId")] public string RuleId { get; init; } = "";

    /// <summary>Explanation.</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = "";

    /// <summary>Page of a PDF finding, when known.</summary>
    [JsonPropertyName("pageNumber")] public int? PageNumber { get; init; }

    /// <summary>CSS selector or location of an HTML or web finding, when known.</summary>
    [JsonPropertyName("selector")] public string? Selector { get; init; }

    /// <summary>True when software cannot decide it.</summary>
    [JsonPropertyName("needsHumanReview")] public bool NeedsHumanReview { get; init; }

    /// <summary>fixed_in_copy or still_failing for machine checks of an applied fix; otherwise null.</summary>
    [JsonPropertyName("resolution")] public string? Resolution { get; init; }
}

/// <summary>What one repair rule did on a fix job.</summary>
public sealed record RemediationAction
{
    /// <summary>Rule identifier.</summary>
    [JsonPropertyName("ruleId")] public string RuleId { get; init; } = "";

    /// <summary>applied, skipped, failed or rolled_back; unknown values are preserved.</summary>
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "";

    /// <summary>Human-readable explanation.</summary>
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";

    /// <summary>Value before the change, when known.</summary>
    [JsonPropertyName("before")] public string? Before { get; init; }

    /// <summary>Value after the change, when known.</summary>
    [JsonPropertyName("after")] public string? After { get; init; }
}

/// <summary>A downloadable file; <see cref="DownloadUrl"/> is signed and short-lived.</summary>
public sealed record Artifact
{
    /// <summary>Artifact id.</summary>
    [JsonPropertyName("id")] public Guid Id { get; init; }

    /// <summary>One of <see cref="ArtifactKinds"/>; unknown values are preserved.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";

    /// <summary>SHA-256 of the file, lowercase hex.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";

    /// <summary>Size of the file in bytes.</summary>
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; init; }

    /// <summary>Signed download address; no token is sent to it.</summary>
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; init; } = "";

    /// <summary>When the signed address stops working.</summary>
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>A single-use upload ticket bound to size, hash and expiry.</summary>
public sealed record UploadTicket
{
    /// <summary>Pass to a job creation call or an image block.</summary>
    [JsonPropertyName("uploadId")] public Guid UploadId { get; init; }

    /// <summary>Always PUT.</summary>
    [JsonPropertyName("method")] public string Method { get; init; } = "";

    /// <summary>Signed destination; no token is sent to it.</summary>
    [JsonPropertyName("uploadUrl")] public string UploadUrl { get; init; } = "";

    /// <summary>When the ticket stops working.</summary>
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Size limit for this content type, when the server has one.</summary>
    [JsonPropertyName("maxBytes")] public long? MaxBytes { get; init; }
}

/// <summary>Rendered position of a node.</summary>
public sealed record BoundingBox
{
    /// <summary>Left edge.</summary>
    [JsonPropertyName("x")] public double X { get; init; }

    /// <summary>Vertical origin as the engine reports it.</summary>
    [JsonPropertyName("y")] public double Y { get; init; }

    /// <summary>Width.</summary>
    [JsonPropertyName("width")] public double Width { get; init; }

    /// <summary>Height.</summary>
    [JsonPropertyName("height")] public double Height { get; init; }
}

/// <summary>One node of a reading-order snapshot; ids are opaque and stable per artifact.</summary>
public sealed record ReadingOrderNode
{
    /// <summary>Opaque id.</summary>
    [JsonPropertyName("nodeId")] public string NodeId { get; init; } = "";

    /// <summary>Parent id, null for a root.</summary>
    [JsonPropertyName("parentId")] public string? ParentId { get; init; }

    /// <summary>Structure role or HTML tag.</summary>
    [JsonPropertyName("role")] public string Role { get; init; } = "";

    /// <summary>Position among siblings in the stored order.</summary>
    [JsonPropertyName("logicalIndex")] public int LogicalIndex { get; init; }

    /// <summary>Page of a PDF structure node, when known.</summary>
    [JsonPropertyName("pageNumber")] public int? PageNumber { get; init; }

    /// <summary>pdf_structure or html_dom; unknown values are preserved.</summary>
    [JsonPropertyName("origin")] public string Origin { get; init; } = "";

    /// <summary>Bounded text preview.</summary>
    [JsonPropertyName("textPreview")] public string? TextPreview { get; init; }

    /// <summary>Rendered box, when known.</summary>
    [JsonPropertyName("boundingBox")] public BoundingBox? BoundingBox { get; init; }
}

/// <summary>Reading-order evidence bound to one artifact hash. <see cref="ReviewRequired"/> is always true.</summary>
public sealed record ReadingOrderSnapshot
{
    /// <summary>The job the evidence belongs to.</summary>
    [JsonPropertyName("jobId")] public Guid JobId { get; init; }

    /// <summary>SHA-256 of the artifact described.</summary>
    [JsonPropertyName("sourceArtifactSha256")] public string SourceArtifactSha256 { get; init; } = "";

    /// <summary>Version to quote when a correction is approved.</summary>
    [JsonPropertyName("snapshotVersion")] public int SnapshotVersion { get; init; }

    /// <summary>The nodes.</summary>
    [JsonPropertyName("nodes")] public IReadOnlyList<ReadingOrderNode> Nodes { get; init; } = [];

    /// <summary>Always true.</summary>
    [JsonPropertyName("reviewRequired")] public bool ReviewRequired { get; init; }

    /// <summary>Always needs_review.</summary>
    [JsonPropertyName("readingOrderReview")] public string ReadingOrderReview { get; init; } = "";

    /// <summary>needs_review or not_run.</summary>
    [JsonPropertyName("focusOrderReview")] public string FocusOrderReview { get; init; } = "";

    /// <summary>Node ids in focus order.</summary>
    [JsonPropertyName("focusOrderNodeIds")] public IReadOnlyList<string> FocusOrderNodeIds { get; init; } = [];

    /// <summary>Node ids in visual order, as a candidate for a person to review.</summary>
    [JsonPropertyName("visualOrderCandidateNodeIds")] public IReadOnlyList<string> VisualOrderCandidateNodeIds { get; init; } = [];

    /// <summary>SHA-256 of the artifact each snapshot origin describes.</summary>
    [JsonPropertyName("artifactHashes")] public IReadOnlyDictionary<string, string> ArtifactHashes { get; init; } = new Dictionary<string, string>();
}

/// <summary>What a reviewer approved: the complete children of one parent, in the new order.</summary>
public sealed record ReadingOrderRemediation
{
    /// <summary>The snapshot's artifact hash; a changed artifact is refused with 409.</summary>
    public required string BaseArtifactSha256 { get; init; }

    /// <summary>The snapshot's version.</summary>
    public required int SnapshotVersion { get; init; }

    /// <summary>One of <see cref="RemediationTargets"/>.</summary>
    public required string Target { get; init; }

    /// <summary>Every child of one parent, in the approved order.</summary>
    public required IReadOnlyList<string> OrderedNodeIds { get; init; }

    /// <summary>Must be true: a person checked the sequence against the content.</summary>
    public required bool Acknowledged { get; init; }

    /// <summary>Audit label asserted by the caller and not authenticated; trimmed, 1-200 printable characters.</summary>
    public string? Reviewer { get; init; }
}

/// <summary>What your backend asks for after authenticating its own user.</summary>
public sealed record ClientSessionRequest
{
    /// <summary>The browser origin the session may be used from, for example https://app.example.</summary>
    [JsonPropertyName("allowedOrigin")] public required string AllowedOrigin { get; init; }

    /// <summary>Scopes from <see cref="SessionScopes"/>; both when omitted.</summary>
    [JsonPropertyName("scopes")] public IReadOnlyList<string>? Scopes { get; init; }

    /// <summary>Job budget, 1-1000 (default 10).</summary>
    [JsonPropertyName("maxJobs")] public int? MaxJobs { get; init; }

    /// <summary>Upload budget, 1-10000 (default ten per job).</summary>
    [JsonPropertyName("maxUploads")] public int? MaxUploads { get; init; }

    /// <summary>Lifetime in seconds, 1-3600 (default 900).</summary>
    [JsonPropertyName("ttlSeconds")] public int? TtlSeconds { get; init; }
}

/// <summary>A minted browser session; hand <see cref="SessionToken"/> to the page, never the server token.</summary>
public sealed record ClientSession
{
    /// <summary>Short-lived bearer for browser code.</summary>
    [JsonPropertyName("sessionToken")] public string SessionToken { get; init; } = "";

    /// <summary>When the session ends.</summary>
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Version and enabled features of the server.</summary>
public sealed record Info
{
    /// <summary>Server version.</summary>
    [JsonPropertyName("version")] public string? Version { get; init; }

    /// <summary>Engine version.</summary>
    [JsonPropertyName("engineVersion")] public string? EngineVersion { get; init; }

    /// <summary>Enabled features: document, html, pdf, web; unknown values are preserved.</summary>
    [JsonPropertyName("features")] public IReadOnlyList<string> Features { get; init; } = [];
}

/// <summary>Answer of the liveness probe.</summary>
public sealed record Health
{
    /// <summary>ok while the process is running.</summary>
    [JsonPropertyName("status")] public string Status { get; init; } = "";
}

/// <summary>One problem of a rejected structured document.</summary>
public sealed record ProblemError
{
    /// <summary>JSON Pointer to the offending value; empty for the document itself.</summary>
    [JsonPropertyName("path")] public string Path { get; init; } = default!;

    /// <summary>For example IMAGE_ALT_MISSING or HEADING_LEVEL_SKIPPED; unknown values are preserved.</summary>
    [JsonPropertyName("code")] public string Code { get; init; } = default!;

    /// <summary>Explanation.</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = default!;
}

/// <summary>RFC 9457 problem body of the API.</summary>
public sealed record Problem
{
    /// <summary>Problem type, usually about:blank.</summary>
    [JsonPropertyName("type")] public string? Type { get; init; }

    /// <summary>Short title.</summary>
    [JsonPropertyName("title")] public string? Title { get; init; }

    /// <summary>HTTP status.</summary>
    [JsonPropertyName("status")] public int Status { get; init; }

    /// <summary>Stable problem code; see <see cref="ProblemCodes"/>.</summary>
    [JsonPropertyName("code")] public string? Code { get; init; }

    /// <summary>Human-readable detail.</summary>
    [JsonPropertyName("detail")] public string? Detail { get; init; }

    /// <summary>Per-problem entries of a rejected structured document.</summary>
    [JsonPropertyName("errors")] public IReadOnlyList<ProblemError>? Errors { get; init; }
}

/// <summary>Response metadata of a call, for callers that need more than the parsed body.</summary>
/// <param name="Status">HTTP status.</param>
/// <param name="Location">Where to poll the job, as sent.</param>
/// <param name="PreferenceApplied">wait=N when the server held the request.</param>
/// <param name="IdempotentReplayed">True when the stored answer of an earlier request was replayed.</param>
public sealed record ResponseMeta(int Status, string? Location, string? PreferenceApplied, bool IdempotentReplayed);
