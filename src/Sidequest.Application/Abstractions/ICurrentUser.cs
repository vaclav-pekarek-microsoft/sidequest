namespace Sidequest.Application.Abstractions;

/// <summary>Supplies the current authenticated identity; callers must authorize it against current database state through <see cref="IResourceAccess"/>.</summary>
public interface ICurrentUser
{
    /// <summary>Reads the current external identity without granting application or resource permissions.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of identity retrieval.</param>
    /// <returns>The authenticated identity, or null when no usable signed-in identity is available.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed during retrieval.</exception>
    public ValueTask<UserIdentity?> GetIdentityAsync(CancellationToken cancellationToken = default);
}
