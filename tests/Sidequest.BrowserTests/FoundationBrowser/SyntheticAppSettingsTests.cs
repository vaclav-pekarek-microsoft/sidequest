namespace Sidequest.BrowserTests.FoundationBrowser;

/// <summary>Checks loopback-only browser configuration and off-origin rejection without launching a browser or making requests.</summary>
public sealed class SyntheticAppSettingsTests
{
    /// <summary>Checks supported loopback roots and exact route resolution while denying an external identity-provider origin.</summary>
    /// <param name="configured">The allowed localhost, IPv4, or IPv6 root URL.</param>
    /// <param name="expected">The independently specified absolute foundation route.</param>
    [Theory]
    [InlineData("http://localhost:5080", "http://localhost:5080/foundation")]
    [InlineData("https://localhost:5443/", "https://localhost:5443/foundation")]
    [InlineData("http://127.0.0.1:5080/", "http://127.0.0.1:5080/foundation")]
    [InlineData("http://127.0.0.2:5080/", "http://127.0.0.2:5080/foundation")]
    [InlineData("http://[::1]:5080/", "http://[::1]:5080/foundation")]
    public void Parse_LoopbackRoot_ProducesExactFoundationAddress(string configured, string expected)
    {
        var settings = SyntheticAppSettings.Parse(configured);
        Assert.Equal(expected, settings.At("/foundation").AbsoluteUri);
        Assert.True(settings.IsSameOrigin(expected));
        Assert.False(settings.IsSameOrigin("https://login.microsoftonline.com/"));
    }

    /// <summary>Checks fail-closed configuration errors for missing or unsafe URLs without echoing their credentials.</summary>
    /// <param name="configured">The absent, malformed, non-loopback, or otherwise forbidden URL.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/foundation")]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("file:///foundation")]
    [InlineData("http://example.invalid/")]
    [InlineData("http://localhost.example.invalid/")]
    [InlineData("http://192.168.1.10/")]
    [InlineData("http://[::2]/")]
    [InlineData("http://localhost@evil.invalid/")]
    [InlineData("http://user:secret@localhost:5080/")]
    [InlineData("http://localhost:5080/foundation")]
    [InlineData("http://localhost:5080/?mode=production")]
    [InlineData("http://localhost:5080/#production")]
    public void Parse_MissingOrUnsafeUrl_FailsWithoutEchoingConfiguration(string? configured)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SyntheticAppSettings.Parse(configured));
        Assert.Equal(SyntheticAppSettings.InvalidUrl, error.Message);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(error.InnerException);
    }

    /// <summary>Checks that changing an origin component or adding credentials cannot pass the outgoing-request gate.</summary>
    /// <param name="candidate">The absolute or malformed request URL under consideration.</param>
    /// <param name="allowed">The literal expected same-origin permission.</param>
    [Theory]
    [InlineData("http://localhost:5080/signin", true)]
    [InlineData("http://localhost:5080/_blazor/negotiate?negotiateVersion=1", true)]
    [InlineData("https://localhost:5080/signin", false)]
    [InlineData("http://localhost:5081/signin", false)]
    [InlineData("http://127.0.0.1:5080/signin", false)]
    [InlineData("http://localhost.example.invalid:5080/signin", false)]
    [InlineData("http://user@localhost:5080/signin", false)]
    [InlineData("/signin", false)]
    public void IsSameOrigin_RejectsSchemeHostPortAndCredentialChanges(string candidate, bool allowed)
    {
        var settings = SyntheticAppSettings.Parse("http://localhost:5080");
        Assert.Equal(allowed, settings.IsSameOrigin(candidate));
    }
}
