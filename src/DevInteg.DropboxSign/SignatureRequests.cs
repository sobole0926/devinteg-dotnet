using System;
using System.Collections.Generic;

namespace DevInteg.DropboxSign;

/// <summary>
/// An embedded signature request: the signers sign inside your page, and Dropbox Sign emails nobody. Signers sign in
/// <see cref="RequestSigner.Order"/>. Only what this client sends is here; subject, message, CCs and text tags are left
/// out on purpose, since an embedded flow carries its own invitation.
/// </summary>
/// <param name="Title">Shown to signers in the signing page.</param>
/// <param name="Signers">At least one, with distinct orders.</param>
/// <param name="Files">At least one document.</param>
public sealed record EmbeddedSignatureRequest(string Title, IReadOnlyList<RequestSigner> Signers, IReadOnlyList<SignatureDocument> Files)
{
    /// <summary>When unsigned signatures expire; none when null.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>A signer may decline instead of signing.</summary>
    public bool AllowDecline { get; init; } = true;

    /// <summary>
    /// Stored on the request and returned with it. Dropbox Sign allows up to 10 keys of 40 characters and 500 characters of
    /// value; a value you will search by (<see cref="DropboxSignClient.FindByMetadataAsync"/>) may not hold a quote or a backslash.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <param name="Name">As it appears on the signature.</param>
/// <param name="EmailAddress">Identifies the signer; Dropbox Sign sends it nothing for an embedded request.</param>
/// <param name="Order">Signing order; any distinct integers, lowest signs first.</param>
public sealed record RequestSigner(string Name, string EmailAddress, int Order);

/// <param name="FileName">The name the document carries at the vendor.</param>
/// <param name="Content">The document's bytes.</param>
/// <param name="ContentType">The document's media type.</param>
public sealed record SignatureDocument(string FileName, ReadOnlyMemory<byte> Content, string ContentType = "application/pdf");

/// <summary>A signature request as Dropbox Sign reports it.</summary>
/// <param name="Id">The vendor's <c>signature_request_id</c>.</param>
/// <param name="IsComplete">Every signer has signed.</param>
/// <param name="IsDeclined">A signer declined.</param>
/// <param name="HasError">The vendor could not process the request.</param>
/// <param name="ExpiresAt">When unsigned signatures expire, or null when the request has no expiry.</param>
/// <param name="Metadata">The request's metadata; values that are not strings are left out.</param>
/// <param name="Signatures">One per signer, in signing order.</param>
public sealed record SignatureRequest(
    string Id,
    bool IsComplete,
    bool IsDeclined,
    bool HasError,
    DateTimeOffset? ExpiresAt,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<Signature> Signatures);

/// <summary>One signer's place on a request.</summary>
/// <param name="Id">The vendor's <c>signature_id</c>: what callbacks name as the related signature and what a sign URL is fetched by.</param>
/// <param name="SignerEmailAddress">The signer's address as sent.</param>
/// <param name="SignerName">The signer's name as sent.</param>
/// <param name="Order">The zero-based signing order, when the vendor reports one.</param>
/// <param name="StatusCode">The vendor's status, e.g. <c>awaiting_signature</c>, <c>signed</c>, <c>declined</c>.</param>
/// <param name="SignedAt">When the signer signed, or null.</param>
public sealed record Signature(string Id, string SignerEmailAddress, string? SignerName, int? Order, string? StatusCode, DateTimeOffset? SignedAt)
{
    /// <summary>The signer declined.</summary>
    public bool IsDeclined => string.Equals(StatusCode, "declined", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A short-lived URL that opens one signer's embedded signing page; fetch a fresh one each time the page opens.</summary>
/// <param name="SignUrl">Pass to <c>hellosign-embedded</c> with the API app's client id.</param>
/// <param name="ExpiresAt">When the URL stops working.</param>
public sealed record EmbeddedSignUrl(Uri SignUrl, DateTimeOffset? ExpiresAt);

/// <summary>What became of a cancellation.</summary>
/// <param name="Canceled">The vendor queued the cancellation. False when there was nothing left to cancel: already canceled or complete, or unknown to this account.</param>
/// <param name="Status">The HTTP status the vendor answered.</param>
/// <param name="ErrorName">The vendor's error name when it refused.</param>
public sealed record CancelResult(bool Canceled, int Status, string? ErrorName);
