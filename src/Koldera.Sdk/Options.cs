namespace Koldera;

/// <summary>Options of <see cref="KolderaClient"/>.</summary>
public sealed class KolderaClientOptions
{
    /// <summary>Origin of your koldera-core server, for example https://koldera.example.com. Falls back to the KOLDERA_URL environment variable.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Server token: any non-empty string the server accepts. Falls back to KOLDERA_TOKEN. Never ship it to a browser.</summary>
    public string? Token { get; set; }

    /// <summary>Per-request timeout (default 30 s). A call with a wait stretches it to cover the wait.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Appended to the SDK's own product token in the User-Agent header.</summary>
    public string? UserAgentSuffix { get; set; }

    /// <summary>Timeout of a signed upload or download (default 10 minutes), separate from the API <see cref="Timeout"/>.</summary>
    public TimeSpan StorageTimeout { get; set; } = TimeSpan.FromMinutes(10);

    internal Func<TimeSpan, CancellationToken, Task>? DelayAsync { get; set; }

    /// <summary>Never prints the token.</summary>
    public override string ToString() =>
        $"KolderaClientOptions {{ BaseUrl = {BaseUrl}, Token = {(string.IsNullOrEmpty(Token) ? "(unset)" : "***")} }}";
}

/// <summary>Options shared by every call that creates a job.</summary>
public class CreateOptions
{
    /// <summary>
    /// Ask the server to hold the request for up to this many seconds (1-900, sent as Prefer: wait=N).
    /// A finished job comes back as it is; a job still running after the wait comes back with its current
    /// state, so check <see cref="Job.IsFinished"/> or set <see cref="UntilFinished"/>.
    /// </summary>
    public int? Wait { get; set; }

    /// <summary>When the returned job is not finished yet, keep polling until it is (see <see cref="IJobs.WaitAsync"/>).</summary>
    public bool UntilFinished { get; set; }

    /// <summary>Polling settings used when <see cref="UntilFinished"/> is set.</summary>
    public WaitOptions? Poll { get; set; }

    /// <summary>
    /// A key of 1-255 printable ASCII characters (trimmed). A retry with the same key and body replays the
    /// stored answer instead of creating a second job. Generated when omitted.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Called with the response metadata of the job creation call.</summary>
    public Action<ResponseMeta>? OnResponse { get; set; }
}

/// <summary>Options of a PDF job.</summary>
public sealed class PdfJobOptions : CreateOptions
{
    /// <summary>File name sent with the upload (default document.pdf).</summary>
    public string? FileName { get; set; }

    /// <summary>BCP 47 tag used only when the document has none; never guessed.</summary>
    public string? ExplicitLanguage { get; set; }

    /// <summary>Title to write; trimmed, 1-500 printable characters; replaces an existing title.</summary>
    public string? ExplicitTitle { get; set; }
}

/// <summary>Options of an HTML-to-PDF job.</summary>
public sealed class HtmlToPdfJobOptions : CreateOptions
{
    /// <summary>File name sent with the upload (default page.html).</summary>
    public string? FileName { get; set; }

    /// <summary>BCP 47 tag used only when the document has none.</summary>
    public string? ExplicitLanguage { get; set; }

    /// <summary>Title to write; trimmed, 1-500 printable characters.</summary>
    public string? ExplicitTitle { get; set; }

    /// <summary>Print only the elements matching this CSS selector (1-200 printable characters).</summary>
    public string? ContentSelector { get; set; }
}

/// <summary>Options of <see cref="IJobs.WaitAsync"/>.</summary>
public sealed class WaitOptions
{
    /// <summary>Give up after this long (default 10 minutes); throws <see cref="JobTimeoutException"/>. The job keeps running server-side.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>First delay when the API sends no Retry-After (default 1 s); doubles up to 10 s.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Called after every poll with the latest job.</summary>
    public Action<Job>? OnUpdate { get; set; }
}
