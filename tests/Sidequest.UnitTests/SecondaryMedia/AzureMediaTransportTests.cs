using System.Net;
using Azure;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Storage.Blobs;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Media;

namespace Sidequest.UnitTests.SecondaryMedia;

/// <summary>Exercises real Azure SDK request construction through a deterministic in-process HTTP transport, never a live provider.</summary>
public sealed class AzureMediaTransportTests
{
    /// <summary>Every operation checks container privacy first; publicly readable containers cannot be read, written or deleted.</summary>
    /// <param name="operation">The provider operation attempted against a publicly configured container.</param>
    /// <returns>Completion after exactly one policy request and safe rejection.</returns>
    [Theory]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("delete")]
    public async Task PublicContainer_RejectsEveryOperationBeforeObjectRequest(string operation)
    {
        using var handler = new TransportHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("restype=container", request.RequestUri!.Query);
            var response = Reply(HttpStatusCode.OK);
            response.Headers.Add("x-ms-blob-public-access", "blob");
            return response;
        });
        var storage = Storage(handler);
        var key = $"covers/{Guid.NewGuid():N}.png";
        var error = await Assert.ThrowsAsync<DomainException>(() => operation switch
        {
            "write" => storage.WriteAsync(key, new byte[] { 1 }, "image/png"),
            "read" => storage.OpenReadAsync(key),
            _ => storage.DeleteIfExistsAsync(key)
        });
        Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
        Assert.True(error.IsPermanentDependencyFailure);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Azure writes carry If-None-Match wildcard and private-cache headers, and do not overwrite an existing object.</summary>
    /// <param name="exists">Whether Azure reports a precondition conflict for the existing object.</param>
    /// <returns>Completion after SDK request headers and truthful result assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_IsWriteNewOnlyAndReportsProviderConflict(bool exists)
    {
        using var handler = new TransportHandler(request =>
        {
            if (request.RequestUri!.Query.Contains("restype=container", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK);
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("*", Assert.Single(request.Headers.GetValues("If-None-Match")));
            Assert.Equal("no-store", Assert.Single(request.Headers.GetValues("x-ms-blob-cache-control")));
            Assert.Equal("image/png", Assert.Single(request.Headers.GetValues("x-ms-blob-content-type")));
            return Reply(exists ? HttpStatusCode.PreconditionFailed : HttpStatusCode.Created,
                exists ? "ConditionNotMet" : null);
        });
        var storage = Storage(handler);
        var operation = storage.WriteAsync($"covers/{Guid.NewGuid():N}.png", new byte[] { 1, 2, 3 }, "image/png");
        if (exists)
            Assert.Equal(ErrorCode.DependencyUnavailable, (await Assert.ThrowsAsync<DomainException>(() => operation)).Code);
        else
            await operation;
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>Delete acknowledges both provider-confirmed removal and already-absent objects without fake success on other errors.</summary>
    /// <param name="status">The provider's object-deletion result.</param>
    /// <returns>Completion after SDK idempotency and safe failure assertions.</returns>
    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Delete_IsIdempotentOnlyForConfirmedAbsence(HttpStatusCode status)
    {
        using var handler = new TransportHandler(request => request.RequestUri!.Query.Contains("restype=container", StringComparison.Ordinal)
            ? Reply(HttpStatusCode.OK) : Reply(status, status == HttpStatusCode.NotFound ? "BlobNotFound" : "AuthorizationFailure"));
        var operation = Storage(handler).DeleteIfExistsAsync($"covers/{Guid.NewGuid():N}.png");
        if (status == HttpStatusCode.Forbidden)
            Assert.Equal(ErrorCode.DependencyUnavailable, (await Assert.ThrowsAsync<DomainException>(() => operation)).Code);
        else
            await operation;
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>The SDK read returns owned buffered content only after a private policy check and exact length validation.</summary>
    /// <returns>Completion after downloaded bytes match the response and the stream remains readable.</returns>
    [Fact]
    public async Task Read_ReturnsOwnedStreamAfterPrivatePolicyAndBoundedDownload()
    {
        using var handler = new TransportHandler(request =>
        {
            var response = Reply(HttpStatusCode.OK);
            if (!request.RequestUri!.Query.Contains("restype=container", StringComparison.Ordinal))
            {
                response.Content = new ByteArrayContent([1, 2, 3, 4]);
                response.Content.Headers.ContentType = new("image/png");
                response.Content.Headers.ContentLength = 4;
                response.Headers.Add("x-ms-blob-type", "BlockBlob");
            }
            return response;
        });
        await using var content = await Storage(handler).OpenReadAsync($"covers/{Guid.NewGuid():N}.png");
        using var output = new MemoryStream();
        await content.CopyToAsync(output);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, output.ToArray());
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>Definite authorization/configuration responses are permanent, while throttling, server outages and timeouts remain retryable.</summary>
    /// <param name="status">Actual provider HTTP response status.</param>
    /// <param name="permanent">Whether operator correction rather than automatic retry is required.</param>
    /// <returns>Completion after the real SDK pipeline produces a safely categorized domain failure.</returns>
    [Theory]
    [InlineData(400, true)]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(404, true)]
    [InlineData(408, false)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    public async Task ProviderStatus_PreservesPresentationAndClassifiesRetry(int status, bool permanent)
    {
        using var handler = new TransportHandler(_ => Reply((HttpStatusCode)status, "SyntheticProviderFailure"));
        var failure = await Assert.ThrowsAsync<DomainException>(() =>
            Storage(handler).DeleteIfExistsAsync($"covers/{Guid.NewGuid():N}.png"));
        Assert.Equal(ErrorCode.DependencyUnavailable, failure.Code);
        Assert.Equal(permanent, failure.IsPermanentDependencyFailure);
        Assert.Equal("Private media storage is unavailable.", failure.Message);
    }

    /// <summary>Even an injected SDK pipeline configured to log bodies has content logging disabled before its first provider request.</summary>
    /// <returns>Completion after both policy and deletion requests observe the enforced logging setting.</returns>
    [Fact]
    public async Task InjectedPipeline_ContentLoggingIsDisabledBeforeProviderUse()
    {
        var options = new BlobClientOptions();
        options.Diagnostics.IsLoggingContentEnabled = true;
        using var handler = new TransportHandler(request =>
        {
            Assert.False(options.Diagnostics.IsLoggingContentEnabled);
            return Reply(request.RequestUri!.Query.Contains("restype=container", StringComparison.Ordinal)
                ? HttpStatusCode.OK : HttpStatusCode.Accepted);
        });
        await Storage(handler, options).DeleteIfExistsAsync($"covers/{Guid.NewGuid():N}.png");
        Assert.Equal(2, handler.Calls);
        Assert.False(options.Diagnostics.IsLoggingContentEnabled);
    }

    /// <summary>Credential acquisition is permanent only when a definite authorization/configuration response exists, not for outages or ambiguous network failures.</summary>
    /// <param name="status">Nested provider status, or zero for a network failure without a definitive response.</param>
    /// <param name="permanent">Whether operator correction is proven necessary.</param>
    /// <returns>Completion after credential exceptions preserve safe presentation and conservative retry classification.</returns>
    [Theory]
    [InlineData(401, true)]
    [InlineData(503, false)]
    [InlineData(0, false)]
    public async Task CredentialFailure_RequiresDefiniteResponseForPermanentClassification(int status, bool permanent)
    {
        using var handler = new TransportHandler(_ => throw new AuthenticationFailedException("private-credential-sentinel",
            status == 0 ? new HttpRequestException("private-network-sentinel") : new RequestFailedException(status, "private-response-sentinel")));
        var failure = await Assert.ThrowsAsync<DomainException>(() =>
            Storage(handler).DeleteIfExistsAsync($"covers/{Guid.NewGuid():N}.png"));
        Assert.Equal(ErrorCode.DependencyUnavailable, failure.Code);
        Assert.Equal(permanent, failure.IsPermanentDependencyFailure);
        Assert.Equal("Private media storage is unavailable.", failure.Message);
        Assert.Null(failure.InnerException);
    }

    private static AzurePrivateMediaStorage Storage(HttpMessageHandler handler, BlobClientOptions? options = null)
    {
        options ??= new BlobClientOptions();
        options.Transport = new HttpClientTransport(new HttpClient(handler));
        options.Retry.Delay = TimeSpan.Zero;
        options.Retry.MaxDelay = TimeSpan.Zero;
        return new(new PrivateMediaOptions
        {
            ContainerName = "covers",
            ConnectionString = $"DefaultEndpointsProtocol=https;AccountName=synthetic;AccountKey={Convert.ToBase64String(new byte[32])};EndpointSuffix=core.windows.net"
        }, options);
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string? code = null)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Add("ETag", "\"synthetic\"");
        response.Content = new ByteArrayContent([]);
        response.Content.Headers.LastModified = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
        if (code is not null)
            response.Headers.Add("x-ms-error-code", code);
        return response;
    }

    private sealed class TransportHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(reply(request));
        }
    }
}
