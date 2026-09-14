using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Sidequest.Domain.Rules;

namespace Sidequest.Infrastructure.Directory;

/// <summary>Tenant-specific Graph client-credential acquisition and in-memory token caching through Azure.Identity.</summary>
/// <remarks>Credential construction is lazy so missing directory configuration does not disable existing Event access.
/// The SDK uses the supplied HTTP transport and its normal authority validation; no persistent token cache,
/// developer-credential fallback, or synthetic identity is enabled.</remarks>
/// <param name="http">Dedicated HTTP client whose lifetime is managed by the host.</param>
/// <param name="credentials">Protected deployment credentials, never user input.</param>
/// <param name="directory">Directory tenant binding against which credentials are validated.</param>
public sealed class GraphClientCredentialTokenProvider(HttpClient http, GraphClientCredentialOptions credentials,
    GraphDirectoryOptions directory) : IGraphAccessTokenProvider
{
    private readonly Lazy<ClientSecretCredential> credential = new(() => new ClientSecretCredential(
        credentials.TenantId.ToString("D"), credentials.ClientId.ToString("D"), credentials.ClientSecret,
        new ClientSecretCredentialOptions { Transport = new HttpClientTransport(http) }));

    /// <inheritdoc />
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        directory.Validate();
        if (credentials.TenantId != directory.TenantId || credentials.ClientId == Guid.Empty ||
            string.IsNullOrWhiteSpace(credentials.ClientSecret))
            throw Failure("Directory credentials are missing or do not match the configured tenant.");
        try
        {
            var token = await credential.Value.GetTokenAsync(
                new TokenRequestContext(["https://graph.microsoft.com/.default"]), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token.Token))
                throw Failure("Directory token response was invalid.");
            return token.Token;
        }
        catch (AuthenticationFailedException)
        {
            throw Failure("Directory token acquisition failed. Check approved credentials and consent.");
        }
        catch (RequestFailedException)
        {
            throw Failure("Directory token service is unavailable.");
        }
        catch (HttpRequestException)
        {
            throw Failure("Directory token service is unavailable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("Directory token acquisition timed out.");
        }
    }

    private static DomainException Failure(string message) => new(ErrorCode.DependencyUnavailable, message);
}
