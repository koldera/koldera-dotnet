using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Koldera;

internal sealed record CallResult<T>(T Value, ResponseMeta Meta, TimeSpan? RetryAfter);

internal sealed class CallOptions
{
    public string? Body { get; init; }

    public string? IdempotencyKey { get; init; }

    public int? Wait { get; init; }

    public bool Anonymous { get; init; }

    public bool RetryOnBusy { get; init; }
}

/// <summary>Bearer-authenticated JSON calls plus the token-free storage exchanges.</summary>
internal sealed class Transport
{
    private const int MaxBusyRetries = 2;
    private const int MaxNetworkRetries = 2;
    private static readonly TimeSpan NetworkRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxBusyDelay = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly HttpClient _storage;
    private readonly Uri _baseUrl;
    private readonly string _token;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _storageTimeout;
    private readonly string _userAgent;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public Transport(HttpClient http, HttpClient storage, Uri baseUrl, string token, TimeSpan timeout, TimeSpan storageTimeout, string userAgent, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _http = http;
        _storage = storage;
        _baseUrl = new Uri(baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        _token = token;
        _timeout = timeout;
        _storageTimeout = storageTimeout;
        _userAgent = userAgent;
        _delay = delay ?? Task.Delay;
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => _delay(delay, cancellationToken);

    public async Task<CallResult<T>> CallAsync<T>(HttpMethod method, string path, JsonTypeInfo<T> typeInfo, CallOptions options, CancellationToken cancellationToken)
    {
        var timeout = options.Wait is { } wait && wait > 0 ? Max(_timeout, TimeSpan.FromSeconds(wait + 10)) : _timeout;
        EnsureHttpClientAllows(timeout);
        // A repeated creation carries the same key, so the server replays its answer instead of creating a second job.
        var safeToRepeat = method == HttpMethod.Get || options.IdempotencyKey is not null;
        var busy = 0;
        var network = 0;
        for (;;)
        {
            using var request = BuildRequest(method, path, options);
            HttpResponseMessage response;
            byte[] body;
            try
            {
                (response, body) = await SendAsync(_http, request, timeout, path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or TimeoutException)
            {
                // A timeout of a held request would only hold again for as long; connection failures are repeated.
                var heldTimeout = options.Wait is not null && error is TimeoutException;
                if (!cancellationToken.IsCancellationRequested && safeToRepeat && !heldTimeout && network < MaxNetworkRetries)
                {
                    network += 1;
                    await _delay(NetworkRetryDelay * network, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw;
            }

            using (response)
            {
                var retryAfter = ParseRetryAfter(response.Headers);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return new CallResult<T>(Parse(body, typeInfo, status, retryAfter), MetaOf(response), retryAfter);
                }

                var problem = ReadProblem(body, response);
                var busyStatus = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
                if (method == HttpMethod.Get && options.RetryOnBusy && busyStatus && busy < MaxBusyRetries)
                {
                    busy += 1;
                    var pause = retryAfter ?? TimeSpan.FromSeconds(1);
                    await _delay(Max(TimeSpan.Zero, pause > MaxBusyDelay ? MaxBusyDelay : pause), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw ApiException.From(problem, retryAfter);
            }
        }
    }

    public async Task StoragePutAsync(string url, byte[] bytes, string contentType, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var (response, body) = await SendAsync(_storage, request, _storageTimeout, "storage", cancellationToken).ConfigureAwait(false);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var problem = ReadProblem(body, response);
                throw new StorageException($"storage answered HTTP {(int)response.StatusCode} ({problem.Code}) to PUT", (int)response.StatusCode, problem.Code);
            }
        }
    }

    public async Task<byte[]> StorageGetAsync(string url, long expectedBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_storageTimeout);
        try
        {
            using var response = await _storage.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsByteArrayAsync(timeoutSource.Token).ConfigureAwait(false);
                var problem = ReadProblem(errorBody, response);
                throw new StorageException($"storage answered HTTP {(int)response.StatusCode} ({problem.Code}) to GET", (int)response.StatusCode, problem.Code);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeoutSource.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > expectedBytes)
                {
                    throw new StorageException($"storage sent more than the declared {expectedBytes} bytes");
                }
            }

            return buffer.ToArray();
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(TimeoutText(_storage, "the download", _storageTimeout, error, timeoutSource));
        }
    }

    internal static string? CheckKey(string? key)
    {
        if (key is null)
        {
            return null;
        }

        var trimmed = key.Trim();
        if (key.Length is < 1 or > 255 || key.Any(c => c < 0x20 || c > 0x7e) || trimmed.Length == 0)
        {
            throw new ConfigurationException("idempotencyKey must be 1-255 printable ASCII characters and not blank");
        }

        return trimmed;
    }

    private static string TimeoutText(HttpClient client, string what, TimeSpan sdkTimeout, OperationCanceledException error, CancellationTokenSource source) =>
        !source.IsCancellationRequested && error.InnerException is TimeoutException
            ? $"{what} timed out: the HttpClient.Timeout of {client.Timeout} elapsed"
            : $"{what} timed out after {sdkTimeout}";

    private void EnsureHttpClientAllows(TimeSpan needed)
    {
        var limit = _http.Timeout;
        if (limit != Timeout.InfiniteTimeSpan && needed > limit)
        {
            throw new ConfigurationException($"the HttpClient has a Timeout of {limit} but this call needs {needed}; set HttpClient.Timeout to Timeout.InfiniteTimeSpan before its first use");
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, CallOptions options)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseUrl, path.TrimStart('/')));
        if (!options.Anonymous)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        if (options.IdempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", options.IdempotencyKey);
        }

        if (options.Wait is { } wait)
        {
            request.Headers.TryAddWithoutValidation("Prefer", $"wait={wait}");
        }

        if (options.Body is not null)
        {
            request.Content = new StringContent(options.Body, System.Text.Encoding.UTF8, "application/json");
        }

        return request;
    }

    private async Task<(HttpResponseMessage Response, byte[] Body)> SendAsync(HttpClient client, HttpRequestMessage request, TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
            try
            {
                var body = await response.Content.ReadAsByteArrayAsync(timeoutSource.Token).ConfigureAwait(false);
                return (response, body);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(TimeoutText(client, $"the request to {what}", timeout, error, timeoutSource));
        }
    }

    private static T Parse<T>(byte[] body, JsonTypeInfo<T> typeInfo, int status, TimeSpan? retryAfter)
    {
        try
        {
            if (body.Length > 0)
            {
                var value = JsonSerializer.Deserialize(body, typeInfo);
                if (value is not null)
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
            // fall through to the error below
        }

        var problem = new Problem { Type = "about:blank", Title = "Invalid response", Status = status, Code = "INTERNAL_ERROR", Detail = "The answer was not the expected JSON." };
        throw new ServerException(problem, retryAfter);
    }

    private static ResponseMeta MetaOf(HttpResponseMessage response)
    {
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        return new ResponseMeta((int)response.StatusCode, Header("Location"), Header("Preference-Applied"), Header("Idempotent-Replayed") == "true");
    }

    private static Problem ReadProblem(byte[] body, HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var title = string.IsNullOrEmpty(response.ReasonPhrase) ? "HTTP error" : response.ReasonPhrase;
        var fallback = new Problem { Type = "about:blank", Title = title, Status = status, Code = status >= 500 ? "INTERNAL_ERROR" : "UNKNOWN" };
        try
        {
            var problem = JsonSerializer.Deserialize(body, KolderaJsonContext.Default.Problem);
            if (problem is { Code: not null, Status: not 0 })
            {
                var errors = problem.Errors?.Where(e => e is { Path: not null, Code: not null, Message: not null }).ToList();
                return problem with { Type = problem.Type ?? "about:blank", Title = problem.Title ?? title, Errors = errors };
            }
        }
        catch (JsonException)
        {
            // a body that is not JSON: use the fallback
        }

        return fallback;
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseHeaders headers)
    {
        var value = headers.RetryAfter;
        if (value is null)
        {
            return null;
        }

        if (value.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (value.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            return until < TimeSpan.Zero ? TimeSpan.Zero : until;
        }

        return null;
    }
}
