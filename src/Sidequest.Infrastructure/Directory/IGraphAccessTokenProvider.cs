namespace Sidequest.Infrastructure.Directory;

/// <summary>Supplies server-held application Graph tokens; never accepts a browser-supplied token or authorizes Event access.</summary>
public interface IGraphAccessTokenProvider
{
    /// <summary>Acquires a token for the configured tenant and https://graph.microsoft.com/.default scope.</summary>
    /// <param name="cancellationToken">Cancels token acquisition.</param>
    /// <returns>A bearer token for server-only use; failures must be explicit.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Approved tenant configuration, credentials, or token-provider availability is missing.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels acquisition.</exception>
    public Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
}
