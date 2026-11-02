using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace DevInteg.DropboxSign;

/// <summary>
/// Dropbox Sign's API for embedded signing, as thin HTTP: no vendor SDK. Every failure leaves as a
/// <see cref="DropboxSignException"/> carrying the status and the vendor's error name, never its message or body.
/// Register it with <c>AddDropboxSignClient()</c> and add a resilience handler that retries reads only: a create is not
/// idempotent at the vendor.
/// </summary>
public sealed class DropboxSignClient
{
    /// <summary>The name of the <see cref="HttpClient"/> the client is registered under.</summary>
    public const string HttpClientName = "DevInteg.DropboxSign";

    /// <summary>The most pages <see cref="FindByMetadataAsync"/> reads before it gives up.</summary>
    public const int MaxSearchPages = 10;

    private const int SearchPageSize = 100;

    private static readonly MediaTypeWithQualityHeaderValue JsonMediaType = new("application/json");

    private readonly HttpClient _http;
    private readonly DropboxSignOptions _options;
    private readonly string _credentials;

    /// <summary>Creates the client over <paramref name="http"/>; the address and key come from <paramref name="options"/>.</summary>
    public DropboxSignClient(HttpClient http, IOptions<DropboxSignOptions> options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value;
        if (!_options.IsValid())
        {
            throw new ArgumentException("Dropbox Sign needs an API key and an absolute base address ending in '/'.", nameof(options));
        }

        _credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes(_options.ApiKey + ":"));
    }

    /// <summary>
    /// Creates an embedded request and returns its id. Not idempotent: the vendor does not deduplicate, so to make a retry
    /// safe, store a reference in <see cref="EmbeddedSignatureRequest.Metadata"/> and search for it first. The search sees a
    /// new request only once it is indexed, about 80 seconds after creation, so remember what you created in between.
    /// </summary>
    public async Task<string> CreateEmbeddedRequestAsync(EmbeddedSignatureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(_options.ClientId))
        {
            throw new InvalidOperationException("An embedded request needs DropboxSignOptions.ClientId.");
        }

        if (request.Signers.Count == 0 || request.Signers.Select(s => s.Order).Distinct().Count() != request.Signers.Count)
        {
            throw new ArgumentException("A request needs at least one signer, with distinct orders.", nameof(request));
        }

        if (request.Files.Count == 0)
        {
            throw new ArgumentException("A request needs at least one file.", nameof(request));
        }

        using var form = new MultipartFormDataContent();
        form.Add(Text(_options.ClientId), "client_id");
        form.Add(Text(_options.TestMode ? "1" : "0"), "test_mode");
        form.Add(Text(request.Title), "title");
        form.Add(Text(request.AllowDecline ? "1" : "0"), "allow_decline");
        if (request.ExpiresAt is { } expiresAt)
        {
            form.Add(Text(expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)), "expires_at");
        }

        foreach (var (key, value) in request.Metadata)
        {
            form.Add(Text(value), $"metadata[{key}]");
        }

        var order = 0;
        foreach (var signer in request.Signers.OrderBy(s => s.Order))
        {
            form.Add(Text(signer.Name), $"signers[{order}][name]");
            form.Add(Text(signer.EmailAddress), $"signers[{order}][email_address]");
            form.Add(Text(order.ToString(CultureInfo.InvariantCulture)), $"signers[{order}][order]");
            order++;
        }

        for (var i = 0; i < request.Files.Count; i++)
        {
            var document = request.Files[i];
            var file = new ReadOnlyMemoryContent(document.Content);
            file.Headers.ContentType = new MediaTypeHeaderValue(document.ContentType);
            form.Add(file, $"files[{i}]", document.FileName);
        }

        using var response = await SendAsync(HttpMethod.Post, "signature_request/create_embedded", form, cancellationToken).ConfigureAwait(false);
        var created = await ReadAsync<SignatureRequestEnvelope>(response, "create", cancellationToken).ConfigureAwait(false);
        var id = created.SignatureRequest?.SignatureRequestId;
        return string.IsNullOrWhiteSpace(id)
            ? throw new DropboxSignException("create", (int)response.StatusCode, "missingSignatureRequestId", transient: false)
            : id;
    }

    /// <summary>
    /// The id of a request whose metadata holds <paramref name="key"/> with exactly <paramref name="value"/>, or null. Searched
    /// as a quoted phrase, since the vendor's parser reads a bare colon as a field separator; a value with a quote or a
    /// backslash is refused because how the parser escapes one is unknown. Finds a request only once the vendor has indexed it.
    /// </summary>
    public async Task<string?> FindByMetadataAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.AsSpan().IndexOfAny('"', '\\') >= 0)
        {
            throw new ArgumentException("A searched metadata value may not hold a quote or a backslash.", nameof(value));
        }

        var query = Uri.EscapeDataString($"metadata:\"{value}\"");
        for (var page = 1; page <= MaxSearchPages; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, $"signature_request/list?query={query}&page={page}&page_size={SearchPageSize}", null, cancellationToken).ConfigureAwait(false);
            var list = await ReadAsync<SignatureRequestListEnvelope>(response, "find", cancellationToken).ConfigureAwait(false);
            var match = list.SignatureRequests?.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.SignatureRequestId) && HasMetadata(r, key, value));
            if (match is not null)
            {
                return match.SignatureRequestId;
            }

            if (list.ListInfo is null || list.ListInfo.NumPages <= page)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The request as the vendor reports it now.</summary>
    public async Task<SignatureRequest> GetAsync(string requestId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        using var response = await SendAsync(HttpMethod.Get, $"signature_request/{Uri.EscapeDataString(requestId)}", null, cancellationToken).ConfigureAwait(false);
        var envelope = await ReadAsync<SignatureRequestEnvelope>(response, "get", cancellationToken).ConfigureAwait(false);
        return envelope.SignatureRequest is { SignatureRequestId: { Length: > 0 } } wire
            ? Map(wire)
            : throw new DropboxSignException("get", (int)response.StatusCode, "missingSignatureRequest", transient: false);
    }

    /// <summary>A fresh URL for one signer's embedded page, by the <see cref="Signature.Id"/>. The URL expires within the hour; fetch one per page load.</summary>
    public async Task<EmbeddedSignUrl> GetEmbeddedSignUrlAsync(string signatureId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signatureId);
        using var response = await SendAsync(HttpMethod.Get, $"embedded/sign_url/{Uri.EscapeDataString(signatureId)}", null, cancellationToken).ConfigureAwait(false);
        var embedded = await ReadAsync<EmbeddedEnvelope>(response, "signUrl", cancellationToken).ConfigureAwait(false);
        if (embedded.Embedded?.SignUrl is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var signUrl))
        {
            throw new DropboxSignException("signUrl", (int)response.StatusCode, "missingSignUrl", transient: false);
        }

        return new EmbeddedSignUrl(signUrl, FromUnix(embedded.Embedded.ExpiresAt));
    }

    /// <summary>
    /// Cancels the request; nobody can sign it afterwards. A request that is already over or unknown answers
    /// <see cref="CancelResult.Canceled"/> false rather than throwing, so cancelling twice is safe. An outage throws.
    /// </summary>
    public async Task<CancelResult> CancelAsync(string requestId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        using var response = await SendAsync(HttpMethod.Post, $"signature_request/cancel/{Uri.EscapeDataString(requestId)}", null, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return new CancelResult(Canceled: true, (int)response.StatusCode, ErrorName: null);
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Gone)
        {
            return new CancelResult(Canceled: false, (int)response.StatusCode, await ErrorNameAsync(response, cancellationToken).ConfigureAwait(false));
        }

        throw await FailureAsync(response, "cancel", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The request's documents as one PDF. The vendor serves a partly signed file as readily as a complete one: check
    /// <see cref="SignatureRequest.IsComplete"/> first when you need the executed copy. While the vendor is still preparing
    /// the file after the last signature, this throws a transient <c>fileNotReady</c>.
    /// </summary>
    public async Task<byte[]> DownloadPdfAsync(string requestId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        using var response = await SendAsync(HttpMethod.Get, $"signature_request/files/{Uri.EscapeDataString(requestId)}?file_type=pdf", null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new DropboxSignException("download", 409, "fileNotReady", transient: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, "download", cancellationToken).ConfigureAwait(false);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return bytes.Length == 0
            ? throw new DropboxSignException("download", (int)response.StatusCode, "emptyFile", transient: true)
            : bytes;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_options.BaseAddress, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _credentials);
        request.Headers.Accept.Add(JsonMediaType);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static SignatureRequest Map(SignatureRequestWire wire)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in wire.Metadata ?? [])
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                metadata[key] = value.GetString()!;
            }
        }

        var signatures = (wire.Signatures ?? [])
            .Where(s => s.SignatureId is { Length: > 0 } && s.SignerEmailAddress is { Length: > 0 })
            .OrderBy(s => s.Order ?? int.MaxValue)
            .Select(s => new Signature(s.SignatureId!, s.SignerEmailAddress!, s.SignerName, s.Order, s.StatusCode, FromUnix(s.SignedAt)))
            .ToList();
        return new SignatureRequest(wire.SignatureRequestId!, wire.IsComplete, wire.IsDeclined, wire.HasError, FromUnix(wire.ExpiresAt), metadata, signatures);
    }

    private static bool HasMetadata(SignatureRequestWire request, string key, string value) =>
        request.Metadata is { } metadata
        && metadata.TryGetValue(key, out var found)
        && found.ValueKind == JsonValueKind.String
        && string.Equals(found.GetString(), value, StringComparison.Ordinal);

    private static DateTimeOffset? FromUnix(long? seconds) => seconds is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    private static StringContent Text(string value) => new(value, Encoding.UTF8);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, operation, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(DropboxSignJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new DropboxSignException(operation, (int)response.StatusCode, "emptyResponse", transient: false);
        }
        catch (JsonException malformed)
        {
            throw new DropboxSignException(operation, (int)response.StatusCode, "malformedResponse", transient: false, malformed);
        }
    }

    private static async Task<DropboxSignException> FailureAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var transient = status is 408 or 409 or 425 or 429 or >= 500;
        return new DropboxSignException(operation, status, await ErrorNameAsync(response, cancellationToken).ConfigureAwait(false), transient);
    }

    /// <summary>The vendor's error name from its body, never its message; null when the body says nothing usable.</summary>
    private static async Task<string?> ErrorNameAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length == 0 || body.Length > 64 * 1024)
            {
                return null;
            }

            var name = JsonSerializer.Deserialize<ErrorEnvelope>(body, DropboxSignJson.Options)?.Error?.ErrorName;
            return string.IsNullOrWhiteSpace(name) ? null : name.Length <= 64 ? name : name[..64];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
