using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Media;

namespace Sidequest.UnitTests.SecondaryMedia;

/// <summary>Verifies that provider configuration and key validation fail explicitly without live provider traffic.</summary>
public sealed class AzurePrivateMediaStorageTests
{
    /// <summary>Constructing an unconfigured adapter performs no provider request, while every requested operation fails safely.</summary>
    /// <param name="operation">The requested read, write or idempotent delete boundary.</param>
    /// <returns>Completion after dependency classification and redacted message assertions.</returns>
    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("delete")]
    public async Task MissingConfiguration_FailsRequestedOperation(string operation)
    {
        var storage = new AzurePrivateMediaStorage(new PrivateMediaOptions());
        var key = $"covers/{Guid.NewGuid():N}.png";
        var error = await Assert.ThrowsAsync<DomainException>(() => operation switch
        {
            "read" => storage.OpenReadAsync(key),
            "write" => storage.WriteAsync(key, new byte[] { 1 }, "image/png"),
            _ => storage.DeleteIfExistsAsync(key)
        });
        Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
        Assert.True(error.IsPermanentDependencyFailure);
        Assert.Equal("Private media configuration is missing or invalid.", error.Message);
    }

    /// <summary>Invalid deployment settings reject before contacting any endpoint.</summary>
    /// <param name="kind">A non-HTTPS endpoint, invalid identity, timeout, malformed secret or container name.</param>
    /// <returns>Completion after safe dependency failure with no secret disclosure.</returns>
    [Theory]
    [InlineData("http")]
    [InlineData("identity")]
    [InlineData("timeout")]
    [InlineData("secret")]
    [InlineData("container")]
    [InlineData("sas")]
    public async Task InvalidConfiguration_IsNotSuccessfulDeletion(string kind)
    {
        var options = new PrivateMediaOptions { ContainerName = "covers", ServiceUri = new Uri("https://example.invalid/") };
        switch (kind)
        {
            case "http": options.ServiceUri = new Uri("http://example.invalid/"); break;
            case "identity": options.ManagedIdentityClientId = "invalid"; break;
            case "timeout": options.OperationTimeout = TimeSpan.FromSeconds(61); break;
            case "secret": options.ConnectionString = "secret-sentinel"; break;
            case "container": options.ContainerName = "invalid--name"; break;
            case "sas": options.ServiceUri = new Uri("https://example.invalid/?sig=secret-sentinel"); break;
        }
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new AzurePrivateMediaStorage(options).DeleteIfExistsAsync($"covers/{Guid.NewGuid():N}.png"));
        Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
        Assert.True(error.IsPermanentDependencyFailure);
        Assert.DoesNotContain("secret-sentinel", error.Message);
        Assert.DoesNotContain("example.invalid", error.Message);
    }

    /// <summary>Untrusted paths and public URLs are never accepted as application-generated object keys.</summary>
    /// <param name="key">Invalid object key.</param>
    /// <returns>Completion after input validation without provider initialization.</returns>
    [Theory]
    [InlineData("../cover.png")]
    [InlineData("https://example.invalid/cover.png")]
    [InlineData("covers/not-a-guid.png")]
    public async Task InvalidKeys_AreRejectedBeforeProviderUse(string key)
    {
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new AzurePrivateMediaStorage(new()).DeleteIfExistsAsync(key));
        Assert.Equal(ErrorCode.Validation, error.Code);
    }
}
