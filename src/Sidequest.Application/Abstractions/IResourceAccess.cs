using Sidequest.Domain.Model;

namespace Sidequest.Application.Abstractions;

/// <summary>Reauthorizes the current identity against eligible, non-departed accounts and individual resource relations in the supplied context.</summary>
/// <remarks>Actor user IDs must match the current identity's internal account ID. Administrator status is not a resource access bypass.
/// These reads do not save or commit; the caller owns the operation's context and transaction.
/// Do not run concurrent authorization queries against the same context.</remarks>
public interface IResourceAccess
{
    /// <summary>Resolves the signed-in identity to an eligible, non-departed local account.</summary>
    /// <param name="db">Caller-owned per-operation context used for current authorization facts.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of identity and database reads.</param>
    /// <returns>The matching internal user account.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Identity is absent, unmapped, ineligible, or departed (Forbidden).</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<UserAccount> RequireUserAsync(ISidequestDbContext db, CancellationToken cancellationToken = default);
    /// <summary>Requires current user eligibility and an explicit database administrator assignment.</summary>
    /// <param name="db">Caller-owned per-operation context.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of authorization reads.</param>
    /// <returns>The authorized administrator account without granting implicit Event or Quest access.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current user eligibility or the administrator assignment is absent (Forbidden).</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<UserAccount> RequireAdministratorAsync(ISidequestDbContext db, CancellationToken cancellationToken = default);
    /// <summary>Requires full Event read access and, optionally, equal-owner permission; does not return discovery-only content.</summary>
    /// <param name="db">Caller-owned per-operation context.</param>
    /// <param name="eventId">Internal Event identifier.</param>
    /// <param name="userId">Internal actor account ID, required to match the eligible current identity.</param>
    /// <param name="ownerOnly">Whether Event ownership is required in addition to the ordinary read predicate.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of authorization reads.</param>
    /// <returns>The authorized Event. Eligibility and active membership are always required;
    /// drafts and Events cancelled without publication remain owner-only, including after archival.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current account is forbidden, or the resource/actor/access check fails with a non-disclosing NotFound outcome.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<Event> RequireEventAsync(ISidequestDbContext db, Guid eventId, Guid userId, bool ownerOnly = false, CancellationToken cancellationToken = default);
    /// <summary>Requires ordinary Quest access or the explicit non-draft Event-owner moderation path, subject to parent Event access.</summary>
    /// <param name="db">Caller-owned per-operation context.</param>
    /// <param name="questId">Internal Quest identifier.</param>
    /// <param name="userId">Internal actor account ID, required to match the eligible, non-departed current identity.</param>
    /// <param name="ownerOnly">Whether Quest ownership is additionally required, including when moderation is requested.</param>
    /// <param name="moderation">Selects Event-owner moderation instead of ordinary visibility/invitation rules; never grants draft access or roster disclosure.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of authorization reads.</param>
    /// <returns>The authorized Quest. A cancelled unpublished draft remains owner-only, including after archival,
    /// and is never exposed through moderation. Callers remain responsible for privacy-filtered projections
    /// and private moderation-access auditing.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current account is forbidden, or missing resources and failed access checks produce a non-disclosing NotFound outcome.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    /// <example>
    /// <code>
    /// await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
    /// var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
    /// var quest = await access.RequireQuestAsync(
    ///     db, questId, actor.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
    /// // Build an authorized projection; never substitute a submitted user ID for actor.Id.
    /// </code>
    /// </example>
    public Task<Quest> RequireQuestAsync(ISidequestDbContext db, Guid questId, Guid userId, bool ownerOnly = false, bool moderation = false, CancellationToken cancellationToken = default);
}
