namespace Sidequest.Application.Abstractions;

/// <summary>Directory selection/resolution port, used outside SQL transactions and never for ongoing group-based resource authorization.</summary>
public interface IDirectoryGateway
{
    /// <summary>Searches directory users for explicit individual selection.</summary>
    /// <param name="query">Directory search text subject to adapter and application input limits.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of remote directory work.</param>
    /// <returns>Matching directory users, or an empty list when none match; failures must not masquerade as empty success.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default);
    /// <summary>Searches supported same-tenant groups for a one-time bulk action.</summary>
    /// <param name="query">Directory group search text.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of remote directory work.</param>
    /// <returns>Selectable groups, without granting group-derived access.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, CancellationToken cancellationToken = default);
    /// <summary>Fully expands a supported group into eligible individuals before any membership changes are applied.</summary>
    /// <param name="groupId">Same-tenant Entra security or Microsoft 365 group object ID.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation across all directory pages and nested expansion.</param>
    /// <returns>Completed eligible-user expansion for deduplicated snapshot persistence; not an atomic directory point-in-time guarantee.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Unsupported, unauthorized, or incomplete expansion must be reported as failure rather than partial success.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<IReadOnlyList<DirectoryUser>> ExpandGroupAsync(Guid groupId, CancellationToken cancellationToken = default);
    /// <summary>Resolves a selected directory user without trusting submitted display or contact details.</summary>
    /// <param name="objectId">Entra user object identifier within the configured tenant.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of directory retrieval.</param>
    /// <returns>The resolved directory identity and eligibility result; callers still enforce current local authorization.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<DirectoryUser> GetUserAsync(Guid objectId, CancellationToken cancellationToken = default);
}
