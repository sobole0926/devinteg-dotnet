using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevInteg.DropboxSign;

/// <summary>Why a callback was not accepted.</summary>
public enum CallbackRejection
{
    /// <summary>Accepted.</summary>
    None,

    /// <summary>Empty, too large, not JSON, without an event, or with ids far longer than the vendor issues.</summary>
    Malformed,

    /// <summary>The event hash does not match the account's key: not from Dropbox Sign, or for another account.</summary>
    SignatureInvalid,

    /// <summary>No <c>Content-Sha256</c> header: stripped on the way (a proxy), or the vendor stopped sending it.</summary>
    ContentHashMissing,

    /// <summary>The <c>Content-Sha256</c> header does not match the payload under the account's key: altered, or another account.</summary>
    ContentHashMismatch,

    /// <summary>Verified, but dated outside the window the vendor delivers in: a replay of a captured event.</summary>
    Stale,
}

/// <summary>A verified callback event: what happened, when, and to which request and signature.</summary>
/// <param name="EventType">The vendor's event name; see <see cref="DropboxSignEventTypes"/>.</param>
/// <param name="EventTime">The event time exactly as sent (Unix seconds as text), for building a deduplication key.</param>
/// <param name="OccurredAt">The event time.</param>
/// <param name="SignatureRequestId">The request the event is about, or null for an account or test event.</param>
/// <param name="RelatedSignatureId">The signature the event is about (a signer signed or declined), when there is one.</param>
public sealed record DropboxSignEvent(string EventType, string EventTime, DateTimeOffset OccurredAt, string? SignatureRequestId, string? RelatedSignatureId);

/// <summary>The event names Dropbox Sign sends for signature requests and callback tests.</summary>
public static class DropboxSignEventTypes
{
    /// <summary>The test event the vendor sends when a callback URL is set.</summary>
    public const string CallbackTest = "callback_test";

    /// <summary>The request was sent.</summary>
    public const string Sent = "signature_request_sent";

    /// <summary>A signer opened the request.</summary>
    public const string Viewed = "signature_request_viewed";

    /// <summary>A signer signed.</summary>
    public const string SignerSigned = "signature_request_signed";

    /// <summary>A signer declined.</summary>
    public const string Declined = "signature_request_declined";

    /// <summary>Every signer signed.</summary>
    public const string AllSigned = "signature_request_all_signed";

    /// <summary>The executed file can be downloaded.</summary>
    public const string Downloadable = "signature_request_downloadable";

    /// <summary>The request expired unsigned.</summary>
    public const string Expired = "signature_request_expired";

    /// <summary>The request was canceled.</summary>
    public const string Canceled = "signature_request_canceled";

    /// <summary>A reminder was sent.</summary>
    public const string Remind = "signature_request_remind";

    /// <summary>An email to a signer bounced.</summary>
    public const string EmailBounce = "signature_request_email_bounce";

    /// <summary>The vendor could not process the request.</summary>
    public const string Invalid = "signature_request_invalid";

    /// <summary>The vendor hit an error on the request's file.</summary>
    public const string FileError = "file_error";
}

/// <summary>
/// Reads and verifies one Dropbox Sign callback with two gates, both keyed with the account's API key and compared in
/// constant time: the <c>Content-Sha256</c> header over the whole payload, and the event hash, HMAC-SHA256 of
/// <c>event_time + event_type</c>. Nothing is read from an unverified body. Answer an accepted callback with
/// <see cref="Acknowledgement"/> as the response body, or the vendor keeps retrying; re-read the request with
/// <see cref="DropboxSignClient.GetAsync"/> before acting on it.
/// </summary>
public static class DropboxSignCallbacks
{
    /// <summary>The body the vendor expects back from a callback it should stop retrying.</summary>
    public const string Acknowledgement = "Hello API Event Received";

    /// <summary>The request header carrying the payload's HMAC.</summary>
    public const string ContentHashHeader = "Content-Sha256";

    /// <summary>The form field the event is posted in.</summary>
    public const string PayloadField = "json";

    /// <summary>The largest payload accepted, in characters.</summary>
    public const int MaxPayloadLength = 256 * 1024;

    /// <summary>The longest request or signature id accepted; the vendor's are 40 characters.</summary>
    public const int MaxIdLength = 128;

    /// <summary>The vendor retries an unacknowledged event for about 30 hours; an older one is a replay.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    /// <summary>How far ahead of the receiver's clock an event may be dated, since clocks differ.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromMinutes(10);

    /// <summary>Verifies and reads one callback.</summary>
    /// <param name="json">The <see cref="PayloadField"/> form field as posted.</param>
    /// <param name="contentHash">The request's <see cref="ContentHashHeader"/> header.</param>
    /// <param name="apiKey">The account's API key.</param>
    /// <param name="now">The receiver's clock, for the replay window.</param>
    /// <param name="callbackEvent">The event, when accepted.</param>
    /// <param name="rejection">Why it was refused, or <see cref="CallbackRejection.None"/>.</param>
    public static bool TryVerify(string? json, string? contentHash, string apiKey, DateTimeOffset now, [NotNullWhen(true)] out DropboxSignEvent? callbackEvent, out CallbackRejection rejection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        callbackEvent = null;
        rejection = CallbackRejection.Malformed;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxPayloadLength)
        {
            return false;
        }

        // Checked before the body is parsed: an unverified body is not read at all.
        if (string.IsNullOrEmpty(contentHash))
        {
            rejection = CallbackRejection.ContentHashMissing;
            return false;
        }

        if (!FixedTimeEquals(ContentHash(apiKey, json), contentHash))
        {
            rejection = CallbackRejection.ContentHashMismatch;
            return false;
        }

        CallbackWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<CallbackWire>(json, DropboxSignJson.Options);
        }
        catch (JsonException)
        {
            return false;
        }

        if (wire?.Event is not { EventTime: { Length: > 0 } time, EventType: { Length: > 0 } type, EventHash: { Length: > 0 } hash })
        {
            return false;
        }

        if (!FixedTimeEquals(EventHash(apiKey, time, type), hash.ToLowerInvariant()))
        {
            rejection = CallbackRejection.SignatureInvalid;
            return false;
        }

        if (!long.TryParse(time, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var at = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (at < now - MaxAge || at > now + MaxAhead)
        {
            rejection = CallbackRejection.Stale;
            return false;
        }

        var requestId = wire.SignatureRequest?.SignatureRequestId;
        var signatureId = wire.Event.EventMetadata?.RelatedSignatureId;
        if (requestId?.Length > MaxIdLength || signatureId?.Length > MaxIdLength)
        {
            // Refused rather than cut, since a cut id could name another request.
            return false;
        }

        callbackEvent = new DropboxSignEvent(type, time, at, string.IsNullOrWhiteSpace(requestId) ? null : requestId, string.IsNullOrWhiteSpace(signatureId) ? null : signatureId);
        rejection = CallbackRejection.None;
        return true;
    }

    /// <summary>The event hash Dropbox Sign sends: lowercase hex of HMAC-SHA256(apiKey, eventTime + eventType).</summary>
    public static string EventHash(string apiKey, string eventTime, string eventType) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiKey), Encoding.UTF8.GetBytes(eventTime + eventType)));

    /// <summary>
    /// The <c>Content-Sha256</c> header Dropbox Sign sends: base64 of the lowercase hex HMAC-SHA256 of the payload, keyed
    /// with the API key. The vendor's documentation says "base64" and its sample prints hex; what its sandbox sends is
    /// base64 of the hex (88 characters), not of the 32-byte digest (observed November 2026).
    /// </summary>
    public static string ContentHash(string apiKey, string json) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiKey), Encoding.UTF8.GetBytes(json)))));

    private static bool FixedTimeEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(presented));
}
