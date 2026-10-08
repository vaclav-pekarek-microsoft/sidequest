namespace Sidequest.Application.Abstractions;

/// <summary>Directory user selection and resolution port used outside SQL transactions.</summary>
public interface IDirectoryGateway
{
    /// <summary>Searches directory users for explicit individual selection.</summary>
    /// <param name="query">Directory search text subject to adapter and application input limits.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of remote directory work.</param>
    /// <returns>Matching directory users, or an empty list when none match; failures must not masquerade as empty success.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default);
    /// <summary>Resolves a selected directory user without trusting submitted display or contact details.</summary>
    /// <param name="objectId">Entra user object identifier within the configured tenant.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of directory retrieval.</param>
    /// <returns>The resolved directory identity and eligibility result; callers still enforce current local authorization.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<DirectoryUser> GetUserAsync(Guid objectId, CancellationToken cancellationToken = default);
}
