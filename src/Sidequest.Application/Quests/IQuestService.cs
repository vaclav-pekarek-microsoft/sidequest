using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Quests;

/// <summary>Server-authorized Quest use cases keeping individual Event membership, equal ownership, invitations, and participation separate.</summary>
/// <remarks>Implementations reauthorize the eligible, non-departed current actor against the database on every call.
/// Mutations own operation-scoped contexts and explicit transactions for state, history, outbox, and scheduling.
/// No external provider calls occur inside SQL transactions; successful commands mean durable intent, not guaranteed external delivery.</remarks>
public interface IQuestService
{
    /// <summary>Lists Quests in the selected authorized view without leaking private/draft resources or suppressed counts.</summary>
    /// <param name="kind">Participation, ownership, discovery, history, joined-first Event-local board, or explicit moderation view.</param>
    /// <param name="eventId">Optional internal parent Event filter; null still restricts results to the actor's authorized scope.</param>
    /// <param name="page">One-based paging input with page size 1 through 100.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the query.</param>
    /// <returns>An authorized page with a matching total count and privacy-aware nullable participant counts.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Validation or authorization checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<PageResult<QuestSummary>> ListAsync(QuestListKind kind, Guid? eventId, PageRequest page,
        CancellationToken cancellationToken = default);
    /// <summary>Lists authorized Quests whose start instants match the optional range, filtering before totals and pagination.</summary>
    /// <param name="kind">Authorized participation, ownership, discovery, history, joined-first Event-local board, or moderation view.</param>
    /// <param name="eventId">Optional internal parent Event filter; absence never broadens authorization.</param>
    /// <param name="page">One-based paging input with page size 1 through 100.</param>
    /// <param name="dates">Inclusive lower and exclusive upper start-instant bounds; null endpoints are unbounded.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the query.</param>
    /// <returns>A privacy-filtered page and total count computed with the same date and access predicates.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Paging, date bounds, view selection, or authorization is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<PageResult<QuestSummary>> ListAsync(QuestListKind kind, Guid? eventId, PageRequest page,
        QuestDateFilter dates, CancellationToken cancellationToken = default);
    /// <summary>Reads authorized Quest detail through ordinary access or the separately audited private moderation path.</summary>
    /// <param name="id">Internal Quest identifier; knowing it grants no access.</param>
    /// <param name="moderation">Requests non-draft Event-owner moderation, which excludes invitation, attendee, and follower rosters.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the read and applicable access audit.</param>
    /// <returns>Authorized content with withheld counts/rosters represented as null, not fabricated zeros or empty collections.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The actor is forbidden or the resource is unavailable without existence disclosure.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<QuestDetail> GetAsync(Guid id, bool moderation = false, CancellationToken cancellationToken = default);
    /// <summary>Creates a Draft Quest for an eligible member of an Active, not-ended Event, atomically assigning the creator as first equal owner.</summary>
    /// <param name="eventId">Internal parent Event identifier supplying dates, zone, and individual membership boundary.</param>
    /// <param name="input">Local scheduling and content input; ownership assignment does not join or follow.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation without partial creation.</param>
    /// <returns>The new internal Quest identifier.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Membership, lifecycle, content, time mapping, or interval containment checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<Guid> CreateAsync(Guid eventId, QuestInput input, CancellationToken cancellationToken = default);
    /// <summary>Edits owner-managed Draft, Active, or Suspended content while the parent remains Active and not ended; never implicitly reinstates suspension.</summary>
    /// <param name="id">Internal Quest identifier.</param>
    /// <param name="version">Expected Base64 rowversion rejecting stale edits.</param>
    /// <param name="input">Replacement content and Event-zone scheduling input; published visibility remains fixed.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic edit.</param>
    /// <returns>A task completing after edit/history and applicable calendar, notification, and schedule intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, validation, lifecycle, fixed visibility, or concurrency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task EditAsync(Guid id, string version, QuestInput input, CancellationToken cancellationToken = default);
    /// <summary>Applies an allowed lifecycle transition; suspension/reinstatement requires Event ownership, not merely Quest ownership.</summary>
    /// <param name="id">Internal Quest identifier.</param>
    /// <param name="version">Expected Base64 rowversion.</param>
    /// <param name="target">Requested lifecycle state, subject to accepted transition and effective-time guards.</param>
    /// <param name="reason">Required explanation for moderation, cancellation, and other reason-requiring transitions.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic transition.</param>
    /// <returns>A task completing after state/history and applicable durable delivery effects are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Actor permissions, reason validation, lifecycle, or concurrency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task ChangeStatusAsync(Guid id, string version, QuestStatus target, string reason, CancellationToken cancellationToken = default);
    /// <summary>Hard-deletes an authorized unpublished Draft Quest subject to draft-deletion guards, retaining the required audit trail.</summary>
    /// <param name="id">Internal Draft Quest identifier.</param>
    /// <param name="version">Expected Base64 rowversion.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of atomic deletion.</param>
    /// <returns>A task completing after deletion and audit are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, draft state, or concurrency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task DeleteDraftAsync(Guid id, string version, CancellationToken cancellationToken = default);
    /// <summary>Changes only the current actor's exclusive participation; Join replaces Following, while Leave never starts or restores it.</summary>
    /// <param name="id">Internal accessible Quest identifier.</param>
    /// <param name="command">Repeat-safe participation command; Follow while Joined is a conflict.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the serialized participation and delivery changes.</param>
    /// <returns>A task completing after participation, history, and applicable calendar/reminder intent are persisted, or a valid repeat is a no-op.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access or lifecycle checks fail, command is invalid, or Follow conflicts with Joined.
    /// Join/Follow require Active resources before Quest end; Leave/Unfollow remain permitted in retained states.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task ParticipateAsync(Guid id, ParticipationCommand command, CancellationToken cancellationToken = default);
    /// <summary>Grants a named Event member access to an owner-managed private Active Quest without joining, following, or requiring acceptance.</summary>
    /// <param name="id">Internal private Quest identifier.</param>
    /// <param name="userId">Eligible recipient's internal account ID, not a directory object ID.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of grant and durable invitation creation.</param>
    /// <returns>A task completing after access grant, history, and mandatory invitation intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Ownership, target membership/eligibility, visibility, or lifecycle checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task InviteAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Revokes named invitation access and applicable participation, retaining independent owner-derived access and history.</summary>
    /// <param name="id">Internal Quest identifier managed by the actor.</param>
    /// <param name="userId">Internal invitee account identifier.</param>
    /// <param name="reason">Recorded revocation explanation for safe access-loss communication.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation without partial grant/participation cleanup.</param>
    /// <returns>A task completing after revocation, participation cleanup, and applicable durable calendar withdrawal intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, reason, or grant-state checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task RevokeInvitationAsync(Guid id, Guid userId, string reason, CancellationToken cancellationToken = default);
    /// <summary>Allows a Quest owner to remove an attendee with a mandatory reason; this is not a permanent public-Quest joining ban.</summary>
    /// <param name="id">Internal Quest identifier managed by the actor.</param>
    /// <param name="userId">Internal attendee account identifier.</param>
    /// <param name="reason">Mandatory reason, 10 through 2000 characters after trimming.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of attendance removal and delivery intent.</param>
    /// <returns>A task completing after removal, history, and required reason/calendar withdrawal intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Authorization, reason, or current-state checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task RemoveAttendeeAsync(Guid id, Guid userId, string reason, CancellationToken cancellationToken = default);
    /// <summary>Assigns an eligible existing Event member as an equal Quest owner, granting role-derived access but not participation.</summary>
    /// <param name="id">Internal Quest identifier already managed by the actor.</param>
    /// <param name="userId">Internal target account identifier; individual Event membership is required.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic ownership change.</param>
    /// <returns>A task completing after owner assignment, audit, and ownership notification intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, target eligibility/membership, or lifecycle checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task AddOwnerAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Removes equal ownership while atomically retaining at least one eligible owner; separate invitation access remains independent.</summary>
    /// <param name="id">Internal Quest identifier managed by the actor.</param>
    /// <param name="userId">Internal owner account identifier; may be the current actor.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the serialized ownership change.</param>
    /// <returns>A task completing after ownership removal, audit, and required notification intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, lifecycle, or last-eligible-owner guards fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task RemoveOwnerAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Reads authorized Quest history, keeping moderation records restricted to permitted owners/moderators.</summary>
    /// <param name="id">Internal Quest identifier.</param>
    /// <param name="moderation">Selects the explicit non-draft Event-owner moderation view rather than ordinary Quest access.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of history retrieval.</param>
    /// <returns>Only action history the actor may see; ordinary access does not imply full moderation history.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The Quest or selected history view is unavailable to the actor.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<IReadOnlyList<QuestHistoryItem>> HistoryAsync(Guid id, bool moderation = false, CancellationToken cancellationToken = default);
    /// <summary>Builds a minimal online-authorized snapshot of the actor's joined Quests for per-account offline display.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of snapshot retrieval.</param>
    /// <returns>Joined-only display data, excluding descriptions, rosters, contacts, images, invitations, and tokens; local storage never grants authorization.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current identity or account eligibility is forbidden.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<IReadOnlyList<OfflineQuest>> GetOfflineJoinedAsync(CancellationToken cancellationToken = default);
}
