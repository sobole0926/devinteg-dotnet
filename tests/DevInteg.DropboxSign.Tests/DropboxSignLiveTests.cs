using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Xunit;

namespace DevInteg.DropboxSign.Tests;

/// <summary>
/// The client against Dropbox Sign's sandbox, on demand only: set <c>DROPBOX_SIGN_LIVE=1</c>, <c>DROPBOX_SIGN_API_KEY</c>
/// and <c>DROPBOX_SIGN_CLIENT_ID</c>. Creates a test-mode embedded request, finds it by its metadata once indexed (about
/// 80 s), fetches a sign URL, reads it and cancels it. Nobody signs. Never runs on a pull request.
/// </summary>
public sealed class DropboxSignLiveTests
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("DROPBOX_SIGN_LIVE") == "1"
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DROPBOX_SIGN_API_KEY"))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DROPBOX_SIGN_CLIENT_ID"));

    [Fact(Skip = "Calls Dropbox Sign's sandbox on demand only: DROPBOX_SIGN_LIVE=1 with the key and client id set.", SkipUnless = nameof(Enabled))]
    public async Task A_test_mode_request_is_created_found_linked_read_and_cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        var client = new DropboxSignClient(http, Options.Create(new DropboxSignOptions
        {
            ApiKey = Environment.GetEnvironmentVariable("DROPBOX_SIGN_API_KEY")!,
            ClientId = Environment.GetEnvironmentVariable("DROPBOX_SIGN_CLIENT_ID")!,
            TestMode = true,
        }));
        var reference = $"live:{Guid.CreateVersion7():N}";
        var request = new EmbeddedSignatureRequest(
            "DevInteg.DropboxSign live check (test mode)",
            [new RequestSigner("Test Signer One", "signer.one@example.com", 1), new RequestSigner("Test Signer Two", "signer.two@example.com", 2)],
            [new SignatureDocument("check.pdf", MinimalPdf())])
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["reference"] = reference },
        };

        var id = await client.CreateEmbeddedRequestAsync(request, ct);
        try
        {
            Assert.Equal(id, await FoundOnceIndexedAsync(client, reference, ct));
            var read = await client.GetAsync(id, ct);
            Assert.Equal(["signer.one@example.com", "signer.two@example.com"], read.Signatures.Select(s => s.SignerEmailAddress));
            Assert.False(read.IsComplete);
            Assert.Equal(reference, read.Metadata["reference"]);
            var url = await client.GetEmbeddedSignUrlAsync(read.Signatures[0].Id, ct);
            Assert.Equal("app.hellosign.com", url.SignUrl.Host);
        }
        finally
        {
            Assert.True((await client.CancelAsync(id, ct)).Canceled);
        }
    }

    /// <summary>The vendor's search sees a new request only once it is indexed: about 80 s in the sandbox. Searches every 5 s for up to 3 minutes.</summary>
    private static async Task<string?> FoundOnceIndexedAsync(DropboxSignClient client, string reference, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        var found = await client.FindByMetadataAsync("reference", reference, ct);
        while (found is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            found = await client.FindByMetadataAsync("reference", reference, ct);
        }

        return found;
    }

    /// <summary>The smallest PDF the vendor accepts: one empty page.</summary>
    private static byte[] MinimalPdf() => System.Text.Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>endobj\nxref\n0 4\n0000000000 65535 f \n0000000009 00000 n \n0000000052 00000 n \n0000000101 00000 n \ntrailer<</Size 4/Root 1 0 R>>\nstartxref\n168\n%%EOF\n");
}
