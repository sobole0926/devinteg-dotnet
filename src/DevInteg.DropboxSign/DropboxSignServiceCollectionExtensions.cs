using System;
using DevInteg.DropboxSign;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Dropbox Sign client and the signing-link tokens.</summary>
public static class DropboxSignServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DropboxSignClient"/> as a typed client named <see cref="DropboxSignClient.HttpClientName"/>,
    /// with the <c>Authorization</c> header redacted from HTTP logs and <see cref="DropboxSignOptions"/> validated at
    /// startup. Configure the options yourself (<c>BindConfiguration</c> or <c>Configure</c>). Add a resilience handler
    /// on the returned builder that retries reads only, e.g.
    /// <c>.AddStandardResilienceHandler(o =&gt; o.Retry.DisableForUnsafeHttpMethods())</c>: a create is not idempotent.
    /// </summary>
    public static IHttpClientBuilder AddDropboxSignClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<DropboxSignOptions>()
            .Validate(o => o.IsValid(), "DropboxSignOptions needs an ApiKey and an absolute BaseAddress ending in '/'.")
            .ValidateOnStart();
        return services.AddHttpClient<DropboxSignClient>(DropboxSignClient.HttpClientName)
            .RedactLoggedHeaders(["Authorization"]);
    }

    /// <summary>
    /// Registers <see cref="SigningLinkTokens"/> as a singleton over <see cref="SigningLinkKeyRing"/>, validated at startup,
    /// and <see cref="TimeProvider.System"/> unless a <see cref="TimeProvider"/> is registered. Configure the ring yourself.
    /// </summary>
    public static IServiceCollection AddDropboxSignSigningLinks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<SigningLinkKeyRing>()
            .Validate(o => o.IsValid(), "SigningLinkKeyRing needs an ActiveKeyId naming one of Keys, each base64 of at least 32 bytes, and MaxLifetimeDays from 1 to 365.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new SigningLinkTokens(sp.GetRequiredService<IOptions<SigningLinkKeyRing>>().Value, sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
