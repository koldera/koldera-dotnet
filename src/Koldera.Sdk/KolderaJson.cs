using System.Text.Json.Serialization;

namespace Koldera;

internal sealed record ListResponse<T>
{
    [JsonPropertyName("items")] public IReadOnlyList<T> Items { get; init; } = [];
}

internal sealed record UploadRequestBody(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("contentType")] string ContentType);

internal sealed record PdfJobRequestBody(
    [property: JsonPropertyName("uploadId")] Guid UploadId,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("explicitLanguage")] string? ExplicitLanguage,
    [property: JsonPropertyName("explicitTitle")] string? ExplicitTitle);

internal sealed record HtmlJobRequestBody(
    [property: JsonPropertyName("uploadId")] Guid UploadId,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("contentSelector")] string? ContentSelector,
    [property: JsonPropertyName("explicitLanguage")] string? ExplicitLanguage,
    [property: JsonPropertyName("explicitTitle")] string? ExplicitTitle);

internal sealed record WebJobRequestBody([property: JsonPropertyName("url")] string Url);

internal sealed record RemediationRequestBody(
    [property: JsonPropertyName("baseArtifactSha256")] string BaseArtifactSha256,
    [property: JsonPropertyName("snapshotVersion")] int SnapshotVersion,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("orderedNodeIds")] IReadOnlyList<string> OrderedNodeIds,
    [property: JsonPropertyName("acknowledged")] bool Acknowledged,
    [property: JsonPropertyName("reviewer")] string? Reviewer);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Job))]
[JsonSerializable(typeof(Info))]
[JsonSerializable(typeof(Health))]
[JsonSerializable(typeof(UploadTicket))]
[JsonSerializable(typeof(ClientSession))]
[JsonSerializable(typeof(ClientSessionRequest))]
[JsonSerializable(typeof(ReadingOrderSnapshot))]
[JsonSerializable(typeof(Problem))]
[JsonSerializable(typeof(ListResponse<Finding>))]
[JsonSerializable(typeof(ListResponse<RemediationAction>))]
[JsonSerializable(typeof(ListResponse<Artifact>))]
[JsonSerializable(typeof(UploadRequestBody))]
[JsonSerializable(typeof(PdfJobRequestBody))]
[JsonSerializable(typeof(HtmlJobRequestBody))]
[JsonSerializable(typeof(WebJobRequestBody))]
[JsonSerializable(typeof(RemediationRequestBody))]
internal sealed partial class KolderaJsonContext : JsonSerializerContext
{
}
