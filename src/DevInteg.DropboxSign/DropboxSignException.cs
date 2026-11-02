using System;

namespace DevInteg.DropboxSign;

/// <summary>
/// Dropbox Sign refused or failed. The HTTP status and the vendor's error name travel; the vendor's message and the
/// response body never do, since they can echo names and addresses back. A caller that can wait retries on
/// <see cref="IsTransient"/>.
/// </summary>
public sealed class DropboxSignException : Exception
{
    /// <summary>Creates the exception for one failed <paramref name="operation"/>.</summary>
    public DropboxSignException(string operation, int? status, string? errorName, bool transient, Exception? innerException = null)
        : base(Describe(operation, status, errorName), innerException)
    {
        Operation = operation;
        Status = status;
        ErrorName = errorName;
        IsTransient = transient;
    }

    /// <summary>What the client was doing: <c>create</c>, <c>find</c>, <c>get</c>, <c>signUrl</c>, <c>cancel</c> or <c>download</c>.</summary>
    public string Operation { get; }

    /// <summary>The HTTP status, or null when the failure was decided without one.</summary>
    public int? Status { get; }

    /// <summary>The vendor's own error code (never its message), or one of this client's: <c>emptyResponse</c>, <c>malformedResponse</c>, <c>missingSignatureRequest</c>, <c>missingSignUrl</c>, <c>fileNotReady</c>, <c>emptyFile</c>.</summary>
    public string? ErrorName { get; }

    /// <summary>Likely to clear on its own: a file still being prepared, an outage, a timeout, a rate limit.</summary>
    public bool IsTransient { get; }

    private static string Describe(string operation, int? status, string? errorName) =>
        $"Dropbox Sign failed on {operation}"
        + (status is null ? "." : $" with status {status}.")
        + (errorName is null ? string.Empty : $" ({errorName})");
}
