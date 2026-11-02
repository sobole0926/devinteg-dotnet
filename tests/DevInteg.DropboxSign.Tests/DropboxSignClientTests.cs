using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Xunit;

namespace DevInteg.DropboxSign.Tests;

/// <summary>
/// The client's wire shape, captured at the HTTP boundary over the sandbox stub: an embedded request with the signers in
/// order, basic authentication, the caller's metadata and expiry, and nothing the vendor would use to email a signer.
/// </summary>
public sealed class DropboxSignClientTests : IDisposable
{
    private const string Reference = "case:0199a0c4000070008000000000000001:v1";
    private const string Alice = "alice@example.com";
    private const string Bob = "bob@example.com";

    private static readonly DateTimeOffset ExpiresAt = new(2026, 11, 17, 5, 59, 59, TimeSpan.Zero);

    private readonly DropboxSignStub _stub = new();

    public void Dispose() => _stub.Dispose();

    private static EmbeddedSignatureRequest Request() =>
        new("Engagement letter", [new RequestSigner("Bob Example", Bob, 2), new RequestSigner("Alice Example", Alice, 1)], [new SignatureDocument("letter.pdf", "%PDF-1.7 approved draft"u8.ToArray())])
        {
            ExpiresAt = ExpiresAt,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["reference"] = Reference },
        };

    [Fact]
    public async Task A_create_is_one_embedded_request_with_the_signers_in_order_and_no_vendor_email()
    {
        var id = await _stub.Client().CreateEmbeddedRequestAsync(Request(), TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(id));
        var create = Assert.Single(_stub.Sent);
        Assert.Equal(HttpMethod.Post, create.Method);
        Assert.Equal("/v3/signature_request/create_embedded", create.PathAndQuery);
        Assert.Equal(["Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(DropboxSignStub.ApiKey + ":"))], create.Headers["Authorization"]);
        Assert.Equal(DropboxSignStub.ClientId, create.Fields["client_id"]);
        Assert.Equal("1", create.Fields["test_mode"]);
        Assert.Equal("1", create.Fields["allow_decline"]);
        Assert.Equal("Engagement letter", create.Fields["title"]);
        Assert.Equal("Alice Example", create.Fields["signers[0][name]"]);
        Assert.Equal(Alice, create.Fields["signers[0][email_address]"]);
        Assert.Equal("0", create.Fields["signers[0][order]"]);
        Assert.Equal(Bob, create.Fields["signers[1][email_address]"]);
        Assert.Equal("1", create.Fields["signers[1][order]"]);
        Assert.Equal("23", create.Fields["files[0]"]);
        Assert.Equal(Reference, create.Fields["metadata[reference]"]);
        Assert.Equal(ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), create.Fields["expires_at"]);
        foreach (var forbidden in new[] { "subject", "message", "cc_email_addresses", "signing_redirect_url", "use_text_tags" })
        {
            Assert.False(create.Fields.ContainsKey(forbidden), $"{forbidden} must not be sent");
        }
    }

    [Fact]
    public async Task A_create_outside_test_mode_says_so_and_one_without_an_expiry_sends_none()
    {
        await _stub.Client(o => o.TestMode = false).CreateEmbeddedRequestAsync(Request() with { ExpiresAt = null, AllowDecline = false }, TestContext.Current.CancellationToken);

        var create = Assert.Single(_stub.Sent);
        Assert.Equal("0", create.Fields["test_mode"]);
        Assert.Equal("0", create.Fields["allow_decline"]);
        Assert.False(create.Fields.ContainsKey("expires_at"));
    }

    [Fact]
    public async Task A_create_without_signers_files_distinct_orders_or_a_client_id_is_refused_before_sending()
    {
        var client = _stub.Client();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateEmbeddedRequestAsync(Request() with { Signers = [] }, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateEmbeddedRequestAsync(Request() with { Files = [] }, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateEmbeddedRequestAsync(Request() with { Signers = [new RequestSigner("A", Alice, 1), new RequestSigner("B", Bob, 1)] }, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _stub.Client(o => o.ClientId = "").CreateEmbeddedRequestAsync(Request(), ct));
        Assert.Empty(_stub.Sent);
    }

    [Fact]
    public async Task A_request_is_found_by_its_metadata_only_once_indexed_and_by_the_exact_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = _stub.Client();
        var id = await client.CreateEmbeddedRequestAsync(Request(), ct);

        Assert.Null(await client.FindByMetadataAsync("reference", Reference, ct));
        _stub.Index();

        Assert.Equal(id, await client.FindByMetadataAsync("reference", Reference, ct));
        Assert.Null(await client.FindByMetadataAsync("other", Reference, ct));
        Assert.Null(await client.FindByMetadataAsync("reference", Reference + "0", ct));
        Assert.Contains(_stub.Sent, s => s.PathAndQuery == "/v3/signature_request/list?query=metadata%3A%22case%3A0199a0c4000070008000000000000001%3Av1%22&page=1&page_size=100");
    }

    [Fact]
    public async Task A_searched_value_with_a_quote_or_backslash_is_refused_before_the_search()
    {
        var client = _stub.Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.FindByMetadataAsync("reference", "case:\"x", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.FindByMetadataAsync("reference", @"case:\x", TestContext.Current.CancellationToken));
        Assert.Empty(_stub.Sent);
    }

    [Fact]
    public async Task The_request_carries_its_signatures_in_order_its_metadata_its_expiry_and_a_decline()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = _stub.Client();
        var id = await client.CreateEmbeddedRequestAsync(Request(), ct);
        var declined = _stub.Decline(id, Alice);

        var request = await client.GetAsync(id, ct);

        Assert.Equal(id, request.Id);
        Assert.True(request.IsDeclined);
        Assert.False(request.IsComplete);
        Assert.Equal(ExpiresAt, request.ExpiresAt);
        Assert.Equal(Reference, request.Metadata["reference"]);
        Assert.Equal([Alice, Bob], request.Signatures.Select(s => s.SignerEmailAddress));
        var alice = request.Signatures[0];
        Assert.Equal(declined.Id, alice.Id);
        Assert.True(alice.IsDeclined);
        Assert.Null(alice.SignedAt);
    }

    [Fact]
    public async Task A_sign_url_is_fetched_fresh_by_signature_id_and_a_cancelled_request_has_none()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = _stub.Client();
        var id = await client.CreateEmbeddedRequestAsync(Request(), ct);
        var signature = (await client.GetAsync(id, ct)).Signatures[0];

        var url = await client.GetEmbeddedSignUrlAsync(signature.Id, ct);
        await client.CancelAsync(id, ct);
        var refused = await Assert.ThrowsAsync<DropboxSignException>(() => client.GetEmbeddedSignUrlAsync(signature.Id, ct));

        Assert.Contains($"signature_id={signature.Id}", url.SignUrl.Query, StringComparison.Ordinal);
        Assert.NotNull(url.ExpiresAt);
        Assert.False(refused.IsTransient);
        Assert.Equal("not_found", refused.ErrorName);
        Assert.Equal("signUrl", refused.Operation);
    }

    [Fact]
    public async Task A_second_cancel_and_an_unknown_cancel_report_nothing_cancelled_but_an_outage_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = _stub.Client();
        var id = await client.CreateEmbeddedRequestAsync(Request(), ct);

        var first = await client.CancelAsync(id, ct);
        var second = await client.CancelAsync(id, ct);
        var unknown = await client.CancelAsync("stub-unknown", ct);
        _stub.Outage = HttpStatusCode.ServiceUnavailable;
        var failed = await Assert.ThrowsAsync<DropboxSignException>(() => client.CancelAsync(id, ct));

        Assert.True(first.Canceled);
        Assert.Equal(new CancelResult(false, 409, "conflict"), second);
        Assert.Equal(new CancelResult(false, 404, "not_found"), unknown);
        Assert.True(failed.IsTransient);
        Assert.Equal(503, failed.Status);
        Assert.Equal("unknown", failed.ErrorName);
        Assert.DoesNotContain("Stub outage", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_file_is_a_transient_failure_while_the_vendor_prepares_it()
    {
        var ct = TestContext.Current.CancellationToken;
        _stub.DownloadsNotReady = 1;
        var client = _stub.Client();
        var id = await client.CreateEmbeddedRequestAsync(Request(), ct);
        Assert.True(_stub.Sign(id, Alice));
        Assert.True(_stub.Sign(id, Bob));

        var notReady = await Assert.ThrowsAsync<DropboxSignException>(() => client.DownloadPdfAsync(id, ct));
        var file = await client.DownloadPdfAsync(id, ct);

        Assert.True(notReady.IsTransient);
        Assert.Equal("fileNotReady", notReady.ErrorName);
        Assert.Contains($"signed by {Alice}, {Bob}", Encoding.ASCII.GetString(file), StringComparison.Ordinal);
        Assert.True((await client.GetAsync(id, ct)).IsComplete);
    }

    [Fact]
    public async Task A_wrong_key_is_a_permanent_failure_that_keeps_the_vendors_message_out()
    {
        var failed = await Assert.ThrowsAsync<DropboxSignException>(() => _stub.Client(o => o.ApiKey = "another-key").CreateEmbeddedRequestAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(401, failed.Status);
        Assert.False(failed.IsTransient);
        Assert.Equal("unauthorized", failed.ErrorName);
        Assert.Equal("create", failed.Operation);
        Assert.DoesNotContain("Bad key", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_that_is_not_the_vendors_json_is_a_malformed_response()
    {
        using var handler = new FixedResponse(HttpStatusCode.OK, "<html>maintenance</html>");
        var client = new DropboxSignClient(new HttpClient(handler), Options.Create(DropboxSignStub.Options()));

        var failed = await Assert.ThrowsAsync<DropboxSignException>(() => client.GetAsync("req_1", TestContext.Current.CancellationToken));

        Assert.Equal("malformedResponse", failed.ErrorName);
        Assert.False(failed.IsTransient);
    }

    [Theory]
    [InlineData("", "https://api.hellosign.com/v3/")]
    [InlineData("key", "https://api.hellosign.com/v3")]
    [InlineData("key", "/v3/")]
    public void Options_without_a_key_or_with_a_base_address_that_relative_paths_would_escape_are_refused(string apiKey, string baseAddress)
    {
        var options = new DropboxSignOptions { ApiKey = apiKey, BaseAddress = new Uri(baseAddress, UriKind.RelativeOrAbsolute) };

        Assert.False(options.IsValid());
        Assert.Throws<ArgumentException>(() => new DropboxSignClient(new HttpClient(), Options.Create(options)));
    }

    private sealed class FixedResponse(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") });
    }
}
