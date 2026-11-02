using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DevInteg.DropboxSign.Tests;

/// <summary>What a callback must carry to be believed, and what is read from it.</summary>
public sealed class DropboxSignCallbacksTests
{
    private const string Key = "callback-test-key";

    /// <summary>A moment just after the events below are dated, so none is stale.</summary>
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1761900000).AddMinutes(5);

    [Fact]
    public void A_verified_signed_event_names_its_type_time_request_and_signature()
    {
        var json = Event(DropboxSignEventTypes.SignerSigned, "1761900000", "req_1", "sig_1");

        Assert.True(Verify(json, Key, Now, out var verified, out var rejection));

        Assert.Equal(CallbackRejection.None, rejection);
        Assert.Equal(new DropboxSignEvent("signature_request_signed", "1761900000", DateTimeOffset.FromUnixTimeSeconds(1761900000), "req_1", "sig_1"), verified);
    }

    [Fact]
    public void A_test_event_without_a_request_is_verified_with_no_request()
    {
        Assert.True(Verify(TestEvent("1761900000"), Key, Now, out var verified, out _));

        Assert.Equal(DropboxSignEventTypes.CallbackTest, verified!.EventType);
        Assert.Null(verified.SignatureRequestId);
        Assert.Null(verified.RelatedSignatureId);
    }

    [Fact]
    public void A_wrong_hash_is_refused_as_unverifiable_and_a_broken_body_as_malformed()
    {
        var forged = Event(DropboxSignEventTypes.SignerSigned, "1761900000", "req_1", "sig_1")
            .Replace(DropboxSignCallbacks.EventHash(Key, "1761900000", "signature_request_signed"), new string('0', 64), StringComparison.Ordinal);

        Assert.False(Verify(forged, Key, Now, out _, out var forgedRejection));
        Assert.False(Verify(Event(DropboxSignEventTypes.SignerSigned, "1761900000", "req_1", "sig_1"), "another-key", Now, out _, out var otherKey));
        Assert.False(Verify("not json", Key, Now, out _, out var malformed));
        Assert.False(Verify("""{"account":{}}""", Key, Now, out _, out var eventless));
        Assert.False(DropboxSignCallbacks.TryVerify(null, "x", Key, Now, out _, out var empty));

        Assert.Equal(CallbackRejection.SignatureInvalid, forgedRejection);
        Assert.Equal(CallbackRejection.SignatureInvalid, otherKey);
        Assert.Equal(CallbackRejection.Malformed, malformed);
        Assert.Equal(CallbackRejection.Malformed, eventless);
        Assert.Equal(CallbackRejection.Malformed, empty);
    }

    [Fact]
    public void An_event_outside_the_delivery_window_is_stale_even_with_a_good_hash()
    {
        var json = Event(DropboxSignEventTypes.SignerSigned, "1761900000", "req_1", "sig_1");

        Assert.False(Verify(json, Key, Now + DropboxSignCallbacks.MaxAge, out _, out var old));
        Assert.False(Verify(json, Key, DateTimeOffset.FromUnixTimeSeconds(1761900000) - DropboxSignCallbacks.MaxAhead - TimeSpan.FromSeconds(1), out _, out var future));
        Assert.True(Verify(json, Key, Now + DropboxSignCallbacks.MaxAge - TimeSpan.FromMinutes(6), out _, out _));

        Assert.Equal(CallbackRejection.Stale, old);
        Assert.Equal(CallbackRejection.Stale, future);
    }

    [Fact]
    public void Ids_far_longer_than_the_vendor_issues_are_refused_rather_than_cut()
    {
        var json = Event(DropboxSignEventTypes.SignerSigned, "1761900000", new string('r', DropboxSignCallbacks.MaxIdLength + 1), "sig_1");

        Assert.False(Verify(json, Key, Now, out _, out var rejection));
        Assert.Equal(CallbackRejection.Malformed, rejection);
    }

    [Fact]
    public void The_event_hash_is_the_documented_hmac_in_hex_and_case_does_not_matter()
    {
        // HMAC-SHA256 keyed with the API key over event_time + event_type, as the vendor documents it, computed here independently.
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData("key"u8.ToArray(), "1348177752signature_request_sent"u8.ToArray()));
        var hash = DropboxSignCallbacks.EventHash("key", "1348177752", "signature_request_sent");

        Assert.Equal(expected, hash);
        var upper = Event(DropboxSignEventTypes.Sent, "1348177752", "req", null, "key").Replace(hash, hash.ToUpperInvariant(), StringComparison.Ordinal);
        Assert.True(Verify(upper, "key", DateTimeOffset.FromUnixTimeSeconds(1348177752), out _, out _));
    }

    [Fact]
    public void The_content_hash_is_base64_of_the_hex_hmac_of_the_payload_as_the_sandbox_sends_it()
    {
        // Computed here independently: HMAC-SHA256 of the json field keyed with the API key, as lowercase hex, then base64
        // (88 characters ending "=="), the form the sandbox's Content-Sha256 header carried.
        var json = Event(DropboxSignEventTypes.SignerSigned, "1761900000", "req_1", "sig_1");
        var hex = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Key), Encoding.UTF8.GetBytes(json)));

        var hash = DropboxSignCallbacks.ContentHash(Key, json);

        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes(hex)), hash);
        Assert.Equal(88, hash.Length);
    }

    [Fact]
    public void A_payload_whose_content_hash_is_missing_wrong_or_for_other_bytes_is_refused_as_unverifiable()
    {
        var json = Event(DropboxSignEventTypes.SignerSigned, "1761900000", "req_1", "sig_1");
        var rawDigest = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Key), Encoding.UTF8.GetBytes(json)));
        var altered = json.Replace("Engagement letter", "Engagement letter 2", StringComparison.Ordinal);

        Assert.False(DropboxSignCallbacks.TryVerify(json, null, Key, Now, out _, out var missing));
        Assert.False(DropboxSignCallbacks.TryVerify(json, rawDigest, Key, Now, out _, out var digestNotHex));
        Assert.False(DropboxSignCallbacks.TryVerify(altered, DropboxSignCallbacks.ContentHash(Key, json), Key, Now, out _, out var otherBytes));
        Assert.False(DropboxSignCallbacks.TryVerify(json, DropboxSignCallbacks.ContentHash("another-key", json), Key, Now, out _, out var otherKey));

        Assert.Equal(CallbackRejection.ContentHashMissing, missing);
        Assert.All([digestNotHex, otherBytes, otherKey], r => Assert.Equal(CallbackRejection.ContentHashMismatch, r));
    }

    /// <summary>Verifies <paramref name="json"/> as the vendor posts it, with its content hash under <paramref name="key"/>.</summary>
    private static bool Verify(string json, string key, DateTimeOffset now, out DropboxSignEvent? verified, out CallbackRejection rejection) =>
        DropboxSignCallbacks.TryVerify(json, DropboxSignCallbacks.ContentHash(key, json), key, now, out verified, out rejection);

    /// <summary>A callback payload as Dropbox Sign posts it, hashed with <paramref name="key"/>.</summary>
    private static string Event(string eventType, string eventTime, string requestId, string? signatureId, string key = Key) =>
        JsonSerializer.Serialize(new
        {
            @event = new
            {
                event_time = eventTime,
                event_type = eventType,
                event_hash = DropboxSignCallbacks.EventHash(key, eventTime, eventType),
                event_metadata = new { related_signature_id = signatureId, reported_for_account_id = "acc_1", reported_for_app_id = (string?)null, event_message = (string?)null },
            },
            signature_request = new
            {
                signature_request_id = requestId,
                title = "Engagement letter",
                is_complete = false,
                is_declined = false,
                has_error = false,
                metadata = new { reference = "case:x:v1" },
                signatures = new[] { new { signature_id = signatureId ?? "sig_0", signer_email_address = "alice@example.com", signer_name = "Alice Example", order = 0, status_code = "signed", signed_at = long.Parse(eventTime, CultureInfo.InvariantCulture) } },
            },
        });

    /// <summary>A <c>callback_test</c> event: no request, only the event.</summary>
    private static string TestEvent(string eventTime, string key = Key) =>
        JsonSerializer.Serialize(new { @event = new { event_time = eventTime, event_type = "callback_test", event_hash = DropboxSignCallbacks.EventHash(key, eventTime, "callback_test"), event_metadata = new { } } });
}
