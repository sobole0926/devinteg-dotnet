using System;
using System.Linq;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DevInteg.DropboxSign.Tests;

/// <summary>A link token names the request and signature, proves it was issued under a key of the ring, expires with the request, and says nothing about anyone.</summary>
public sealed class SigningLinkTokensTests
{
    private const string KeyOne = "dGVzdC1zaWduaW5nLWxpbmsta2V5LW5ldmVyLXVzZWQh";
    private const string KeyTwo = "c2Vjb25kLXNpZ25pbmctbGluay1rZXktbmV2ZXItdXNlZA==";

    private static readonly SigningLinkSubject Subject = new("req_abc", "sig_123");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RequestExpiry = new(2026, 11, 17, 6, 0, 0, TimeSpan.Zero);

    private static SigningLinkKeyRing Ring(string active, params (string Id, string Key)[] keys) => new()
    {
        ActiveKeyId = active,
        Keys = keys.ToDictionary(k => k.Id, k => k.Key, StringComparer.Ordinal),
    };

    [Fact]
    public void A_token_round_trips_and_a_tampered_or_foreign_one_does_not()
    {
        var tokens = new SigningLinkTokens(Ring("one", ("one", KeyOne)), new FakeTimeProvider(Now));

        var token = tokens.Issue(Subject, RequestExpiry);

        Assert.Equal(Subject, tokens.Verify(token));
        Assert.StartsWith("one.", token, StringComparison.Ordinal);
        Assert.DoesNotContain("req_abc", token, StringComparison.Ordinal);
        Assert.Null(tokens.Verify(token[..^1]));
        Assert.Null(tokens.Verify(token.Replace('.', '-')));
        Assert.Null(tokens.Verify(string.Empty));
        Assert.Null(tokens.Verify(null));
        Assert.Null(tokens.Verify(new string('a', SigningLinkTokens.MaxLength + 1)));
        Assert.Throws<ArgumentException>(() => tokens.Issue(new SigningLinkSubject("a:b", "c"), RequestExpiry));
    }

    [Fact]
    public void A_token_under_a_key_the_ring_does_not_hold_or_under_the_wrong_key_verifies_to_nothing()
    {
        var clock = new FakeTimeProvider(Now);
        var token = new SigningLinkTokens(Ring("one", ("one", KeyOne)), clock).Issue(Subject, RequestExpiry);

        Assert.Null(new SigningLinkTokens(Ring("two", ("two", KeyTwo)), clock).Verify(token));
        // Same key id, different key: the mac does not verify.
        Assert.Null(new SigningLinkTokens(Ring("one", ("one", KeyTwo)), clock).Verify(token));
        // Swapping the key id for another configured key does not help either.
        Assert.Null(new SigningLinkTokens(Ring("one", ("one", KeyOne), ("two", KeyTwo)), clock).Verify("two" + token[3..]));
    }

    [Fact]
    public void A_rotated_out_key_still_verifies_its_tokens_while_the_ring_holds_it()
    {
        var clock = new FakeTimeProvider(Now);
        var before = new SigningLinkTokens(Ring("one", ("one", KeyOne)), clock).Issue(Subject, RequestExpiry);

        var rotated = new SigningLinkTokens(Ring("two", ("one", KeyOne), ("two", KeyTwo)), clock);
        var after = rotated.Issue(Subject, RequestExpiry);

        Assert.Equal(Subject, rotated.Verify(before));
        Assert.StartsWith("two.", after, StringComparison.Ordinal);
        Assert.Equal(Subject, rotated.Verify(after));
        // Once the old key is removed, its tokens no longer open anything.
        Assert.Null(new SigningLinkTokens(Ring("two", ("two", KeyTwo)), clock).Verify(before));
    }

    [Fact]
    public void A_token_expires_with_the_request_or_after_the_maximum_lifetime()
    {
        var clock = new FakeTimeProvider(Now);
        var ring = Ring("one", ("one", KeyOne));
        var tokens = new SigningLinkTokens(ring, clock);
        var withRequestExpiry = tokens.Issue(Subject, RequestExpiry);
        var withoutRequestExpiry = tokens.Issue(Subject, requestExpiresAt: null);

        clock.SetUtcNow(RequestExpiry.AddSeconds(-1));
        Assert.Equal(Subject, tokens.Verify(withRequestExpiry));
        clock.SetUtcNow(RequestExpiry);
        Assert.Null(tokens.Verify(withRequestExpiry));

        Assert.Equal(Subject, tokens.Verify(withoutRequestExpiry));
        clock.SetUtcNow(Now.AddDays(ring.MaxLifetimeDays));
        Assert.Null(tokens.Verify(withoutRequestExpiry));
    }

    [Theory]
    [InlineData("", "one", KeyOne)]
    [InlineData("two", "one", KeyOne)]
    [InlineData("one", "one", "c2hvcnQ=")]
    [InlineData("one", "one", "not base64!")]
    [InlineData("o.ne", "o.ne", KeyOne)]
    public void An_invalid_ring_is_refused(string active, string id, string key)
    {
        var ring = Ring(active, (id, key));

        Assert.False(ring.IsValid());
        Assert.Throws<ArgumentException>(() => new SigningLinkTokens(ring, new FakeTimeProvider()));
    }
}
