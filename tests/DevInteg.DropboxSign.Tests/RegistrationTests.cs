using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Xunit;

namespace DevInteg.DropboxSign.Tests;

/// <summary>The registration as a consumer composes it: options validated, and a resilience handler that retries reads but never a create.</summary>
public sealed class RegistrationTests
{
    [Fact]
    public async Task With_the_recommended_handler_a_read_is_retried_and_a_create_is_sent_once()
    {
        using var stub = new DropboxSignStub { Outage = HttpStatusCode.ServiceUnavailable };
        var services = new ServiceCollection();
        services.Configure<DropboxSignOptions>(o =>
        {
            o.ApiKey = DropboxSignStub.ApiKey;
            o.ClientId = DropboxSignStub.ClientId;
            o.TestMode = true;
            o.BaseAddress = new Uri("https://dropbox-sign.invalid/v3/");
        });
        services.AddDropboxSignClient()
            .ConfigurePrimaryHttpMessageHandler(() => stub)
            .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());
        services.Configure<HttpStandardResilienceOptions>($"{DropboxSignClient.HttpClientName}-standard", o =>
        {
            o.Retry.Delay = TimeSpan.FromMilliseconds(1);
            o.Retry.UseJitter = false;
        });
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<DropboxSignClient>();
        var request = new EmbeddedSignatureRequest("Letter", [new RequestSigner("Alice Example", "alice@example.com", 1)], [new SignatureDocument("letter.pdf", "%PDF-1.7"u8.ToArray())]);

        var read = await Assert.ThrowsAsync<DropboxSignException>(() => client.GetAsync("stub-1", TestContext.Current.CancellationToken));
        var reads = stub.Sent.Count;
        stub.Sent.Clear();
        var create = await Assert.ThrowsAsync<DropboxSignException>(() => client.CreateEmbeddedRequestAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(503, read.Status);
        Assert.True(read.IsTransient);
        Assert.True(reads > 1, $"a read should be retried; it was sent {reads} time(s)");
        Assert.Equal(503, create.Status);
        Assert.Equal(1, stub.Sent.Count(s => s.PathAndQuery == "/v3/signature_request/create_embedded"));
        Assert.Equal(["Authorization"], stub.Sent.SelectMany(s => s.Headers.Keys).Where(k => k == "Authorization").Distinct());
    }

    [Fact]
    public void Options_without_a_key_fail_at_startup_validation()
    {
        var services = new ServiceCollection();
        services.AddDropboxSignClient();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<DropboxSignOptions>>().Value);
    }

    [Fact]
    public void Signing_links_resolve_over_a_configured_ring_and_an_empty_ring_fails_validation()
    {
        var configured = new ServiceCollection();
        configured.Configure<SigningLinkKeyRing>(r =>
        {
            r.ActiveKeyId = "one";
            r.Keys["one"] = Convert.ToBase64String(new byte[32]);
        });
        configured.AddDropboxSignSigningLinks();
        using (var provider = configured.BuildServiceProvider())
        {
            var tokens = provider.GetRequiredService<SigningLinkTokens>();
            Assert.Equal(new SigningLinkSubject("req", "sig"), tokens.Verify(tokens.Issue(new SigningLinkSubject("req", "sig"), null)));
        }

        var empty = new ServiceCollection().AddDropboxSignSigningLinks();
        using var emptyProvider = empty.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => emptyProvider.GetRequiredService<SigningLinkTokens>());
    }
}
