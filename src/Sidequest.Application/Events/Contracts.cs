using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Events;

/// <summary>Authorized Event list views separating current membership, discovery, and retained history.</summary>
public enum EventListKind
{
    /// <summary>Current Events associated with the actor's individual membership or ownership.</summary>
    Mine,
    /// <summary>Active Event discovery summaries visible to eligible users without granting full content access.</summary>
    Available,
    /// <summary>Historical Events still readable through the actor's retained access.</summary>
    History
}
/// <summary>Minimal named-user projection for rosters without email disclosure.</summary>
/// <param name="Id">Internal account identifier, not an Entra object identifier.</param>
/// <param name="DisplayName">Directory-derived display label.</param>
public sealed record PersonSummary(Guid Id, string DisplayName);
/// <summary>Equal-owner directory contact projection; ordering does not imply a primary owner.</summary>
/// <param name="Id">Internal owner account identifier.</param>
/// <param name="DisplayName">Owner's directory-derived display label.</param>
/// <param name="Email">Owner's directory contact address, disclosed only where owner-contact policy allows.</param>
public sealed record OwnerSummary(Guid Id, string DisplayName, string Email);
/// <summary>Event projection usable for authorized discovery without exposing full descriptions, membership rosters, or counts.</summary>
/// <param name="Id">Internal Event identifier.</param>
/// <param name="Name">Event display name.</param>
/// <param name="DiscoverySummary">Short discovery-safe description.</param>
/// <param name="StartDate">Inclusive first date in the Event's zone.</param>
/// <param name="EndDate">Inclusive last date in the Event's zone.</param>
/// <param name="TimeZoneId">IANA Event zone inherited by its Quests.</param>
/// <param name="Status">Lifecycle state presented by the authorized query.</param>
/// <param name="Owners">Equal owners' directory contacts; no primary-owner rank.</param>
/// <param name="IsMember">Whether the current actor has active individual membership.</param>
/// <param name="IsOwner">Whether the current actor is an Event owner.</param>
/// <param name="Version">Opaque Base64 SQL rowversion for optimistic concurrency, not a time value.</param>
public sealed record EventSummary(Guid Id, string Name, string DiscoverySummary, DateOnly StartDate,
    DateOnly EndDate, string TimeZoneId, EventStatus Status, IReadOnlyList<OwnerSummary> Owners,
    bool IsMember, bool IsOwner, string Version);
/// <summary>Event detail with full content omitted when the actor has discovery-only access.</summary>
/// <param name="Summary">Authorized Event summary.</param>
/// <param name="Description">Full description for an authorized reader, or null when privacy limits the result to discovery content.</param>
public sealed record EventDetail(EventSummary Summary, string? Description);
/// <summary>Event configuration input; the application validates dates, text, lifecycle, and existing child intervals.</summary>
/// <param name="Name">Proposed Event name, 3 through 120 characters after trimming.</param>
/// <param name="Description">Full member-only plain-text description, at most 10000 characters after trimming.</param>
/// <param name="DiscoverySummary">Eligible-user discovery text, at most 300 characters after trimming.</param>
/// <param name="StartDate">Inclusive first local Event date.</param>
/// <param name="EndDate">Inclusive last local Event date, not earlier than StartDate.</param>
/// <param name="TimeZoneId">Valid IANA zone; cannot be changed after publication.</param>
public sealed record EventInput(string Name, string Description, string DiscoverySummary,
    DateOnly StartDate, DateOnly EndDate, string TimeZoneId);
/// <summary>Authorized individual audience row, without group-derived grants.</summary>
/// <param name="User">Member identity projected without email.</param>
/// <param name="Status">Active or removed individual membership.</param>
/// <param name="IsOwner">Whether the person also holds equal Event ownership.</param>
public sealed record MembershipSummary(PersonSummary User, MembershipStatus Status, bool IsOwner);
/// <summary>Authorized membership request projection with retained decision information.</summary>
/// <param name="Id">Internal request identifier.</param>
/// <param name="EventId">Internal requested Event identifier.</param>
/// <param name="EventName">Authorized Event display name.</param>
/// <param name="User">Requester identity without email disclosure.</param>
/// <param name="Status">Pending, decided, or withdrawn state.</param>
/// <param name="Reason">Recorded decision explanation, including rejection reasons.</param>
/// <param name="CreatedUtc">UTC instant when the request was submitted.</param>
public sealed record RequestSummary(Guid Id, Guid EventId, string EventName, PersonSummary User,
    MembershipRequestStatus Status, string Reason, DateTimeOffset CreatedUtc);
/// <summary>Authorized consent-based Event invitation projection, distinct from a Quest access grant.</summary>
/// <param name="Id">Internal invitation identifier.</param>
/// <param name="EventId">Internal inviting Event identifier.</param>
/// <param name="EventName">Authorized Event display name.</param>
/// <param name="User">Invited person's internal identity projection.</param>
/// <param name="Status">Pending or terminal invitation state.</param>
/// <param name="ExpiresUtc">UTC deadline bounded by seven days and the Event's local end boundary.</param>
public sealed record EventInvitationSummary(Guid Id, Guid EventId, string EventName,
    PersonSummary User, EventInvitationStatus Status, DateTimeOffset ExpiresUtc);
/// <summary>Potentially duplicate discoverable Event presented as a warning rather than an automatic creation ban.</summary>
/// <param name="Event">Only the Event content the actor is permitted to discover.</param>
/// <param name="Similarity">Normalized name-word Jaccard similarity from zero through one; duplicate warnings use at least 0.6 or identical normalized names.</param>
public sealed record DuplicateEvent(EventSummary Event, double Similarity);
/// <summary>Manager-visible progress for a one-time group expansion and individual audience operation.</summary>
/// <param name="Id">Internal bulk operation identifier.</param>
/// <param name="Mode">Direct add or consent-based invitation mode.</param>
/// <param name="Status">Overall expansion/application state.</param>
/// <param name="Total">Number of recorded snapshot recipients; interpret with Status while expansion is incomplete.</param>
/// <param name="Applied">Number of recipients whose individual operation was applied.</param>
/// <param name="Skipped">Number of recipients skipped by idempotency or current-state safeguards.</param>
/// <param name="Failed">Number of recipients with recorded failures.</param>
/// <param name="Error">Safe operation-level error, or null when none is recorded.</param>
public sealed record BulkOperationSummary(Guid Id, BulkMode Mode, BulkStatus Status,
    int Total, int Applied, int Skipped, int Failed, string? Error);

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
    Task<PageResult<EventSummary>> ListAsync(EventListKind kind, PageRequest page, CancellationToken cancellationToken = default);
    /// <summary>Reads full Event content for authorized readers or only discovery content where policy permits.</summary>
    /// <param name="id">Internal Event identifier; a known URL is not a membership grant.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the read.</param>
    /// <returns>The authorized summary and nullable full description.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The actor is forbidden or the Event is unavailable without disclosing protected content.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<EventDetail> GetAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Creates an unpublished Event and atomically assigns the eligible creator as its first equal owner and individual member.</summary>
    /// <param name="input">Proposed Event configuration.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation; the operation must not commit partial state.</param>
    /// <returns>The new internal Event identifier.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Input is invalid or the current account is forbidden.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<Guid> CreateAsync(EventInput input, CancellationToken cancellationToken = default);
    /// <summary>Updates owner-managed Draft/Active configuration before Event end, preserving published zone and child-interval constraints.</summary>
    /// <param name="id">Internal Event identifier.</param>
    /// <param name="version">Expected Base64 rowversion used to reject stale edits.</param>
    /// <param name="input">Validated replacement configuration.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic edit.</param>
    /// <returns>A task completing after the edit and related audit/scheduling changes are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Validation, unavailable access, lifecycle, or concurrency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task EditAsync(Guid id, string version, EventInput input, CancellationToken cancellationToken = default);
    /// <summary>Applies an authorized Event lifecycle transition with atomic history, child effects, and durable delivery intent.</summary>
    /// <param name="id">Internal Event identifier.</param>
    /// <param name="version">Expected Base64 rowversion.</param>
    /// <param name="target">Requested lifecycle state; only accepted transitions are allowed.</param>
    /// <param name="reason">Action explanation, mandatory for cancellation and other reason-requiring transitions.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation without committing a partial cascade.</param>
    /// <returns>A task completing after the transition transaction is persisted, not after external delivery.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, reason validation, lifecycle guards, or concurrency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task ChangeStatusAsync(Guid id, string version, EventStatus target, string reason, CancellationToken cancellationToken = default);
    /// <summary>Hard-deletes an owner-authorized unpublished Draft only when no requests, invitations, or Quests exist, retaining deletion audit.</summary>
    /// <param name="id">Internal Draft Event identifier.</param>
    /// <param name="version">Expected Base64 rowversion.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic deletion.</param>
    /// <returns>A task completing when deletion and its audit are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The resource is unavailable or draft/dependency/concurrency guards fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task DeleteDraftAsync(Guid id, string version, CancellationToken cancellationToken = default);
    /// <summary>Finds potentially duplicate Events without disclosing Events outside the actor's discovery permissions.</summary>
    /// <param name="name">Proposed Event name for similarity comparison.</param>
    /// <param name="start">Proposed inclusive first local date.</param>
    /// <param name="end">Proposed inclusive last local date.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of duplicate lookup.</param>
    /// <returns>At most five discoverable Active candidates with overlapping inclusive dates, ordered by descending normalized name similarity; warnings do not prohibit creation.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Input is invalid or the actor is forbidden.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<IReadOnlyList<DuplicateEvent>> FindDuplicatesAsync(string name, DateOnly start, DateOnly end, CancellationToken cancellationToken = default);
    /// <summary>Submits the actor's repeat-safe membership request to an Active, not-ended Event without granting access.</summary>
    /// <param name="eventId">Internal discoverable Event identifier.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of request persistence.</param>
    /// <returns>A task completing after request and manager-notification intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Eligibility, availability, lifecycle, or request guards fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task RequestMembershipAsync(Guid eventId, CancellationToken cancellationToken = default);
    /// <summary>Withdraws the current actor's pending membership request while retaining request history.</summary>
    /// <param name="requestId">Internal request identifier owned by the current actor.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of withdrawal.</param>
    /// <returns>A task completing when the withdrawal is persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The request is unavailable to the actor or its state prevents withdrawal.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task WithdrawRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
    /// <summary>Records an Event owner's decision; approval atomically activates individual membership and resolves related pending work.</summary>
    /// <param name="requestId">Internal pending request identifier.</param>
    /// <param name="approve">True to approve membership; false to reject with a requester-visible reason.</param>
    /// <param name="reason">Decision explanation, mandatory for rejection.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic decision.</param>
    /// <returns>A task completing after decision, membership effects, audit, and delivery intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, reason, current request state, or Event lifecycle guards fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task DecideRequestAsync(Guid requestId, bool approve, string reason, CancellationToken cancellationToken = default);
    /// <summary>Lists membership requests only within the actor's authorized requester/manager scope.</summary>
    /// <param name="eventId">Optional internal Event filter; null does not bypass authorization or expose all users' requests.</param>
    /// <param name="page">Validated one-based paging input.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the query.</param>
    /// <returns>An authorized request page and matching total count.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Input or authorization checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<PageResult<RequestSummary>> ListRequestsAsync(Guid? eventId, PageRequest page, CancellationToken cancellationToken = default);
    /// <summary>Reads the individual Event audience only with member-content authorization, never through discovery alone.</summary>
    /// <param name="eventId">Internal Event identifier.</param>
    /// <param name="page">Validated one-based paging input.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the query.</param>
    /// <returns>A page of authorized membership rows without email roster fields.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The Event is unavailable or paging input is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<PageResult<MembershipSummary>> ListMembersAsync(Guid eventId, PageRequest page, CancellationToken cancellationToken = default);
    /// <summary>Adds an individually selected eligible user as an Event owner-authorized membership operation, without restoring Quest participation.</summary>
    /// <param name="eventId">Internal target Event identifier, subject to audience lifecycle guards.</param>
    /// <param name="directoryObjectId">Entra user object ID to resolve outside the SQL transaction, not an internal account ID.</param>
    /// <param name="restore">Explicit consent to reactivate a previously removed membership rather than silently restoring it.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of resolution and atomic membership work.</param>
    /// <returns>A task completing after individual membership and applicable pending-work/delivery effects are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Eligibility, access, lifecycle, restoration, or directory dependency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task AddMemberAsync(Guid eventId, Guid directoryObjectId, bool restore, CancellationToken cancellationToken = default);
    /// <summary>Deactivates individual membership with owner authorization, invalidating child grants/participation and queuing required withdrawals.</summary>
    /// <param name="eventId">Internal Event identifier.</param>
    /// <param name="userId">Internal member account identifier; ordinary removal requires prior removal from Event/child owner lists.</param>
    /// <param name="reason">Mandatory removal explanation.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation without partial membership cleanup.</param>
    /// <returns>A task completing after membership, child cleanup, history, and durable delivery intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, reason, or ownership-continuity guards fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task RemoveMemberAsync(Guid eventId, Guid userId, string reason, CancellationToken cancellationToken = default);
    /// <summary>Deactivates the current actor's Event membership through the same child-access cleanup and ownership-continuity rules as removal.</summary>
    /// <param name="eventId">Internal Event identifier to leave.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of atomic membership cleanup.</param>
    /// <returns>A task completing after membership removal and applicable withdrawals are durably recorded.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access or remaining owner assignments prevent leaving.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task LeaveAsync(Guid eventId, CancellationToken cancellationToken = default);
    /// <summary>Creates a repeat-safe named invitation to an Active, not-ended Event; membership begins only on acceptance or explicit activation.</summary>
    /// <param name="eventId">Internal Event identifier managed by the actor.</param>
    /// <param name="directoryObjectId">Eligible recipient's Entra user object ID, resolved outside the SQL transaction.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of resolution and invitation creation.</param>
    /// <returns>A task completing after invitation and mandatory delivery intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Actor/recipient eligibility, lifecycle, or directory dependency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task InviteAsync(Guid eventId, Guid directoryObjectId, CancellationToken cancellationToken = default);
    /// <summary>Records the named recipient's response; acceptance atomically activates individual membership without restoring prior Quest state.</summary>
    /// <param name="invitationId">Internal Event invitation identifier belonging to the current actor.</param>
    /// <param name="accept">True to activate membership; false to decline.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic response.</param>
    /// <returns>A task completing after response and applicable membership/delivery effects are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The invitation is unavailable, expired, resolved incompatibly, or its Event no longer permits acceptance.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task RespondToInvitationAsync(Guid invitationId, bool accept, CancellationToken cancellationToken = default);
    /// <summary>Allows an Event owner to withdraw an outstanding invitation without treating it as membership removal.</summary>
    /// <param name="invitationId">Internal pending Event invitation identifier.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of revocation.</param>
    /// <returns>A task completing after invitation revocation is persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access or invitation-state checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default);
    /// <summary>Lists Event invitations only within the current recipient's or authorized manager's scope.</summary>
    /// <param name="eventId">Optional internal Event filter; null remains restricted to the actor's authorized scope.</param>
    /// <param name="page">Validated one-based paging input.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the query.</param>
    /// <returns>An authorized invitation page with a matching total count.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Input or access checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<PageResult<EventInvitationSummary>> ListInvitationsAsync(Guid? eventId, PageRequest page, CancellationToken cancellationToken = default);
    /// <summary>Adds an eligible equal owner with required individual membership, auditing and notifying the ownership change.</summary>
    /// <param name="eventId">Internal Event identifier currently managed by the actor.</param>
    /// <param name="directoryObjectId">New owner's Entra user object ID, not an internal account ID.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of resolution and atomic assignment.</param>
    /// <returns>A task completing after ownership, required membership, audit, and delivery intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, target eligibility, lifecycle, or dependency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task AddOwnerAsync(Guid eventId, Guid directoryObjectId, CancellationToken cancellationToken = default);
    /// <summary>Removes an equal owner assignment, serializing competing removals to retain at least one eligible owner.</summary>
    /// <param name="eventId">Internal Event identifier managed by the actor.</param>
    /// <param name="userId">Internal owner account identifier; may be the current actor.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic assignment change.</param>
    /// <returns>A task completing after assignment removal, audit, and ownership delivery intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, lifecycle, or last-eligible-owner guards fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task RemoveOwnerAsync(Guid eventId, Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Starts a one-time background group expansion, saving a complete recipient snapshot before individual add/invite operations.</summary>
    /// <param name="eventId">Internal Active, not-ended Event identifier managed by the actor.</param>
    /// <param name="groupId">Supported same-tenant Entra group object ID retained only for operational/audit use.</param>
    /// <param name="mode">Direct add or consent-based invitation; bulk add must not silently restore removed members.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of durable workflow creation.</param>
    /// <returns>The new bulk operation identifier, not a promise that expansion or recipient application has completed.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Authorization, lifecycle, group/mode validation, or operation limits fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<Guid> StartBulkAsync(Guid eventId, Guid groupId, BulkMode mode, CancellationToken cancellationToken = default);
    /// <summary>Reads authorized bulk progress, including skips and partial failures, without implying ongoing group synchronization.</summary>
    /// <param name="operationId">Internal bulk operation identifier.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the read.</param>
    /// <returns>Current recorded workflow and recipient counts.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The operation is unavailable to the current actor.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<BulkOperationSummary> GetBulkAsync(Guid operationId, CancellationToken cancellationToken = default);
    /// <summary>Searches eligible directory candidates for explicit user selection without granting resource access.</summary>
    /// <param name="query">User search text subject to input/rate limits.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the external directory query.</param>
    /// <returns>Authorized directory selection results; application commands revalidate selected identities.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Authorization, validation, or directory dependency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default);
    /// <summary>Searches supported groups for an explicit one-time bulk action, never for group-based authorization.</summary>
    /// <param name="query">Group search text subject to input/rate limits.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the external directory query.</param>
    /// <returns>Authorized same-tenant group selection results.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Authorization, validation, or directory dependency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>Quest-side effects of already-authorized Event operations, enlisted in the caller-supplied context and explicit transaction.</summary>
/// <remarks>Implementations must not independently commit, own/dispose the supplied context, or call external providers inside the transaction.
/// The Event operation owns atomic persistence of parent changes, child state/history, and durable delivery work.</remarks>
public interface IQuestEventLifecycle
{
    /// <summary>Cancels applicable Draft, Active, and Suspended child Quests as an explicit parent-cancellation cascade.</summary>
    /// <param name="db">Caller-owned context enlisted in the Event cancellation transaction.</param>
    /// <param name="eventId">Internal Event identifier already authorized for cancellation.</param>
    /// <param name="actorId">Internal initiating actor identifier, or null for a system action.</param>
    /// <param name="reason">Recorded cascade reason for child history and applicable notifications.</param>
    /// <param name="now">Caller-supplied UTC operation instant used consistently across the cascade.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation; the caller controls rollback.</param>
    /// <returns>A task completing after child effects are prepared in the caller's transaction, not independently committed or externally delivered.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task CancelForEventAsync(ISidequestDbContext db, Guid eventId, Guid? actorId, string reason,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    /// <summary>Performs repeat-safe Event-end cleanup: complete overdue Active/Suspended children and cancel unpublished drafts with a system reason.</summary>
    /// <param name="db">Caller-owned context enlisted in the Event completion transaction.</param>
    /// <param name="eventId">Internal completing Event identifier.</param>
    /// <param name="now">Caller-supplied UTC completion instant.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation; the caller controls rollback.</param>
    /// <returns>A task completing after child history/state cleanup within the supplied transaction, without participant cancellation delivery or an independent commit.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task CompleteForEventAsync(ISidequestDbContext db, Guid eventId, DateTimeOffset now, CancellationToken cancellationToken = default);
    /// <summary>Invalidates the removed member's Quest invitations, attendance, and follows while retaining history and queuing applicable calendar withdrawals.</summary>
    /// <param name="db">Caller-owned context enlisted in the membership removal transaction.</param>
    /// <param name="eventId">Internal Event identifier whose membership is being removed.</param>
    /// <param name="userId">Internal account identifier losing individual membership.</param>
    /// <param name="actorId">Internal authorized actor identifier responsible for removal or leaving.</param>
    /// <param name="reason">Recorded membership-loss explanation.</param>
    /// <param name="now">Caller-supplied UTC operation instant.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation; the caller controls rollback.</param>
    /// <returns>A task completing after child cleanup and durable withdrawal intent are prepared, without independently committing or restoring state on rejoin.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task RemoveMemberParticipationAsync(ISidequestDbContext db, Guid eventId, Guid userId, Guid actorId,
        string reason, DateTimeOffset now, CancellationToken cancellationToken = default);
}
