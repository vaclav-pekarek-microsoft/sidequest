using Sidequest.Domain.Model;

namespace Sidequest.Application.Abstractions;

/// <summary>Authenticated external identity snapshot; does not itself establish current application eligibility or resource access.</summary>
/// <param name="TenantId">Entra tenant identifier forming an external key with ObjectId.</param>
/// <param name="ObjectId">Entra user object identifier, not the application's internal UserAccount.Id.</param>
/// <param name="DisplayName">Mutable display data, never an authorization key.</param>
/// <param name="Email">Mutable contact data, never an authorization key.</param>
public sealed record UserIdentity(Guid TenantId, Guid ObjectId, string DisplayName, string Email);

/// <summary>Supplies the current authenticated identity; callers must authorize it against current database state through <see cref="IResourceAccess"/>.</summary>
public interface ICurrentUser
{
    /// <summary>Reads the current external identity without granting application or resource permissions.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of identity retrieval.</param>
    /// <returns>The authenticated identity, or null when no usable signed-in identity is available.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed during retrieval.</exception>
    ValueTask<UserIdentity?> GetIdentityAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reauthorizes the current identity against eligible, non-departed accounts and individual resource relations in the supplied context.</summary>
/// <remarks>Actor user IDs must match the current identity's internal account ID. Administrator status is not a resource access bypass.
/// These reads do not save or commit; the caller owns the operation's context and transaction.</remarks>
public interface IResourceAccess
{
    /// <summary>Resolves the signed-in identity to an eligible, non-departed local account.</summary>
    /// <param name="db">Caller-owned per-operation context used for current authorization facts.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of identity and database reads.</param>
    /// <returns>The matching internal user account.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Identity is absent, unmapped, ineligible, or departed (Forbidden).</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<UserAccount> RequireUserAsync(ISidequestDbContext db, CancellationToken cancellationToken = default);
    /// <summary>Requires current user eligibility and an explicit database administrator assignment.</summary>
    /// <param name="db">Caller-owned per-operation context.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of authorization reads.</param>
    /// <returns>The authorized administrator account without granting implicit Event or Quest access.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current user eligibility or the administrator assignment is absent (Forbidden).</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<UserAccount> RequireAdministratorAsync(ISidequestDbContext db, CancellationToken cancellationToken = default);
    /// <summary>Requires full Event read access and, optionally, equal-owner permission; does not return discovery-only content.</summary>
    /// <param name="db">Caller-owned per-operation context.</param>
    /// <param name="eventId">Internal Event identifier.</param>
    /// <param name="userId">Internal actor account ID, required to match the eligible current identity.</param>
    /// <param name="ownerOnly">Whether Event ownership is required in addition to the ordinary read predicate.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of authorization reads.</param>
    /// <returns>The authorized Event; Draft access is owner-only and non-draft full access requires active membership.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current account is forbidden, or the resource/actor/access check fails with a non-disclosing NotFound outcome.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<Event> RequireEventAsync(ISidequestDbContext db, Guid eventId, Guid userId, bool ownerOnly = false, CancellationToken cancellationToken = default);
    /// <summary>Requires ordinary Quest access or the explicit non-draft Event-owner moderation path, subject to parent Event access.</summary>
    /// <param name="db">Caller-owned per-operation context.</param>
    /// <param name="questId">Internal Quest identifier.</param>
    /// <param name="userId">Internal actor account ID, required to match the eligible, non-departed current identity.</param>
    /// <param name="ownerOnly">Whether Quest ownership is additionally required, including when moderation is requested.</param>
    /// <param name="moderation">Selects Event-owner moderation instead of ordinary visibility/invitation rules; never grants draft access or roster disclosure.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of authorization reads.</param>
    /// <returns>The authorized Quest. Callers remain responsible for privacy-filtered projections and private moderation-access auditing.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current account is forbidden, or missing resources and failed access checks produce a non-disclosing NotFound outcome.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<Quest> RequireQuestAsync(ISidequestDbContext db, Guid questId, Guid userId, bool ownerOnly = false, bool moderation = false, CancellationToken cancellationToken = default);
}

/// <summary>Trusted directory user result for identity resolution and explicit audience selection, not an Event access grant.</summary>
/// <param name="TenantId">Entra tenant identifier.</param>
/// <param name="ObjectId">Entra user object identifier, not an internal account ID.</param>
/// <param name="DisplayName">Mutable directory display label.</param>
/// <param name="Email">Directory-resolved contact address; unusable values must not be treated as successful email destinations.</param>
/// <param name="IsEligible">Whether directory eligibility policy accepts this workforce account.</param>
public sealed record DirectoryUser(Guid TenantId, Guid ObjectId, string DisplayName, string Email, bool IsEligible);
/// <summary>Selectable directory group for a one-time expansion, never a membership or ownership grant.</summary>
/// <param name="ObjectId">Entra group object identifier used as an operational bulk input.</param>
/// <param name="DisplayName">Directory display label for group selection.</param>
public sealed record DirectoryGroup(Guid ObjectId, string DisplayName);

/// <summary>Directory selection/resolution port, used outside SQL transactions and never for ongoing group-based resource authorization.</summary>
public interface IDirectoryGateway
{
    /// <summary>Searches directory users for explicit individual selection.</summary>
    /// <param name="query">Directory search text subject to adapter and application input limits.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of remote directory work.</param>
    /// <returns>Matching directory users, or an empty list when none match; failures must not masquerade as empty success.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default);
    /// <summary>Searches supported same-tenant groups for a one-time bulk action.</summary>
    /// <param name="query">Directory group search text.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of remote directory work.</param>
    /// <returns>Selectable groups, without granting group-derived access.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, CancellationToken cancellationToken = default);
    /// <summary>Fully expands a supported group into eligible individuals before any membership changes are applied.</summary>
    /// <param name="groupId">Same-tenant Entra security or Microsoft 365 group object ID.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation across all directory pages and nested expansion.</param>
    /// <returns>Completed eligible-user expansion for deduplicated snapshot persistence; not an atomic directory point-in-time guarantee.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Unsupported, unauthorized, or incomplete expansion must be reported as failure rather than partial success.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<IReadOnlyList<DirectoryUser>> ExpandGroupAsync(Guid groupId, CancellationToken cancellationToken = default);
    /// <summary>Resolves a selected directory user without trusting submitted display or contact details.</summary>
    /// <param name="objectId">Entra user object identifier within the configured tenant.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of directory retrieval.</param>
    /// <returns>The resolved directory identity and eligibility result; callers still enforce current local authorization.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<DirectoryUser> GetUserAsync(Guid objectId, CancellationToken cancellationToken = default);
}
