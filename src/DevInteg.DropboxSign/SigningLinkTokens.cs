using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DevInteg.DropboxSign;

/// <summary>The request and signature a signing-link token stands for.</summary>
public sealed record SigningLinkSubject(string SignatureRequestId, string SignatureId);

/// <summary>
/// The keys signing-link tokens are signed with, apart from the vendor's API key so either rotates alone. Keep the keys
/// out of source control. To rotate: add a key, make it active, and remove the old one once its last token has expired.
/// </summary>
public sealed class SigningLinkKeyRing
{
    /// <summary>The shortest key accepted, in bytes.</summary>
    public const int MinKeyBytes = 32;

    /// <summary>The key new tokens are signed with; must name one of <see cref="Keys"/>.</summary>
    public string ActiveKeyId { get; set; } = string.Empty;

    /// <summary>Key id to base64 key. Ids are short <c>[A-Za-z0-9-]</c>, since they travel in the token.</summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Bound from configuration.")]
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>A token's lifetime when the request reports no expiry: 1 to 365 days.</summary>
    public int MaxLifetimeDays { get; set; } = 60;

    /// <summary>The ring can issue and verify: an active key it holds, every id well formed, every key base64 of at least <see cref="MinKeyBytes"/> bytes.</summary>
    public bool IsValid() =>
        MaxLifetimeDays is >= 1 and <= 365
        && Keys.Count > 0
        && Keys.ContainsKey(ActiveKeyId)
        && Keys.All(key => IsKeyId(key.Key) && DecodeKey(key.Value) is { Length: >= MinKeyBytes });

    /// <summary>A key id of 1 to 32 ASCII letters, digits or hyphens.</summary>
    public static bool IsKeyId(string? id) =>
        id is { Length: > 0 and <= 32 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>The key's bytes, or null when it is not base64.</summary>
    public static byte[]? DecodeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// The opaque token in a signing link you send a signer yourself: <c>{keyId}.{payload}.{mac}</c>, where the payload is
/// the request and signature ids and an expiry, and the mac HMAC-SHA256 over <c>{keyId}.{payload}</c> with a key from
/// the <see cref="SigningLinkKeyRing"/>. Your signing page posts the token back, you verify it, and you fetch a fresh
/// <see cref="EmbeddedSignUrl"/> for the subject. A token verifies only while its key is in the ring and its expiry has
/// not passed. It carries no name or address, since links end up in mailboxes, logs and browser history; it is not
/// single-use, so a signer can come back to finish.
/// </summary>
public sealed class SigningLinkTokens
{
    /// <summary>The longest token <see cref="Verify"/> reads.</summary>
    public const int MaxLength = 512;

    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _activeKeyId;
    private readonly TimeSpan _maxLifetime;
    private readonly TimeProvider _time;

    /// <summary>Creates the issuer and verifier over a valid <paramref name="ring"/>.</summary>
    public SigningLinkTokens(SigningLinkKeyRing ring, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(time);
        if (!ring.IsValid())
        {
            throw new ArgumentException("The signing-link key ring is not valid.", nameof(ring));
        }

        _keys = ring.Keys.ToDictionary(key => key.Key, key => SigningLinkKeyRing.DecodeKey(key.Value)!, StringComparer.Ordinal);
        _activeKeyId = ring.ActiveKeyId;
        _maxLifetime = TimeSpan.FromDays(ring.MaxLifetimeDays);
        _time = time;
    }

    /// <summary>A token for <paramref name="subject"/>, signed with the active key.</summary>
    /// <param name="subject">The request and signature the link opens.</param>
    /// <param name="requestExpiresAt">The request's expiry; the token expires with it, or after the ring's maximum lifetime when there is none.</param>
    public string Issue(SigningLinkSubject subject, DateTimeOffset? requestExpiresAt)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject.SignatureRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject.SignatureId);
        if (subject.SignatureRequestId.Contains(':', StringComparison.Ordinal) || subject.SignatureId.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Dropbox Sign ids never contain ':'.", nameof(subject));
        }

        var expiresAt = requestExpiresAt ?? _time.GetUtcNow().Add(_maxLifetime);
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            $"{subject.SignatureRequestId}:{subject.SignatureId}:{expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}"));
        return $"{_activeKeyId}.{payload}.{Base64Url.EncodeToString(Mac(_keys[_activeKeyId], _activeKeyId, payload))}";
    }

    /// <summary>The subject a token names, or null when it was not issued under a key in the ring, was altered, or has expired.</summary>
    public SigningLinkSubject? Verify(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxLength)
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || !_keys.TryGetValue(parts[0], out var key) || parts[1].Length == 0)
        {
            return null;
        }

        byte[] presented;
        byte[] payload;
        try
        {
            presented = Base64Url.DecodeFromChars(parts[2]);
            payload = Base64Url.DecodeFromChars(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(Mac(key, parts[0], parts[1]), presented))
        {
            return null;
        }

        var fields = Encoding.UTF8.GetString(payload).Split(':');
        if (fields.Length != 3 || fields[0].Length == 0 || fields[1].Length == 0
            || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expires))
        {
            return null;
        }

        return _time.GetUtcNow().ToUnixTimeSeconds() < expires ? new SigningLinkSubject(fields[0], fields[1]) : null;
    }

    private static byte[] Mac(byte[] key, string keyId, string payload) =>
        HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{keyId}.{payload}"));
}
