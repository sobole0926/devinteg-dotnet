using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevInteg.DropboxSign;

/// <summary>
/// Dropbox Sign's wire shapes, only what the client reads. Unknown members are ignored, since the API adds fields freely.
/// Timestamps are Unix seconds.
/// </summary>
internal static class DropboxSignJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

internal sealed record SignatureRequestEnvelope(
    [property: JsonPropertyName("signature_request")] SignatureRequestWire? SignatureRequest);

internal sealed record SignatureRequestListEnvelope(
    [property: JsonPropertyName("signature_requests")] IReadOnlyList<SignatureRequestWire>? SignatureRequests,
    [property: JsonPropertyName("list_info")] ListInfoWire? ListInfo);

internal sealed record ListInfoWire(
    [property: JsonPropertyName("num_pages")] int NumPages,
    [property: JsonPropertyName("page")] int Page);

internal sealed record SignatureRequestWire(
    [property: JsonPropertyName("signature_request_id")] string? SignatureRequestId,
    [property: JsonPropertyName("is_complete")] bool IsComplete,
    [property: JsonPropertyName("is_declined")] bool IsDeclined,
    [property: JsonPropertyName("has_error")] bool HasError,
    [property: JsonPropertyName("expires_at")] long? ExpiresAt,
    [property: JsonPropertyName("metadata")] Dictionary<string, JsonElement>? Metadata,
    [property: JsonPropertyName("signatures")] IReadOnlyList<SignatureWire>? Signatures);

internal sealed record SignatureWire(
    [property: JsonPropertyName("signature_id")] string? SignatureId,
    [property: JsonPropertyName("signer_email_address")] string? SignerEmailAddress,
    [property: JsonPropertyName("signer_name")] string? SignerName,
    [property: JsonPropertyName("order")] int? Order,
    [property: JsonPropertyName("status_code")] string? StatusCode,
    [property: JsonPropertyName("signed_at")] long? SignedAt);

internal sealed record EmbeddedEnvelope(
    [property: JsonPropertyName("embedded")] EmbeddedWire? Embedded);

internal sealed record EmbeddedWire(
    [property: JsonPropertyName("sign_url")] string? SignUrl,
    [property: JsonPropertyName("expires_at")] long? ExpiresAt);

internal sealed record ErrorEnvelope(
    [property: JsonPropertyName("error")] ErrorWire? Error);

internal sealed record ErrorWire(
    [property: JsonPropertyName("error_name")] string? ErrorName);

/// <summary>A callback's payload: the <c>json</c> form field. Only the event and the request's id are read.</summary>
internal sealed record CallbackWire(
    [property: JsonPropertyName("event")] CallbackEventWire? Event,
    [property: JsonPropertyName("signature_request")] CallbackSignatureRequestWire? SignatureRequest);

internal sealed record CallbackEventWire(
    [property: JsonPropertyName("event_time")] string? EventTime,
    [property: JsonPropertyName("event_type")] string? EventType,
    [property: JsonPropertyName("event_hash")] string? EventHash,
    [property: JsonPropertyName("event_metadata")] CallbackEventMetadataWire? EventMetadata);

internal sealed record CallbackEventMetadataWire(
    [property: JsonPropertyName("related_signature_id")] string? RelatedSignatureId);

internal sealed record CallbackSignatureRequestWire(
    [property: JsonPropertyName("signature_request_id")] string? SignatureRequestId);
