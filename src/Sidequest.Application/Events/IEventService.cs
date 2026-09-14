using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Events;

/// <summary>Server-authorized Event, individual audience, and equal-ownership use cases.</summary>
/// <remarks>Implementations resolve the current eligible, non-departed actor through database authorization.
/// Mutations own a per-operation context and explicit transaction covering state, audit, outbox, and schedules.
/// Directory/provider calls occur outside SQL transactions. Lifecycle, last-owner, and optimistic-concurrency guards remain authoritative.</remarks>
public interface IEventService
{
    /// <summary>Lists only Events visible in the requested membership, discovery, or history view.</summary>
    /// <param name="kind">Authorized view selector.</param>
    /// <param name="page">One-based paging input with page size 1 through 100.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the query.</param>
    /// <returns>A privacy-filtered page and matching authorized total count.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Identity/access is forbidden or query input is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<PageResult<EventSummary>> ListAsync(EventListKind kind, PageRequest page, CancellationToken cancellationToken = default);
    /// <summary>Reads full Event content for authorized readers or only discovery content where policy permits.</summary>
    /// <param name="id">Internal Event identifier; a known URL is not a membership grant.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the read.</param>
    /// <returns>The authorized summary and nullable full description.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The actor is forbidden or the Event is unavailable without disclosing protected content.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<EventDetail> GetAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Creates an unpublished Event and atomically assigns the eligible creator as its first equal owner and individual member.</summary>
    /// <param name="input">Proposed Event configuration.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation; the operation must not commit partial state.</param>
    /// <returns>The new internal Event identifier.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Input is invalid or the current account is forbidden.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<Guid> CreateAsync(EventInput input, CancellationToken cancellationToken = default);
    /// <summary>Updates owner-managed Draft/Active configuration before Event end, preserving published zone and child-interval constraints.</summary>
    /// <param name="id">Internal Event identifier.</param>
    /// <param name="version">Expected Base64 rowversion used to reject stale edits.</param>
    /// <param name="input">Validated replacement configuration.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic edit.</param>
    /// <returns>A task completing after the edit and related audit/scheduling changes are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Validation, unavailable access, lifecycle, or concurrency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task EditAsync(Guid id, string version, EventInput input, CancellationToken cancellationToken = default);
    /// <summary>Applies an authorized Event lifecycle transition with atomic history, child effects, and durable delivery intent.</summary>
    /// <param name="id">Internal Event identifier.</param>
    /// <param name="version">Expected Base64 rowversion.</param>
    /// <param name="target">Requested lifecycle state; only accepted transitions are allowed.</param>
    /// <param name="reason">Action explanation, mandatory for cancellation and other reason-requiring transitions.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation without committing a partial cascade.</param>
    /// <returns>A task completing after the transition transaction is persisted, not after external delivery.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, reason validation, lifecycle guards, or concurrency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task ChangeStatusAsync(Guid id, string version, EventStatus target, string reason, CancellationToken cancellationToken = default);
    /// <summary>Hard-deletes an owner-authorized unpublished Draft only when no requests, invitations, or Quests exist, retaining deletion audit.</summary>
    /// <param name="id">Internal Draft Event identifier.</param>
    /// <param name="version">Expected Base64 rowversion.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic deletion.</param>
    /// <returns>A task completing when deletion and its audit are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The resource is unavailable or draft/dependency/concurrency guards fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task DeleteDraftAsync(Guid id, string version, CancellationToken cancellationToken = default);
    /// <summary>Finds potentially duplicate Events without disclosing Events outside the actor's discovery permissions.</summary>
    /// <param name="name">Proposed Event name for similarity comparison.</param>
    /// <param name="start">Proposed inclusive first local date.</param>
    /// <param name="end">Proposed inclusive last local date.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of duplicate lookup.</param>
    /// <returns>At most five discoverable Active candidates with overlapping inclusive dates, ordered by descending normalized name similarity; warnings do not prohibit creation.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Input is invalid or the actor is forbidden.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<IReadOnlyList<DuplicateEvent>> FindDuplicatesAsync(string name, DateOnly start, DateOnly end, CancellationToken cancellationToken = default);
    /// <summary>Submits the actor's repeat-safe membership request to an Active, not-ended Event without granting access.</summary>
    /// <param name="eventId">Internal discoverable Event identifier.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of request persistence.</param>
    /// <returns>A task completing after request and manager-notification intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Eligibility, availability, lifecycle, or request guards fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task RequestMembershipAsync(Guid eventId, CancellationToken cancellationToken = default);
    /// <summary>Withdraws the current actor's pending membership request while retaining request history.</summary>
    /// <param name="requestId">Internal request identifier owned by the current actor.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of withdrawal.</param>
    /// <returns>A task completing when the withdrawal is persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The request is unavailable to the actor or its state prevents withdrawal.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task WithdrawRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
    /// <summary>Records an Event owner's decision; approval atomically activates individual membership and resolves related pending work.</summary>
    /// <param name="requestId">Internal pending request identifier.</param>
    /// <param name="approve">True to approve membership; false to reject with a requester-visible reason.</param>
    /// <param name="reason">Decision explanation, mandatory for rejection.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic decision.</param>
    /// <returns>A task completing after decision, membership effects, audit, and delivery intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, reason, current request state, or Event lifecycle guards fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task DecideRequestAsync(Guid requestId, bool approve, string reason, CancellationToken cancellationToken = default);
    /// <summary>Lists membership requests only within the actor's authorized requester/manager scope.</summary>
    /// <param name="eventId">Optional internal Event filter; null does not bypass authorization or expose all users' requests.</param>
    /// <param name="page">Validated one-based paging input.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the query.</param>
    /// <returns>An authorized request page and matching total count.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Input or authorization checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<PageResult<RequestSummary>> ListRequestsAsync(Guid? eventId, PageRequest page, CancellationToken cancellationToken = default);
    /// <summary>Reads the individual Event audience only with member-content authorization, never through discovery alone.</summary>
    /// <param name="eventId">Internal Event identifier.</param>
    /// <param name="page">Validated one-based paging input.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the query.</param>
    /// <returns>A page of authorized membership rows without email roster fields.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The Event is unavailable or paging input is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<PageResult<MembershipSummary>> ListMembersAsync(Guid eventId, PageRequest page, CancellationToken cancellationToken = default);
    /// <summary>Adds an individually selected eligible user as an Event owner-authorized membership operation, without restoring Quest participation.</summary>
    /// <param name="eventId">Internal target Event identifier, subject to audience lifecycle guards.</param>
    /// <param name="directoryObjectId">Entra user object ID to resolve outside the SQL transaction, not an internal account ID.</param>
    /// <param name="restore">Explicit consent to reactivate a previously removed membership rather than silently restoring it.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of resolution and atomic membership work.</param>
    /// <returns>A task completing after individual membership and applicable pending-work/delivery effects are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Eligibility, access, lifecycle, restoration, or directory dependency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task AddMemberAsync(Guid eventId, Guid directoryObjectId, bool restore, CancellationToken cancellationToken = default);
    /// <summary>Deactivates individual membership with owner authorization, invalidating child grants/participation and queuing required withdrawals.</summary>
    /// <param name="eventId">Internal Event identifier.</param>
    /// <param name="userId">Internal member account identifier; ordinary removal requires prior removal from Event/child owner lists.</param>
    /// <param name="reason">Mandatory removal explanation.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation without partial membership cleanup.</param>
    /// <returns>A task completing after membership, child cleanup, history, and durable delivery intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, reason, or ownership-continuity guards fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task RemoveMemberAsync(Guid eventId, Guid userId, string reason, CancellationToken cancellationToken = default);
    /// <summary>Deactivates the current actor's Event membership through the same child-access cleanup and ownership-continuity rules as removal.</summary>
    /// <param name="eventId">Internal Event identifier to leave.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of atomic membership cleanup.</param>
    /// <returns>A task completing after membership removal and applicable withdrawals are durably recorded.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access or remaining owner assignments prevent leaving.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task LeaveAsync(Guid eventId, CancellationToken cancellationToken = default);
    /// <summary>Creates a repeat-safe named invitation to an Active, not-ended Event; membership begins only on acceptance or explicit activation.</summary>
    /// <param name="eventId">Internal Event identifier managed by the actor.</param>
    /// <param name="directoryObjectId">Eligible recipient's Entra user object ID, resolved outside the SQL transaction.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of resolution and invitation creation.</param>
    /// <returns>A task completing after invitation and mandatory delivery intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Actor/recipient eligibility, lifecycle, or directory dependency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task InviteAsync(Guid eventId, Guid directoryObjectId, CancellationToken cancellationToken = default);
    /// <summary>Records the named recipient's response; acceptance atomically activates individual membership without restoring prior Quest state.</summary>
    /// <param name="invitationId">Internal Event invitation identifier belonging to the current actor.</param>
    /// <param name="accept">True to activate membership; false to decline.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic response.</param>
    /// <returns>A task completing after response and applicable membership/delivery effects are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The invitation is unavailable, expired, resolved incompatibly, or its Event no longer permits acceptance.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task RespondToInvitationAsync(Guid invitationId, bool accept, CancellationToken cancellationToken = default);
    /// <summary>Allows an Event owner to withdraw an outstanding invitation without treating it as membership removal.</summary>
    /// <param name="invitationId">Internal pending Event invitation identifier.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of revocation.</param>
    /// <returns>A task completing after invitation revocation is persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access or invitation-state checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default);
    /// <summary>Lists Event invitations only within the current recipient's or authorized manager's scope.</summary>
    /// <param name="eventId">Optional internal Event filter; null remains restricted to the actor's authorized scope.</param>
    /// <param name="page">Validated one-based paging input.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the query.</param>
    /// <returns>An authorized invitation page with a matching total count.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Input or access checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<PageResult<EventInvitationSummary>> ListInvitationsAsync(Guid? eventId, PageRequest page, CancellationToken cancellationToken = default);
    /// <summary>Adds an eligible equal owner with required individual membership, auditing and notifying the ownership change.</summary>
    /// <param name="eventId">Internal Event identifier currently managed by the actor.</param>
    /// <param name="directoryObjectId">New owner's Entra user object ID, not an internal account ID.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of resolution and atomic assignment.</param>
    /// <returns>A task completing after ownership, required membership, audit, and delivery intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, target eligibility, lifecycle, or dependency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task AddOwnerAsync(Guid eventId, Guid directoryObjectId, CancellationToken cancellationToken = default);
    /// <summary>Removes an equal owner assignment, serializing competing removals to retain at least one eligible owner.</summary>
    /// <param name="eventId">Internal Event identifier managed by the actor.</param>
    /// <param name="userId">Internal owner account identifier; may be the current actor.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic assignment change.</param>
    /// <returns>A task completing after assignment removal, audit, and ownership delivery intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, lifecycle, or last-eligible-owner guards fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task RemoveOwnerAsync(Guid eventId, Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Starts a one-time background group expansion, saving a complete recipient snapshot before individual add/invite operations.</summary>
    /// <param name="eventId">Internal Active, not-ended Event identifier managed by the actor.</param>
    /// <param name="groupId">Supported same-tenant Entra group object ID retained only for operational/audit use.</param>
    /// <param name="mode">Direct add or consent-based invitation; bulk add must not silently restore removed members.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of durable workflow creation.</param>
    /// <returns>The new bulk operation identifier, not a promise that expansion or recipient application has completed.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Authorization, lifecycle, group/mode validation, or operation limits fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<Guid> StartBulkAsync(Guid eventId, Guid groupId, BulkMode mode, CancellationToken cancellationToken = default);
    /// <summary>Reads authorized bulk progress, including skips and partial failures, without implying ongoing group synchronization.</summary>
    /// <param name="operationId">Internal bulk operation identifier.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the read.</param>
    /// <returns>Current recorded workflow and recipient counts.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The operation is unavailable to the current actor.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<BulkOperationSummary> GetBulkAsync(Guid operationId, CancellationToken cancellationToken = default);
    /// <summary>Searches eligible directory candidates for explicit user selection without granting resource access.</summary>
    /// <param name="query">User search text subject to input/rate limits.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the external directory query.</param>
    /// <returns>Authorized directory selection results; application commands revalidate selected identities.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Authorization, validation, or directory dependency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default);
    /// <summary>Searches supported groups for an explicit one-time bulk action, never for group-based authorization.</summary>
    /// <param name="query">Group search text subject to input/rate limits.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the external directory query.</param>
    /// <returns>Authorized same-tenant group selection results.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Authorization, validation, or directory dependency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, CancellationToken cancellationToken = default);
}
