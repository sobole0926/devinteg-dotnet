# DevInteg.DropboxSign

A thin [Dropbox Sign](https://developers.hellosign.com/) client for **embedded signing** in .NET 10, written as plain HTTP with no vendor SDK. It does the parts that are easy to get subtly wrong:

- Creates an embedded request. Dropbox Sign emails nobody; signers sign inside your page.
- Finds a request again by its metadata. The search's quoting rules and its roughly 80-second indexing delay are handled and documented.
- Fetches a fresh embedded sign URL, reads status, cancels (safe to repeat) and downloads the PDF (a transient failure while the file is still being prepared).
- **Verifies callbacks** with both of the vendor's checks, compared in constant time: the `Content-Sha256` header over the whole payload and the `event_hash`. A replay window is enforced as well.
- **Signing-link tokens**: opaque, HMAC-signed tokens for links you send signers yourself. They're signed with a key ring of your own, separate from the API key, so either can rotate on its own.

Failures surface as `DropboxSignException`, which carries the HTTP status, the vendor's error *name* and whether the failure is transient. It never includes the vendor's message or the response body, since those can echo names and addresses back.

## Install

```
dotnet add package DevInteg.DropboxSign
```

## Register

```csharp
builder.Services.AddOptions<DropboxSignOptions>().BindConfiguration("DropboxSign");
builder.Services.AddDropboxSignClient()
    // Retry reads only: creating a request is not idempotent at the vendor.
    .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());

builder.Services.AddOptions<SigningLinkKeyRing>().BindConfiguration("DropboxSign:SigningLinks");
builder.Services.AddDropboxSignSigningLinks();
```

`AddStandardResilienceHandler` comes from `Microsoft.Extensions.Http.Resilience`. Keep the API key and the signing-link keys out of source control: use user-secrets, the environment or a vault. Set `TestMode = true` outside production; test-mode requests are free, watermarked and not legally binding.

## Create, then sign in your page

```csharp
var id = await dropboxSign.CreateEmbeddedRequestAsync(new EmbeddedSignatureRequest(
    "Engagement letter",
    [new RequestSigner("Alice Example", "alice@example.com", Order: 1)],
    [new SignatureDocument("letter.pdf", pdfBytes)])
{
    ExpiresAt = signBy,
    Metadata = new Dictionary<string, string> { ["reference"] = "matter:42:v1" },
});

var request = await dropboxSign.GetAsync(id);
var token = signingLinks.Issue(new SigningLinkSubject(id, request.Signatures[0].Id), request.ExpiresAt);
// Email https://your.app/sign/{token} in your own voice. When the page opens:
if (signingLinks.Verify(token) is { } subject)
{
    var url = await dropboxSign.GetEmbeddedSignUrlAsync(subject.SignatureId);
    // Hand url.SignUrl and your client id to hellosign-embedded.
}
```

**Making a create safe to retry:** Dropbox Sign does not deduplicate. Put a reference in the metadata, call `FindByMetadataAsync` before creating, and remember what this process created. The search only sees a request about 80 seconds after it was created.

## Receive callbacks

```csharp
app.MapPost("/dropbox-sign/events", async (HttpRequest http, TimeProvider time, IOptions<DropboxSignOptions> options) =>
{
    var form = await http.ReadFormAsync();
    if (!DropboxSignCallbacks.TryVerify(form[DropboxSignCallbacks.PayloadField], http.Headers[DropboxSignCallbacks.ContentHashHeader],
            options.Value.ApiKey, time.GetUtcNow(), out var evt, out var rejection))
    {
        return Results.BadRequest(); // Log the rejection; never acknowledge.
    }

    // Store evt durably (deduplicate on type + time + request + signature), then acknowledge.
    return Results.Text(DropboxSignCallbacks.Acknowledgement);
});
```

Before acting on an event, re-read the request with `GetAsync`. A callback says something happened; `GetAsync` tells you where the request stands now.

## Licence

MIT. Not affiliated with or endorsed by Dropbox, Inc.
