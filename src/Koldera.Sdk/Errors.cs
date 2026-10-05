namespace Koldera;

/// <summary>
/// Every non-2xx answer of the API becomes one of these. <see cref="Code"/> is the stable problem code
/// (<c>QUEUE_FULL</c>, <c>STALE_READING_ORDER_SNAPSHOT</c>, ...); the exception type tells the caller what to do.
/// Messages never include a credential.
/// </summary>
public class ApiException : Exception
{
    /// <summary>Builds the exception from the problem body and the optional <c>Retry-After</c>.</summary>
    public ApiException(Problem problem, TimeSpan? retryAfter)
        : base(Describe(problem))
    {
        ArgumentNullException.ThrowIfNull(problem);
        Status = problem.Status;
        Code = problem.Code ?? "UNKNOWN";
        Title = problem.Title ?? "API error";
        Detail = problem.Detail;
        Errors = problem.Errors ?? [];
        RetryAfter = retryAfter;
    }

    /// <summary>HTTP status.</summary>
    public int Status { get; }

    /// <summary>Problem code; an unknown code of a newer core is preserved as it was sent.</summary>
    public string Code { get; }

    /// <summary>Short title.</summary>
    public string Title { get; }

    /// <summary>Human-readable detail, when given.</summary>
    public string? Detail { get; }

    /// <summary>Per-problem entries of a rejected structured document (path, code, message); empty otherwise.</summary>
    public IReadOnlyList<ProblemError> Errors { get; }

    /// <summary>What the API asked to wait (<c>Retry-After</c>), when given.</summary>
    public TimeSpan? RetryAfter { get; }

    internal static ApiException From(Problem problem, TimeSpan? retryAfter) => problem.Status switch
    {
        400 or 422 => new ValidationException(problem, retryAfter),
        401 => new AuthenticationException(problem, retryAfter),
        403 => new PermissionException(problem, retryAfter),
        404 => new NotFoundException(problem, retryAfter),
        409 => new ConflictException(problem, retryAfter),
        415 => new UnsupportedMediaTypeException(problem, retryAfter),
        421 => new HostNotAllowedException(problem, retryAfter),
        429 => new RateLimitException(problem, retryAfter),
        503 => new ServiceUnavailableException(problem, retryAfter),
        >= 500 => new ServerException(problem, retryAfter),
        _ => new ApiException(problem, retryAfter),
    };

    private static string Describe(Problem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var detail = string.IsNullOrEmpty(problem.Detail) ? "" : ": " + problem.Detail;
        return $"{problem.Title ?? "API error"} ({problem.Code ?? "UNKNOWN"}, HTTP {problem.Status}){detail}";
    }
}

/// <summary>401: the token is missing, malformed, expired or unknown.</summary>
public sealed class AuthenticationException(Problem problem, TimeSpan? retryAfter) : ApiException(problem, retryAfter);

/// <summary>403: the token is valid but lacks the scope or the right (INSUFFICIENT_SCOPE, FORBIDDEN, SIGNATURE_INVALID).</summary>
public sealed class PermissionException(Problem problem, TimeSpan? retryAfter) : ApiException(problem, retryAfter);

/// <summary>404: the resource does not exist, is not visible to this token, or its feature is switched off.</summary>
public sealed class NotFoundException(Problem problem, TimeSpan? retryAfter) : ApiException(problem, retryAfter);

/// <summary>409: idempotency key reused with another body, stale reading-order snapshot, expired artifact.</summary>
public sealed class ConflictException(Problem problem, TimeSpan? retryAfter) : ApiException(problem, retryAfter);

/// <summary>422, and 400 for a malformed body: the request or the input is invalid. See <see cref="ApiException.Code"/> and <see cref="ApiException.Errors"/>.</summary>
public sealed class ValidationException(Problem problem, TimeSpan? retryAfter) : ApiException(problem, retryAfter);

/// <summary>415: the body is not JSON, or the input type is unsupported.</summary>
public sealed class UnsupportedMediaTypeException(Problem problem, TimeSpan? retryAfter) : ApiException(problem, retryAfter);

/// <summary>421: the request reached a host the server does not serve.</summary>
public sealed class HostNotAllowedException(Problem problem, TimeSpan? retryAfter) : ApiException(problem, retryAfter);

/// <summary>429: a client-session budget is used up (SESSION_JOB_LIMIT, SESSION_UPLOAD_LIMIT). Mint a new session.</summary>
public sealed class RateLimitException(Problem problem, TimeSpan? retryAfter) : ApiException(problem, retryAfter);

/// <summary>Any 5xx other than 503.</summary>
public class ServerException(Problem problem, TimeSpan? retryAfter) : ApiException(problem, retryAfter);

/// <summary>503, including QUEUE_FULL; <see cref="ApiException.RetryAfter"/> says when to try again.</summary>
public sealed class ServiceUnavailableException(Problem problem, TimeSpan? retryAfter) : ServerException(problem, retryAfter);

/// <summary>The SDK was used wrongly: bad options, an invalid argument, an unacknowledged correction, an empty stream.</summary>
public sealed class ConfigurationException(string message) : Exception(message);

/// <summary><see cref="IJobs.WaitAsync"/> ran past its timeout. The job keeps running server-side.</summary>
public sealed class JobTimeoutException(Guid jobId, TimeSpan timeout) : Exception($"job {jobId} did not finish within {timeout}")
{
    /// <summary>The job that is still running.</summary>
    public Guid JobId { get; } = jobId;
}

/// <summary>A signed storage exchange (upload PUT or artifact download) failed, or the downloaded bytes did not match.</summary>
public sealed class StorageException(string message, int? status = null, string? code = null) : Exception(message)
{
    /// <summary>HTTP status the storage answered, when the failure was an HTTP one.</summary>
    public int? Status { get; } = status;

    /// <summary>Problem code of the storage answer (SIGNATURE_INVALID, UPLOAD_EXPIRED, ...), when it sent one.</summary>
    public string? Code { get; } = code;
}
