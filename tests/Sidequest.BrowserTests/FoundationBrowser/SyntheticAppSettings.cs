using System.Net;

namespace Sidequest.BrowserTests.FoundationBrowser;

/// <summary>Represents the explicitly configured local synthetic-app origin permitted for browser compatibility tests.</summary>
/// <param name="BaseUri">The validated HTTP(S) loopback root without credentials, path, query, or fragment.</param>
/// <remarks>The URI value is immutable; validation and origin comparisons share no mutable state and perform no network or DNS operations.</remarks>
internal sealed record SyntheticAppSettings(Uri BaseUri)
{
    /// <summary>The safe configuration error that intentionally does not echo a potentially credential-bearing URL.</summary>
    internal const string InvalidUrl =
        "SIDEQUEST_BASE_URL must be an absolute HTTP(S) loopback root URL for a running synthetic development app, without credentials, query or fragment.";

    /// <summary>Validates the mandatory base URL without resolving DNS or contacting an application.</summary>
    /// <param name="value">The SIDEQUEST_BASE_URL value; null and whitespace are invalid.</param>
    /// <returns>Settings restricted to localhost or a literal loopback IP at the configured scheme and port.</returns>
    /// <exception cref="InvalidOperationException">The value is absent or is not an allowed credential-free HTTP(S) loopback root.</exception>
    /// <example>
    /// <code>
    /// var settings = SyntheticAppSettings.Parse("http://127.0.0.1:5078");
    /// Assert.Equal("http://127.0.0.1:5078/foundation", settings.At("/foundation").AbsoluteUri);
    /// Assert.False(settings.IsSameOrigin("https://login.microsoftonline.com/"));
    /// </code>
    /// </example>
    internal static SyntheticAppSettings Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/" ||
            !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
              (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address))))
            throw new InvalidOperationException(InvalidUrl);

        return new(uri);
    }

    /// <summary>Resolves a test-owned application route against the validated synthetic-app root.</summary>
    /// <param name="path">The relative application route supplied by the test, such as /foundation.</param>
    /// <returns>The resolved route URI; callers must not supply untrusted absolute routes.</returns>
    internal Uri At(string path) => new(BaseUri, path);

    /// <summary>Checks an outgoing absolute URL against the configured scheme, host, port, and no-credentials requirement.</summary>
    /// <param name="value">The request URL to inspect without network access.</param>
    /// <returns>True only when the request remains on the configured credential-free origin.</returns>
    internal bool IsSameOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == BaseUri.Scheme &&
        uri.IdnHost.Equals(BaseUri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == BaseUri.Port && uri.UserInfo.Length == 0;
}
