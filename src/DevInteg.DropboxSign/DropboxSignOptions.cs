using System;

namespace DevInteg.DropboxSign;

/// <summary>
/// The account and API app the client works under. Keep the key out of source control: bind it from the environment,
/// user-secrets or a vault.
/// </summary>
public sealed class DropboxSignOptions
{
    /// <summary>Dropbox Sign's production API root.</summary>
    public const string DefaultBaseAddress = "https://api.hellosign.com/v3/";

    /// <summary>The account's API key. Sent as HTTP basic authentication; also the HMAC key of every callback the account receives.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The API app embedded requests run under. Required to create an embedded request.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Requests are free, watermarked and not legally binding. Turn on outside production.</summary>
    public bool TestMode { get; set; }

    /// <summary>The API's base address, ending in a slash; a test points it at a stub.</summary>
    public Uri BaseAddress { get; set; } = new(DefaultBaseAddress);

    /// <summary>An API key is set and the base address is absolute and ends in a slash, so relative paths resolve under it.</summary>
    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && BaseAddress is { IsAbsoluteUri: true }
        && BaseAddress.AbsolutePath.EndsWith('/');
}
