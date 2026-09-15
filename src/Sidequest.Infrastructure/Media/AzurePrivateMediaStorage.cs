using System.Globalization;
using System.Net.Http.Headers;
using Azure;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Sidequest.Application.Media;
using Sidequest.Domain.Rules;

namespace Sidequest.Infrastructure.Media;

/// <summary>Uses the real Azure SDK with write-new-only objects and a checked private container policy.</summary>
/// <param name="options">Deployment configuration copied on construction; missing configuration fails operations, not service resolution.</param>
/// <param name="clientOptions">Optional Azure SDK pipeline configuration, for example a deterministic transport in adapter tests.
/// Null uses the SDK's normal HTTPS transport. The adapter always applies bounded retries and network timeouts.</param>
/// <param name="clock">UTC clock for HTTP-date retry windows; null uses the system clock.</param>
/// <remarks>The container must be provisioned privately outside this adapter; it never creates a container or emits a URL/SAS.
/// Operators must also disable account-level anonymous Blob access to prevent a policy change racing a request.
/// SDK content logging is disabled even for an injected pipeline. Known invalid configuration, public-container policy and
/// definite HTTP 400/401/403/404 failures require operator correction; outages, throttling and timeouts remain retryable.</remarks>
public sealed class AzurePrivateMediaStorage(PrivateMediaOptions options, BlobClientOptions? clientOptions = null,
    TimeProvider? clock = null) : IPrivateMediaStorage
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Uri? serviceUri = options.ServiceUri;
    private readonly string? connectionString = options.ConnectionString;
    private readonly string? identityClientId = options.ManagedIdentityClientId;
    private readonly string containerName = options.ContainerName;
    private readonly TimeSpan operationTimeout = options.OperationTimeout;

    /// <inheritdoc />
    public async Task WriteAsync(string blobName, ReadOnlyMemory<byte> data, string contentType,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(blobName);
        if (data.Length is <= 0 or > 84_000_000 || contentType != "image/png")
            throw new DomainException(ErrorCode.Validation, "Only sanitized PNG content may be stored.");
        await ExecuteAsync(async (container, token) =>
        {
            await container.GetBlobClient(blobName).UploadAsync(BinaryData.FromBytes(data),
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = contentType, CacheControl = "no-store" },
                    TransferOptions = new StorageTransferOptions
                    {
                        MaximumConcurrency = 1,
                        InitialTransferSize = 84_000_000,
                        MaximumTransferSize = 84_000_000
                    }
                }, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken = default)
    {
        ValidateKey(blobName);
        // Buffer within the bounded operation so the returned stream cannot outlive its provider timeout.
        return await ExecuteAsync<Stream>(async (container, token) =>
        {
            var response = await container.GetBlobClient(blobName).DownloadStreamingAsync(cancellationToken: token).ConfigureAwait(false);
            using var result = response.Value;
            if (result.Details.ContentLength is <= 0 or > 84_000_000 || result.Details.ContentType != "image/png")
                throw Unavailable();
            var output = new MemoryStream(checked((int)result.Details.ContentLength));
            try
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var count = await result.Content.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0)
                        break;
                    if (output.Length + count > 84_000_000)
                        throw Unavailable();
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                if (output.Length != result.Details.ContentLength)
                    throw Unavailable();
                output.Position = 0;
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteIfExistsAsync(string blobName, CancellationToken cancellationToken = default)
    {
        ValidateKey(blobName);
        await ExecuteAsync(async (container, token) =>
        {
            await container.GetBlobClient(blobName).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots,
                cancellationToken: token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ExecuteAsync<T>(Func<BlobContainerClient, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var container = CreateContainer();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(operationTimeout);
        try
        {
            var properties = await container.GetPropertiesAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
            if (properties.Value.PublicAccess != PublicAccessType.None)
                throw Unavailable(permanent: true);
            return await operation(container, deadline.Token).ConfigureAwait(false);
        }
        catch (RequestFailedException error)
        {
            throw Unavailable(permanent: IsConfigurationStatus(error.Status), retryAfter: RequestedRetryAfter(error));
        }
        catch (AuthenticationFailedException error)
        {
            // Credential acquisition can also fail during an outage. Only definite provider responses prove configuration failure.
            throw Unavailable(permanent: IsPermanentProviderFailure(error), retryAfter: RequestedRetryAfter(error));
        }
        catch (AggregateException error) when (error.Flatten().InnerExceptions is { Count: > 0 } failures &&
            failures.All(failure => failure is RequestFailedException or AuthenticationFailedException or HttpRequestException or
                IOException or OperationCanceledException))
        {
            token.ThrowIfCancellationRequested();
            throw Unavailable(permanent: failures.All(IsPermanentProviderFailure), retryAfter: RequestedRetryAfter(error));
        }
        catch (HttpRequestException)
        {
            throw Unavailable();
        }
        catch (IOException)
        {
            throw Unavailable();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw Unavailable();
        }
    }

    private BlobContainerClient CreateContainer()
    {
        if (operationTimeout <= TimeSpan.Zero || operationTimeout > TimeSpan.FromSeconds(60) ||
            string.IsNullOrWhiteSpace(containerName) ||
            !System.Text.RegularExpressions.Regex.IsMatch(containerName, "^[a-z0-9](?:[a-z0-9]|-(?!-)){1,61}[a-z0-9]$"))
            throw Configuration();
        try
        {
            var pipeline = clientOptions ?? new BlobClientOptions();
            pipeline.Diagnostics.IsLoggingContentEnabled = false;
            pipeline.Retry.MaxRetries = 1;
            pipeline.Retry.NetworkTimeout = operationTimeout;
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                var container = new BlobContainerClient(connectionString, containerName, pipeline);
                if (container.Uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(container.Uri.Query))
                    throw Configuration();
                return container;
            }
            if (serviceUri is null || !serviceUri.IsAbsoluteUri || serviceUri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(serviceUri.Query) || !string.IsNullOrEmpty(serviceUri.UserInfo) ||
                !string.IsNullOrEmpty(serviceUri.Fragment) || serviceUri.AbsolutePath != "/" ||
                identityClientId is not null && !Guid.TryParse(identityClientId, out _))
                throw Configuration();
            var credential = new ManagedIdentityCredential(identityClientId is null
                ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(identityClientId));
            return new BlobServiceClient(serviceUri, credential, pipeline).GetBlobContainerClient(containerName);
        }
        catch (ArgumentException)
        {
            throw Configuration();
        }
        catch (FormatException)
        {
            throw Configuration();
        }
    }

    private static void ValidateKey(string key)
    {
        if (key is null || key.Length != 43 || !key.StartsWith("covers/", StringComparison.Ordinal) ||
            !key.EndsWith(".png", StringComparison.Ordinal) || !Guid.TryParseExact(key.AsSpan(7, 32), "N", out _))
            throw new DomainException(ErrorCode.Validation, "Invalid private media key.");
    }

    private static bool IsConfigurationStatus(int status) => status is 400 or 401 or 403 or 404;

    private TimeSpan? RequestedRetryAfter(Exception error)
    {
        if (error is AggregateException aggregate)
            return aggregate.Flatten().InnerExceptions.Select(RequestedRetryAfter).Max();
        if (error is AuthenticationFailedException { InnerException: { } inner })
            return RequestedRetryAfter(inner);
        if (error is not RequestFailedException provider || provider.GetRawResponse() is not { } response)
            return null;
        if (response.Headers.TryGetValue("Retry-After", out var header) &&
            RetryConditionHeaderValue.TryParse(header, out var retry))
        {
            if (retry.Delta is { } delta)
                return delta;
            if (retry.Date is { } date)
            {
                var now = time.GetUtcNow();
                return date > now ? date - now : TimeSpan.Zero;
            }
        }
        if (response.Headers.TryGetValue("x-ms-retry-after-ms", out var milliseconds) &&
            long.TryParse(milliseconds, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return value <= TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond
                ? TimeSpan.FromTicks(value * TimeSpan.TicksPerMillisecond)
                : TimeSpan.MaxValue;
        return null;
    }

    private static bool IsPermanentProviderFailure(Exception failure) => failure switch
    {
        RequestFailedException response => IsConfigurationStatus(response.Status),
        AuthenticationFailedException { InnerException: RequestFailedException response } => IsConfigurationStatus(response.Status),
        _ => false
    };

    private static DomainException Configuration() => new(ErrorCode.DependencyUnavailable,
        "Private media configuration is missing or invalid.", isPermanentDependencyFailure: true);
    private static DomainException Unavailable(bool permanent = false, TimeSpan? retryAfter = null) => new(ErrorCode.DependencyUnavailable,
        "Private media storage is unavailable.", isPermanentDependencyFailure: permanent, retryAfter: retryAfter);
}
