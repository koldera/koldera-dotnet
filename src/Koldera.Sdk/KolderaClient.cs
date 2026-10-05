using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Koldera;

/// <summary>
/// Server-side client of a koldera-core server. It holds the server token and can mint short-lived
/// sessions for browser code. Thread-safe: create one per process, or let <c>AddKoldera</c> manage it.
/// </summary>
public sealed class KolderaClient : IKolderaClient, IDisposable
{
    internal const string SdkName = "koldera-sdk-dotnet";
    internal const string SdkVersion = "0.1.0";

    private readonly Transport _transport;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Creates a client. Pass your own <see cref="HttpClient"/> to control pooling and proxies; otherwise one
    /// is owned and disposed with the client. A supplied client should have an infinite <c>Timeout</c>: the SDK
    /// applies <see cref="KolderaClientOptions.Timeout"/> itself and stretches it for calls that wait.
    /// </summary>
    public KolderaClient(KolderaClientOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var (baseUrl, token) = Resolve(options);
        var agent = $"{SdkName}/{SdkVersion}{(string.IsNullOrWhiteSpace(options.UserAgentSuffix) ? "" : " " + options.UserAgentSuffix)}";
        _transport = new Transport(_http, baseUrl, token, options.Timeout, agent, options.DelayAsync);
        Uploads = new Uploads(_transport);
        Jobs = new Jobs(_transport);
        Pdf = new PdfJobs(_transport, Uploads, Jobs);
        HtmlToPdf = new HtmlToPdfJobs(_transport, Uploads, Jobs);
        Web = new WebJobs(_transport, Jobs);
        Documents = new DocumentJobs(_transport, Uploads, Jobs);
        Artifacts = new Artifacts(_transport);
        ClientSessions = new ClientSessions(_transport);
    }

    /// <summary>Creates a client for dependency injection; <c>AddKoldera</c> registers it with the supplied <see cref="HttpClient"/>.</summary>
    [ActivatorUtilitiesConstructor]
    public KolderaClient(HttpClient httpClient, IOptions<KolderaClientOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), httpClient ?? throw new ArgumentNullException(nameof(httpClient)))
    {
    }

    /// <inheritdoc/>
    public IUploads Uploads { get; }

    /// <inheritdoc/>
    public IJobs Jobs { get; }

    /// <inheritdoc/>
    public IPdfJobs Pdf { get; }

    /// <inheritdoc/>
    public IHtmlToPdfJobs HtmlToPdf { get; }

    /// <inheritdoc/>
    public IWebJobs Web { get; }

    /// <inheritdoc/>
    public IDocumentJobs Documents { get; }

    /// <inheritdoc/>
    public IArtifacts Artifacts { get; }

    /// <inheritdoc/>
    public IClientSessions ClientSessions { get; }

    /// <inheritdoc/>
    public async Task<Info> InfoAsync(CancellationToken cancellationToken = default) =>
        (await _transport.CallAsync(HttpMethod.Get, "/v1/info", KolderaJsonContext.Default.Info, new CallOptions { RetryOnBusy = true }, cancellationToken).ConfigureAwait(false)).Value;

    /// <inheritdoc/>
    public async Task<Health> HealthAsync(CancellationToken cancellationToken = default) =>
        (await _transport.CallAsync(HttpMethod.Get, "/healthz", KolderaJsonContext.Default.Health, new CallOptions { Anonymous = true, RetryOnBusy = true }, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Disposes the owned <see cref="HttpClient"/>, if any.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private static (Uri BaseUrl, string Token) Resolve(KolderaClientOptions options)
    {
        var token = options.Token ?? Environment.GetEnvironmentVariable("KOLDERA_TOKEN") ?? "";
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ConfigurationException("Token is required: set KolderaClientOptions.Token or KOLDERA_TOKEN");
        }

        var baseUrl = options.BaseUrl ?? (Uri.TryCreate(Environment.GetEnvironmentVariable("KOLDERA_URL"), UriKind.Absolute, out var fromEnv) ? fromEnv : null);
        if (baseUrl is null)
        {
            throw new ConfigurationException("BaseUrl is required: set KolderaClientOptions.BaseUrl or KOLDERA_URL");
        }

        if (!baseUrl.IsAbsoluteUri || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new ConfigurationException("BaseUrl must be an absolute http(s) URL");
        }

        return (baseUrl, token);
    }
}

internal static partial class Checks
{
    public static string KeyOrNew(string? key) => Transport.CheckKey(key) ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>A key for a companion request of one logical call (the upload before a job), within the 255-character limit.</summary>
    public static string Derived(string key, string purpose) => $"{(key.Length > 240 ? key[..240] : key)}:{purpose}";

    public static void Wait(int? wait)
    {
        if (wait is { } value && (value < 1 || value > 900))
        {
            throw new ConfigurationException("wait must be a whole number of seconds from 1 to 900");
        }
    }

    public static void Mode(string mode, params string[] allowed)
    {
        if (!allowed.Contains(mode))
        {
            throw new ConfigurationException($"mode must be one of {string.Join(", ", allowed)}");
        }
    }

    public static void Language(string? tag)
    {
        if (tag is not null && !LanguageTag().IsMatch(tag))
        {
            throw new ConfigurationException("explicitLanguage must be a well-formed BCP 47 tag");
        }
    }

    public static string? Printable(string? value, int max, string what)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.EnumerateRunes().Count() > max || trimmed.Any(char.IsControl))
        {
            throw new ConfigurationException($"{what} must be 1-{max} printable characters");
        }

        return trimmed;
    }

    public static void Upload(string contentType, string fileName)
    {
        if (!UploadContentTypes.All.Contains(contentType))
        {
            throw new ConfigurationException($"contentType must be one of {string.Join(", ", UploadContentTypes.All)}");
        }

        if (string.IsNullOrEmpty(fileName) || fileName.Length > 255)
        {
            throw new ConfigurationException("fileName must be 1-255 characters");
        }
    }

    [GeneratedRegex("^[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*$")]
    private static partial Regex LanguageTag();
}

internal static class JobCreation
{
    /// <summary>POSTs a job request and applies <c>Wait</c> and <c>UntilFinished</c>; shared by every creation call.</summary>
    public static async Task<Job> PostAsync(Transport transport, IJobs jobs, string path, string body, string key, CreateOptions options, CancellationToken cancellationToken)
    {
        var result = await transport.CallAsync(HttpMethod.Post, path, KolderaJsonContext.Default.Job, new CallOptions { Body = body, IdempotencyKey = key, Wait = options.Wait }, cancellationToken).ConfigureAwait(false);
        options.OnResponse?.Invoke(result.Meta);
        if (options.UntilFinished && !result.Value.IsFinished)
        {
            return await jobs.WaitAsync(result.Value.Id, options.Poll, cancellationToken).ConfigureAwait(false);
        }

        return result.Value;
    }
}

internal sealed class Uploads(Transport transport) : IUploads
{
    public async Task<UploadTicket> UploadAsync(Stream content, string contentType, string fileName, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        Checks.Upload(contentType, fileName);
        var key = Checks.KeyOrNew(idempotencyKey);
        var bytes = await ReadAllAsync(content, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new ConfigurationException("the file is empty");
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var request = new UploadRequestBody(fileName, bytes.Length, sha256, contentType);
        var issued = await transport.CallAsync(HttpMethod.Post, "/v1/uploads", KolderaJsonContext.Default.UploadTicket,
            new CallOptions { Body = JsonSerializer.Serialize(request, KolderaJsonContext.Default.UploadRequestBody), IdempotencyKey = key }, cancellationToken).ConfigureAwait(false);
        var ticket = issued.Value;
        if (ticket.MaxBytes is { } max && bytes.Length > max)
        {
            throw new ConfigurationException($"the file ({bytes.Length} bytes) exceeds the ticket limit of {max} bytes");
        }

        try
        {
            await transport.StoragePutAsync(ticket.UploadUrl, bytes, contentType, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException error) when (issued.Meta.IdempotentReplayed && error is { Status: 409, Code: ProblemCodes.UploadAlreadyCompleted })
        {
            // A replayed ticket belongs to an earlier attempt whose PUT already stored these exact bytes (size and hash are part of the replayed request).
        }

        return ticket;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (!stream.CanRead)
        {
            throw new ObjectDisposedException(nameof(stream), "the stream cannot be read (disposed or write-only)");
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}

internal sealed class PdfJobs(Transport transport, IUploads uploads, IJobs jobs) : IPdfJobs
{
    public async Task<Job> CreateJobAsync(Stream pdf, string mode, PdfJobOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PdfJobOptions();
        ArgumentNullException.ThrowIfNull(pdf);
        Checks.Mode(mode, PdfJobModes.Analyze, PdfJobModes.Fix);
        Checks.Language(options.ExplicitLanguage);
        var title = Checks.Printable(options.ExplicitTitle, 500, "explicitTitle");
        Checks.Wait(options.Wait);
        var key = Checks.KeyOrNew(options.IdempotencyKey);
        var ticket = await uploads.UploadAsync(pdf, UploadContentTypes.Pdf, options.FileName ?? "document.pdf", Checks.Derived(key, "upload"), cancellationToken).ConfigureAwait(false);
        var body = new PdfJobRequestBody(ticket.UploadId, mode, "ua1", options.ExplicitLanguage, title);
        return await JobCreation.PostAsync(transport, jobs, "/v1/pdf/jobs", JsonSerializer.Serialize(body, KolderaJsonContext.Default.PdfJobRequestBody), key, options, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class HtmlToPdfJobs(Transport transport, IUploads uploads, IJobs jobs) : IHtmlToPdfJobs
{
    public async Task<Job> CreateJobAsync(Stream html, string mode, HtmlToPdfJobOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new HtmlToPdfJobOptions();
        ArgumentNullException.ThrowIfNull(html);
        Checks.Mode(mode, HtmlToPdfJobModes.GenerateAnalyze, HtmlToPdfJobModes.GenerateFix);
        Checks.Language(options.ExplicitLanguage);
        var title = Checks.Printable(options.ExplicitTitle, 500, "explicitTitle");
        var selector = Checks.Printable(options.ContentSelector, 200, "contentSelector");
        Checks.Wait(options.Wait);
        var key = Checks.KeyOrNew(options.IdempotencyKey);
        var ticket = await uploads.UploadAsync(html, UploadContentTypes.Html, options.FileName ?? "page.html", Checks.Derived(key, "upload"), cancellationToken).ConfigureAwait(false);
        var body = new HtmlJobRequestBody(ticket.UploadId, mode, "ua1", selector, options.ExplicitLanguage, title);
        return await JobCreation.PostAsync(transport, jobs, "/v1/html-to-pdf/jobs", JsonSerializer.Serialize(body, KolderaJsonContext.Default.HtmlJobRequestBody), key, options, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class WebJobs(Transport transport, IJobs jobs) : IWebJobs
{
    public async Task<Job> CreateScanAsync(Uri url, CreateOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new CreateOptions();
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new ConfigurationException("url must be an absolute http(s) URL");
        }

        Checks.Wait(options.Wait);
        var key = Checks.KeyOrNew(options.IdempotencyKey);
        var body = JsonSerializer.Serialize(new WebJobRequestBody(url.ToString()), KolderaJsonContext.Default.WebJobRequestBody);
        return await JobCreation.PostAsync(transport, jobs, "/v1/web/jobs", body, key, options, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class DocumentJobs(Transport transport, IUploads uploads, IJobs jobs) : IDocumentJobs
{
    public Task<Job> CreateJobAsync(StructuredDocument document, CreateOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PostAsync(document.ToJson(), options, cancellationToken);
    }

    public Task<Job> CreateJobAsync(JsonObject document, CreateOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PostAsync(DocumentJson.Write(document), options, cancellationToken);
    }

    public Task<UploadTicket> UploadImageAsync(Stream image, string contentType, string? fileName = null, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        if (!UploadContentTypes.Images.Contains(contentType))
        {
            throw new ConfigurationException($"image contentType must be one of {string.Join(", ", UploadContentTypes.Images)}");
        }

        return uploads.UploadAsync(image, contentType, fileName ?? "image", idempotencyKey, cancellationToken);
    }

    private async Task<Job> PostAsync(string body, CreateOptions? options, CancellationToken cancellationToken)
    {
        options ??= new CreateOptions();
        Checks.Wait(options.Wait);
        var key = Checks.KeyOrNew(options.IdempotencyKey);
        return await JobCreation.PostAsync(transport, jobs, "/v1/documents/jobs", body, key, options, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class Jobs(Transport transport) : IJobs
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    public async Task<Job> GetAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        (await GetJobAsync(jobId, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<Job> WaitAsync(Guid jobId, WaitOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new WaitOptions();
        var started = DateTimeOffset.UtcNow;
        var delay = options.InitialDelay;
        for (;;)
        {
            var result = await GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            options.OnUpdate?.Invoke(result.Value);
            if (result.Value.IsFinished)
            {
                return result.Value;
            }

            var pause = result.RetryAfter ?? delay;
            if (DateTimeOffset.UtcNow - started + pause > options.Timeout)
            {
                throw new JobTimeoutException(jobId, options.Timeout);
            }

            await transport.DelayAsync(pause, cancellationToken).ConfigureAwait(false);
            delay = delay * 2 > MaxDelay ? MaxDelay : delay * 2;
        }
    }

    public async Task<IReadOnlyList<Finding>> ListFindingsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        (await transport.CallAsync(HttpMethod.Get, $"/v1/jobs/{jobId:D}/findings", KolderaJsonContext.Default.ListResponseFinding, new CallOptions { RetryOnBusy = true }, cancellationToken).ConfigureAwait(false)).Value.Items;

    public async Task<IReadOnlyList<RemediationAction>> ListActionsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        (await transport.CallAsync(HttpMethod.Get, $"/v1/jobs/{jobId:D}/actions", KolderaJsonContext.Default.ListResponseRemediationAction, new CallOptions { RetryOnBusy = true }, cancellationToken).ConfigureAwait(false)).Value.Items;

    public async Task<IReadOnlyList<Artifact>> ListArtifactsAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        (await transport.CallAsync(HttpMethod.Get, $"/v1/jobs/{jobId:D}/artifacts", KolderaJsonContext.Default.ListResponseArtifact, new CallOptions { RetryOnBusy = true }, cancellationToken).ConfigureAwait(false)).Value.Items;

    public async Task<ReadingOrderSnapshot> GetReadingOrderAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        (await transport.CallAsync(HttpMethod.Get, $"/v1/jobs/{jobId:D}/reading-order", KolderaJsonContext.Default.ReadingOrderSnapshot, new CallOptions { RetryOnBusy = true }, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<Job> RemediateReadingOrderAsync(Guid jobId, ReadingOrderRemediation approval, CreateOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        options ??= new CreateOptions();
        if (!approval.Acknowledged)
        {
            throw new ConfigurationException("a reading-order correction must be acknowledged by the reviewer (Acknowledged = true)");
        }

        var ids = approval.OrderedNodeIds;
        if (ids is null || ids.Count < 2 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new ConfigurationException("OrderedNodeIds must list at least two distinct node ids");
        }

        var hash = approval.BaseArtifactSha256;
        if (hash is not { Length: 64 } || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ConfigurationException("BaseArtifactSha256 must be the 64-hex artifact hash from the snapshot");
        }

        var reviewer = Checks.Printable(approval.Reviewer, 200, "reviewer");
        Checks.Wait(options.Wait);
        var key = Checks.KeyOrNew(options.IdempotencyKey);
        var body = new RemediationRequestBody(hash, approval.SnapshotVersion, approval.Target, ids, true, reviewer);
        return await JobCreation.PostAsync(transport, this, $"/v1/jobs/{jobId:D}/reading-order/remediations", JsonSerializer.Serialize(body, KolderaJsonContext.Default.RemediationRequestBody), key, options, cancellationToken).ConfigureAwait(false);
    }

    private Task<CallResult<Job>> GetJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        transport.CallAsync(HttpMethod.Get, $"/v1/jobs/{jobId:D}", KolderaJsonContext.Default.Job, new CallOptions { RetryOnBusy = true }, cancellationToken);
}

internal sealed class Artifacts(Transport transport) : IArtifacts
{
    public async Task<byte[]> DownloadAsync(Artifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var bytes = await transport.StorageGetAsync(artifact.DownloadUrl, artifact.SizeBytes, cancellationToken).ConfigureAwait(false);
        if (bytes.Length != artifact.SizeBytes)
        {
            throw new StorageException($"artifact {artifact.Kind}: received {bytes.Length} bytes, expected {artifact.SizeBytes}");
        }

        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageException($"artifact {artifact.Kind}: SHA-256 mismatch");
        }

        return bytes;
    }

    public async Task DownloadAsync(Artifact artifact, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var bytes = await DownloadAsync(artifact, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class ClientSessions(Transport transport) : IClientSessions
{
    private static readonly Regex Origin = new("^https?://[^/]+$", RegexOptions.CultureInvariant);

    public async Task<ClientSession> CreateAsync(ClientSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AllowedOrigin is null || !Origin.IsMatch(request.AllowedOrigin))
        {
            throw new ConfigurationException("AllowedOrigin must be an absolute http(s) origin without a path");
        }

        var body = JsonSerializer.Serialize(request, KolderaJsonContext.Default.ClientSessionRequest);
        return (await transport.CallAsync(HttpMethod.Post, "/v1/client-sessions", KolderaJsonContext.Default.ClientSession, new CallOptions { Body = body }, cancellationToken).ConfigureAwait(false)).Value;
    }
}
