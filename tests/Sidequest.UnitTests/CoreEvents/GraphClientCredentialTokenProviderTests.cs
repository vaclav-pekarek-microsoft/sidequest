using System.Net;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Directory;

namespace Sidequest.UnitTests.CoreEvents;

/// <summary>Verifies tenant-bound OAuth requests and safe failure behavior with no credential endpoint calls.</summary>
public sealed class GraphClientCredentialTokenProviderTests
{
    /// <summary>Uses the official credential pipeline for the configured tenant/Graph scope and caches a valid token in memory.</summary>
    /// <returns>A task completing after SDK transport, form, token, and one-acquisition cache assertions.</returns>
    [Fact]
    public async Task TokenRequestUsesTenantAndGraphScope()
    {
        var policy = GraphDirectoryGatewayTests.Policy();
        var clientId = Guid.Parse("40000000-0000-0000-0000-000000000004");
        using var handler = new ControlledHttpHandler((request, _) => Task.FromResult(
            Reply(request, policy.TenantId, "{\"access_token\":\"issued-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}")));
        using var http = new HttpClient(handler);
        var sut = new GraphClientCredentialTokenProvider(http, new()
        {
            TenantId = policy.TenantId,
            ClientId = clientId,
            ClientSecret = "synthetic&value"
        }, policy);
        Assert.Equal("issued-token", await sut.GetTokenAsync());
        Assert.Equal("issued-token", await sut.GetTokenAsync());
        var request = Assert.Single(handler.Requests, x => x.Method == HttpMethod.Post);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://login.microsoftonline.com/{policy.TenantId}/oauth2/v2.0/token", request.Uri);
        Assert.Contains($"client_id={clientId}", request.Body);
        Assert.Contains("client_secret=synthetic%26value", request.Body);
        Assert.Contains("https://graph.microsoft.com/.default", Uri.UnescapeDataString(request.Body));
        Assert.Contains("grant_type=client_credentials", request.Body);
        Assert.All(handler.Requests, x => Assert.Equal("login.microsoftonline.com", new Uri(x.Uri).Host));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetTokenAsync(cancellation.Token));
        Assert.Single(handler.Requests, x => x.Method == HttpMethod.Post);
    }

    /// <summary>Rejects cross-tenant credentials before accessing the HTTP transport.</summary>
    /// <returns>A task completing after the configuration guard is checked.</returns>
    [Fact]
    public async Task WrongTenantFailsBeforeHttp()
    {
        using var handler = new ControlledHttpHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<DomainException>(() => new GraphClientCredentialTokenProvider(http,
            new() { TenantId = Guid.NewGuid(), ClientId = Guid.NewGuid(), ClientSecret = "synthetic" },
            GraphDirectoryGatewayTests.Policy()).GetTokenAsync());
        Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
        Assert.Contains("do not match", error.Message);
        Assert.Empty(handler.Requests);
    }

    /// <summary>A cancelled caller retains cancellation semantics instead of receiving a misleading provider failure or cached success.</summary>
    /// <returns>A task completing after cancellation propagation is checked through the SDK-backed provider.</returns>
    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        var policy = GraphDirectoryGatewayTests.Policy();
        using var handler = new ControlledHttpHandler((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("A cancelled caller must not reach a provider request.");
        });
        using var http = new HttpClient(handler);
        var sut = new GraphClientCredentialTokenProvider(http,
            new() { TenantId = policy.TenantId, ClientId = Guid.NewGuid(), ClientSecret = "synthetic" }, policy);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetTokenAsync(cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    /// <summary>Never treats denied, malformed, missing or empty token responses as successful authentication.</summary>
    /// <param name="body">Synthetic OAuth response body.</param>
    /// <param name="status">Synthetic HTTP response status.</param>
    /// <returns>A task completing after safe error and request-count checks.</returns>
    [Theory]
    [InlineData("{}", HttpStatusCode.OK)]
    [InlineData("{", HttpStatusCode.OK)]
    [InlineData("[]", HttpStatusCode.OK)]
    [InlineData("null", HttpStatusCode.OK)]
    [InlineData("{\"access_token\":\" \"}", HttpStatusCode.OK)]
    [InlineData("sensitive-provider-detail", HttpStatusCode.Unauthorized)]
    public async Task InvalidTokenResponsesFailSafely(string body, HttpStatusCode status)
    {
        var policy = GraphDirectoryGatewayTests.Policy();
        using var handler = new ControlledHttpHandler((request, _) => Task.FromResult(Reply(request, policy.TenantId, body, status)));
        using var http = new HttpClient(handler);
        var sut = new GraphClientCredentialTokenProvider(http,
            new() { TenantId = policy.TenantId, ClientId = Guid.NewGuid(), ClientSecret = "synthetic" }, policy);
        var error = await Assert.ThrowsAsync<DomainException>(() => sut.GetTokenAsync());
        Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
        Assert.DoesNotContain("sensitive-provider-detail", error.Message);
        Assert.Single(handler.Requests, x => x.Method == HttpMethod.Post);
    }

    private static HttpResponseMessage Reply(HttpRequestMessage request, Guid tenant, string tokenBody,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var root = $"https://login.microsoftonline.com/{tenant:D}";
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == $"{root}/oauth2/v2.0/token")
            return ControlledHttpHandler.Json(tokenBody, status);
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            return ControlledHttpHandler.Json($$"""
                {"authorization_endpoint":"{{root}}/oauth2/v2.0/authorize",
                 "token_endpoint":"{{root}}/oauth2/v2.0/token",
                 "issuer":"{{root}}/v2.0",
                 "jwks_uri":"{{root}}/discovery/v2.0/keys"}
                """);
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/common/discovery/instance")
            return ControlledHttpHandler.Json($$"""
                {"tenant_discovery_endpoint":"{{root}}/v2.0/.well-known/openid-configuration",
                 "metadata":[{"preferred_network":"login.microsoftonline.com","preferred_cache":"login.microsoftonline.com",
                              "aliases":["login.microsoftonline.com","login.windows.net","sts.windows.net"]}]}
                """);
        throw new InvalidOperationException("Unexpected request in the isolated Azure.Identity transport.");
    }
}
