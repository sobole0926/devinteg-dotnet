using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace DevInteg.DropboxSign.Tests;

/// <summary>One request the client sent, captured at the HTTP boundary: method, path, headers and the multipart fields (files as their byte counts).</summary>
public sealed record SentRequest(HttpMethod Method, string PathAndQuery, IReadOnlyDictionary<string, IReadOnlyList<string>> Headers, IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// Dropbox Sign's sandbox as far as the client uses it, in memory: embedded requests with ordered signers, search by
/// metadata, embedded sign URLs, cancel, status and the file (409 until prepared). Signing happens through
/// <see cref="Sign"/>, as a signer would in the embedded page. No network.
/// </summary>
public sealed class DropboxSignStub : HttpMessageHandler
{
    public const string ApiKey = "test-api-key-never-sent";

    public const string ClientId = "0123456789abcdef0123456789abcdef";

    private readonly ConcurrentDictionary<string, Request> _requests = new(StringComparer.Ordinal);
    private int _next;

    public List<SentRequest> Sent { get; } = [];

    /// <summary>How many downloads answer 409 (the file still being prepared) before the file comes.</summary>
    public int DownloadsNotReady { get; set; }

    /// <summary>Every request answers this status instead (an outage).</summary>
    public HttpStatusCode? Outage { get; set; }

    public IReadOnlyCollection<Request> Requests => [.. _requests.Values];

    /// <summary>The API key the sandbox accepts; a test rotates it.</summary>
    public string AcceptedKey { get; set; } = ApiKey;

    public static DropboxSignOptions Options(Action<DropboxSignOptions>? configure = null)
    {
        var options = new DropboxSignOptions
        {
            ApiKey = ApiKey,
            ClientId = ClientId,
            TestMode = true,
            BaseAddress = new Uri("https://dropbox-sign.invalid/v3/"),
        };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>The client over this stub.</summary>
    public DropboxSignClient Client(Action<DropboxSignOptions>? configure = null) =>
        new(new HttpClient(this, disposeHandler: false), Microsoft.Extensions.Options.Options.Create(Options(configure)));

    /// <summary>The search catches up: every request created so far becomes findable, as the real one does after about 80 s.</summary>
    public void Index()
    {
        foreach (var request in _requests.Values)
        {
            request.Indexed = true;
        }
    }

    /// <summary>The signer signs in the embedded page; false when the sandbox would refuse (canceled, declined, out of turn, not a signer).</summary>
    public bool Sign(string requestId, string email)
    {
        if (!_requests.TryGetValue(requestId, out var request))
        {
            return false;
        }

        lock (request)
        {
            if (request.Canceled || request.Signatures.Any(s => s.Declined))
            {
                return false;
            }

            var next = request.Signatures.OrderBy(s => s.Order).FirstOrDefault(s => s.SignedAt is null);
            if (next is null || !string.Equals(next.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            next.SignedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return true;
        }
    }

    public Signature Decline(string requestId, string email)
    {
        var request = _requests[requestId];
        lock (request)
        {
            var signature = request.Signatures.First(s => string.Equals(s.Email, email, StringComparison.OrdinalIgnoreCase));
            signature.Declined = true;
            return signature;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var fields = await FieldsAsync(request.Content, cancellationToken);
        lock (Sent)
        {
            Sent.Add(new SentRequest(
                request.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.ToDictionary(h => h.Key, h => (IReadOnlyList<string>)[.. h.Value], StringComparer.OrdinalIgnoreCase),
                fields));
        }

        if (Outage is { } outage)
        {
            return Json(outage, """{"error":{"error_name":"unknown","error_msg":"Stub outage"}}""");
        }

        if (request.Headers.Authorization is not { Scheme: "Basic", Parameter: { } credentials }
            || Encoding.ASCII.GetString(Convert.FromBase64String(credentials)) != AcceptedKey + ":")
        {
            return Json(HttpStatusCode.Unauthorized, """{"error":{"error_name":"unauthorized","error_msg":"Bad key"}}""");
        }

        var path = request.RequestUri.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        return (request.Method.Method, path) switch
        {
            ("POST", "/v3/signature_request/create_embedded") => Create(fields),
            ("GET", "/v3/signature_request/list") => List(query["query"], query["page"]),
            ("GET", var p) when p.StartsWith("/v3/signature_request/files/", StringComparison.Ordinal) => Files(p["/v3/signature_request/files/".Length..]),
            ("GET", var p) when p.StartsWith("/v3/signature_request/", StringComparison.Ordinal) => Get(p["/v3/signature_request/".Length..]),
            ("GET", var p) when p.StartsWith("/v3/embedded/sign_url/", StringComparison.Ordinal) => SignUrl(p["/v3/embedded/sign_url/".Length..]),
            ("POST", var p) when p.StartsWith("/v3/signature_request/cancel/", StringComparison.Ordinal) => Cancel(p["/v3/signature_request/cancel/".Length..]),
            _ => Json(HttpStatusCode.NotFound, """{"error":{"error_name":"not_found","error_msg":"No such route"}}"""),
        };
    }

    private HttpResponseMessage Create(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("client_id", out var clientId) || clientId != ClientId)
        {
            return Json(HttpStatusCode.BadRequest, """{"error":{"error_name":"bad_request","error_msg":"client_id"}}""");
        }

        var id = $"stub{Interlocked.Increment(ref _next):D4}{Guid.CreateVersion7():N}";
        var signatures = new List<Signature>();
        for (var i = 0; fields.ContainsKey($"signers[{i}][email_address]"); i++)
        {
            signatures.Add(new Signature($"sig{i}{Guid.CreateVersion7():N}", fields[$"signers[{i}][email_address]"], fields[$"signers[{i}][name]"], int.Parse(fields[$"signers[{i}][order]"], CultureInfo.InvariantCulture)));
        }

        var metadata = fields.Where(f => f.Key.StartsWith("metadata[", StringComparison.Ordinal)).ToDictionary(f => f.Key[9..^1], f => f.Value, StringComparer.Ordinal);
        var created = new Request(id, fields.GetValueOrDefault("title", string.Empty), signatures, metadata, fields.GetValueOrDefault("test_mode") == "1",
            long.TryParse(fields.GetValueOrDefault("expires_at"), CultureInfo.InvariantCulture, out var expires) ? expires : null, fields.GetValueOrDefault("files[0]"));
        _requests[id] = created;
        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { signature_request = Wire(created) }));
    }

    private HttpResponseMessage List(string? query, string? page)
    {
        // As the sandbox answered (probed November 2026): a bare value with a colon is a syntax error, a quoted phrase
        // matches. The stub matches any indexed request whose metadata holds the phrase exactly; a new one is indexed only
        // when a test calls Index(), as the real search catches up about 80 s after creation.
        const string Field = "metadata:";
        string? value = null;
        if (query is not null && query.StartsWith(Field + "\"", StringComparison.Ordinal) && query.EndsWith('"') && query.Length > Field.Length + 1)
        {
            value = query[(Field.Length + 1)..^1];
        }
        else if (query is not null && query.StartsWith(Field, StringComparison.Ordinal) && query[Field.Length..].Contains(':', StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.BadRequest, """{"error":{"error_name":"bad_request","error_msg":"Error retrieving result. Please check your query syntax."}}""");
        }

        var matches = _requests.Values.Where(r => r.Indexed && value is not null && r.Metadata.Values.Contains(value, StringComparer.Ordinal)).Select(Wire).ToList();
        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            signature_requests = matches,
            list_info = new { num_pages = 1, num_results = matches.Count, page = int.Parse(page ?? "1", CultureInfo.InvariantCulture), page_size = 100 },
        }));
    }

    private HttpResponseMessage Get(string id) =>
        _requests.TryGetValue(id, out var request)
            ? Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { signature_request = Wire(request) }))
            : Json(HttpStatusCode.NotFound, """{"error":{"error_name":"not_found","error_msg":"Signature request not found"}}""");

    private HttpResponseMessage SignUrl(string signatureId)
    {
        var request = _requests.Values.FirstOrDefault(r => r.Signatures.Any(s => s.Id == signatureId));
        if (request is null || request.Canceled)
        {
            return Json(HttpStatusCode.NotFound, """{"error":{"error_name":"not_found","error_msg":"Signature not found"}}""");
        }

        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { embedded = new { sign_url = $"https://app.hellosign.com/editor/embeddedSign?signature_id={signatureId}&token=stub", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } }));
    }

    private HttpResponseMessage Cancel(string id)
    {
        if (!_requests.TryGetValue(id, out var request))
        {
            return Json(HttpStatusCode.NotFound, """{"error":{"error_name":"not_found","error_msg":"Signature request not found"}}""");
        }

        lock (request)
        {
            if (request.Canceled || request.Signatures.All(s => s.SignedAt is not null))
            {
                return Json(HttpStatusCode.Conflict, """{"error":{"error_name":"conflict","error_msg":"Not cancellable"}}""");
            }

            request.Canceled = true;
        }

        return Json(HttpStatusCode.OK, "{}");
    }

    private HttpResponseMessage Files(string id)
    {
        if (!_requests.TryGetValue(id, out var request))
        {
            return Json(HttpStatusCode.NotFound, """{"error":{"error_name":"not_found","error_msg":"Signature request not found"}}""");
        }

        if (DownloadsNotReady > 0)
        {
            DownloadsNotReady--;
            return Json(HttpStatusCode.Conflict, """{"error":{"error_name":"conflict","error_msg":"Files are still being processed"}}""");
        }

        var signed = string.Join(", ", request.Signatures.Where(s => s.SignedAt is not null).Select(s => s.Email));
        var content = new ByteArrayContent(Encoding.ASCII.GetBytes($"%PDF-1.7 stub ({request.FileBytes} bytes in) signed by {signed}\n%%EOF\n"));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static object Wire(Request request) => new
    {
        signature_request_id = request.Id,
        title = request.Title,
        test_mode = request.TestMode,
        is_complete = request.Signatures.All(s => s.SignedAt is not null),
        is_declined = request.Signatures.Any(s => s.Declined),
        has_error = false,
        expires_at = request.ExpiresAt,
        metadata = request.Metadata,
        signatures = request.Signatures.Select(s => new
        {
            signature_id = s.Id,
            signer_email_address = s.Email,
            signer_name = s.Name,
            order = s.Order,
            status_code = s.Declined ? "declined" : s.SignedAt is null ? "awaiting_signature" : "signed",
            signed_at = s.SignedAt,
        }),
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>The multipart fields by name; a file part becomes its byte count.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> FieldsAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (content is MultipartFormDataContent multipart)
        {
            foreach (var part in multipart)
            {
                var name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? string.Empty;
                fields[name] = part.Headers.ContentDisposition?.FileName is null
                    ? await part.ReadAsStringAsync(cancellationToken)
                    : (await part.ReadAsByteArrayAsync(cancellationToken)).Length.ToString(CultureInfo.InvariantCulture);
            }
        }

        return fields;
    }

    public sealed class Signature(string id, string email, string name, int order)
    {
        public string Id { get; } = id;

        public string Email { get; } = email;

        public string Name { get; } = name;

        public int Order { get; } = order;

        public long? SignedAt { get; set; }

        public bool Declined { get; set; }
    }

    public sealed class Request(string id, string title, List<Signature> signatures, Dictionary<string, string> metadata, bool testMode, long? expiresAt, string? fileBytes)
    {
        public string Id { get; } = id;

        public string Title { get; } = title;

        public List<Signature> Signatures { get; } = signatures;

        public Dictionary<string, string> Metadata { get; } = metadata;

        public bool TestMode { get; } = testMode;

        /// <summary>Visible to the search: the real one indexes a new request about 80 s after creation.</summary>
        public bool Indexed { get; set; }

        public long? ExpiresAt { get; } = expiresAt;

        public string? FileBytes { get; } = fileBytes;

        public bool Canceled { get; set; }
    }
}
