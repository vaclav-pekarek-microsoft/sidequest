using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Quests;

/// <summary>Authorized Quest views; moderation is a separate privacy-restricted access path.</summary>
public enum QuestListKind
{
    /// <summary>Current actor's joined activities, excluding follower-only participation.</summary>
    Joined,
    /// <summary>Current actor's followed activities, excluding joined attendance.</summary>
    Following,
    /// <summary>Activities the actor owns, without implying attendance.</summary>
    Organizing,
    /// <summary>Discoverable public activities within the actor's individually joined Events.</summary>
    Discover,
    /// <summary>Private activities accessible through the actor's active named invitations.</summary>
    Invited,
    /// <summary>Past activities still accessible to the current actor.</summary>
    History,
    /// <summary>Non-draft activities moderated by an Event owner, without participant or invitation roster disclosure.</summary>
    Moderation
}
/// <summary>Quest configuration input interpreted in the parent Event's zone; no independent zone or parent reassignment is accepted.</summary>
/// <param name="Title">Proposed title, 3 through 120 characters after trimming.</param>
/// <param name="Description">Plain-text activity description, at most 10000 characters after trimming.</param>
/// <param name="Location">Physical or online meeting location, 1 through 500 characters for publication after trimming.</param>
/// <param name="SuggestedCapacity">Advisory attendee count from 1 through 10000, or null; never a hard joining limit.</param>
/// <param name="StartLocal">Local start wall-clock components in the inherited Event IANA zone; DateTime.Kind does not select a zone.</param>
/// <param name="EndLocal">Local end wall-clock components; resolved instant must exceed start and fit within the Event's inclusive dates.</param>
/// <param name="StartOffset">Chosen UTC offset for an ambiguous start, or null if unambiguous; nonexistent local times are invalid.</param>
/// <param name="EndOffset">Chosen UTC offset for an ambiguous end, or null if unambiguous; any supplied offset must match the zone.</param>
/// <param name="Visibility">Member-public or named-private visibility; immutable after publication.</param>
public sealed record QuestInput(string Title, string Description, string Location, int? SuggestedCapacity,
    DateTime StartLocal, DateTime EndLocal, TimeSpan? StartOffset, TimeSpan? EndOffset, QuestVisibility Visibility);
/// <summary>Authorized Quest projection with privacy-aware nullable participation counts.</summary>
/// <param name="Id">Internal Quest identifier.</param>
/// <param name="EventId">Internal parent Event identifier and membership boundary.</param>
/// <param name="EventName">Authorized parent Event display name.</param>
/// <param name="Title">Activity title.</param>
/// <param name="Location">Activity meeting location.</param>
/// <param name="StartUtc">UTC start instant.</param>
/// <param name="EndUtc">UTC exclusive end instant, strictly after start.</param>
/// <param name="TimeZoneId">IANA zone inherited from the Event, not independently stored or edited on the Quest.</param>
/// <param name="Status">Lifecycle state presented by the query, subject to effective time boundaries.</param>
/// <param name="Visibility">Public/private policy within Event membership.</param>
/// <param name="AttendeeCount">Current Joined count, or null when privacy suppresses counts, such as moderation-only access; null is not zero.</param>
/// <param name="FollowerCount">Current Following count, or null when privacy suppresses counts; null is not zero.</param>
/// <param name="SuggestedCapacity">Optional advisory attendance count, not a joining limit.</param>
/// <param name="Participation">Current actor's exclusive None/Following/Joined state.</param>
/// <param name="IsOwner">Whether the current actor is an equal Quest owner.</param>
/// <param name="CanModerate">Whether the actor can use the separate Event-owner moderation path, not a content-editing grant.</param>
/// <param name="Version">Opaque Base64 SQL rowversion for concurrency checks, not a timestamp.</param>
/// <param name="CoverAssetId">Internal private media identifier, or null without a cover; possession does not authorize retrieval.</param>
public sealed record QuestSummary(Guid Id, Guid EventId, string EventName, string Title, string Location,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, string TimeZoneId, QuestStatus Status, QuestVisibility Visibility,
    int? AttendeeCount, int? FollowerCount, int? SuggestedCapacity, ParticipationStatus Participation,
    bool IsOwner, bool CanModerate, string Version, Guid? CoverAssetId);
/// <summary>Authorized Quest content with roster disclosure determined independently for ordinary, owner, and moderation views.</summary>
/// <param name="Summary">Privacy-filtered Quest summary.</param>
/// <param name="Description">Authorized plain-text activity details.</param>
/// <param name="StatusReason">Participant-facing lifecycle explanation.</param>
/// <param name="Owners">Equal Quest owners; no primary-owner or creator privilege is implied.</param>
/// <param name="Attendees">Current attendee names for authorized ordinary viewers, or null when withheld for privacy, including moderation-only views.</param>
/// <param name="Followers">Current follower names for Quest owners, or null when the actor may not see the roster.</param>
/// <param name="Invitees">Named invitee roster for Quest owners, or null when the actor may not see it; null is not an empty roster.</param>
public sealed record QuestDetail(QuestSummary Summary, string Description, string StatusReason,
    IReadOnlyList<OwnerSummary> Owners, IReadOnlyList<PersonSummary>? Attendees,
    IReadOnlyList<PersonSummary>? Followers, IReadOnlyList<PersonSummary>? Invitees);
/// <summary>Authorized Quest action history, excluding protected moderation details from ordinary viewer projections.</summary>
/// <param name="Action">Recorded action label.</param>
/// <param name="Reason">Safe explanation visible in the selected history view.</param>
/// <param name="OccurredUtc">UTC action instant.</param>
/// <param name="Actor">Actor display label, or null when no user label is supplied, including system actions.</param>
public sealed record QuestHistoryItem(string Action, string Reason, DateTimeOffset OccurredUtc, string? Actor);
/// <summary>Minimal joined-Quest offline display snapshot; never an authorization source or offline mutation instruction.</summary>
/// <param name="Id">Internal joined Quest identifier.</param>
/// <param name="EventId">Internal parent Event identifier.</param>
/// <param name="Title">Last refreshed activity title.</param>
/// <param name="Location">Last refreshed meeting location.</param>
/// <param name="StartUtc">Last refreshed UTC start instant.</param>
/// <param name="EndUtc">Last refreshed UTC end instant.</param>
/// <param name="TimeZoneId">Last refreshed inherited Event IANA zone.</param>
/// <param name="Status">Last known lifecycle state, not a guarantee of current online state.</param>
public sealed record OfflineQuest(Guid Id, Guid EventId, string Title, string Location,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, string TimeZoneId, QuestStatus Status);

/// <summary>Server-authorized Quest use cases keeping individual Event membership, equal ownership, invitations, and participation separate.</summary>
/// <remarks>Implementations reauthorize the eligible, non-departed current actor against the database on every call.
/// Mutations own operation-scoped contexts and explicit transactions for state, history, outbox, and scheduling.
/// No external provider calls occur inside SQL transactions; successful commands mean durable intent, not guaranteed external delivery.</remarks>
public interface IQuestService
{
    /// <summary>Lists Quests in the selected authorized view without leaking private/draft resources or suppressed counts.</summary>
    /// <param name="kind">Participation, ownership, discovery, history, or explicit moderation view.</param>
    /// <param name="eventId">Optional internal parent Event filter; null still restricts results to the actor's authorized scope.</param>
    /// <param name="page">One-based paging input with page size 1 through 100.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the query.</param>
    /// <returns>An authorized page with a matching total count and privacy-aware nullable participant counts.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Validation or authorization checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<PageResult<QuestSummary>> ListAsync(QuestListKind kind, Guid? eventId, PageRequest page,
        CancellationToken cancellationToken = default);
    /// <summary>Reads authorized Quest detail through ordinary access or the separately audited private moderation path.</summary>
    /// <param name="id">Internal Quest identifier; knowing it grants no access.</param>
    /// <param name="moderation">Requests non-draft Event-owner moderation, which excludes invitation, attendee, and follower rosters.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the read and applicable access audit.</param>
    /// <returns>Authorized content with withheld counts/rosters represented as null, not fabricated zeros or empty collections.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The actor is forbidden or the resource is unavailable without existence disclosure.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<QuestDetail> GetAsync(Guid id, bool moderation = false, CancellationToken cancellationToken = default);
    /// <summary>Creates a Draft Quest for an eligible member of an Active, not-ended Event, atomically assigning the creator as first equal owner.</summary>
    /// <param name="eventId">Internal parent Event identifier supplying dates, zone, and individual membership boundary.</param>
    /// <param name="input">Local scheduling and content input; ownership assignment does not join or follow.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation without partial creation.</param>
    /// <returns>The new internal Quest identifier.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Membership, lifecycle, content, time mapping, or interval containment checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<Guid> CreateAsync(Guid eventId, QuestInput input, CancellationToken cancellationToken = default);
    /// <summary>Edits owner-managed Draft, Active, or Suspended content while the parent remains Active and not ended; never implicitly reinstates suspension.</summary>
    /// <param name="id">Internal Quest identifier.</param>
    /// <param name="version">Expected Base64 rowversion rejecting stale edits.</param>
    /// <param name="input">Replacement content and Event-zone scheduling input; published visibility remains fixed.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic edit.</param>
    /// <returns>A task completing after edit/history and applicable calendar, notification, and schedule intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, validation, lifecycle, fixed visibility, or concurrency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task EditAsync(Guid id, string version, QuestInput input, CancellationToken cancellationToken = default);
    /// <summary>Applies an allowed lifecycle transition; suspension/reinstatement requires Event ownership, not merely Quest ownership.</summary>
    /// <param name="id">Internal Quest identifier.</param>
    /// <param name="version">Expected Base64 rowversion.</param>
    /// <param name="target">Requested lifecycle state, subject to accepted transition and effective-time guards.</param>
    /// <param name="reason">Required explanation for moderation, cancellation, and other reason-requiring transitions.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic transition.</param>
    /// <returns>A task completing after state/history and applicable durable delivery effects are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Actor permissions, reason validation, lifecycle, or concurrency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task ChangeStatusAsync(Guid id, string version, QuestStatus target, string reason, CancellationToken cancellationToken = default);
    /// <summary>Hard-deletes an authorized unpublished Draft Quest subject to draft-deletion guards, retaining the required audit trail.</summary>
    /// <param name="id">Internal Draft Quest identifier.</param>
    /// <param name="version">Expected Base64 rowversion.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of atomic deletion.</param>
    /// <returns>A task completing after deletion and audit are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, draft state, or concurrency checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task DeleteDraftAsync(Guid id, string version, CancellationToken cancellationToken = default);
    /// <summary>Changes only the current actor's exclusive participation; Join replaces Following, while Leave never starts or restores it.</summary>
    /// <param name="id">Internal accessible Quest identifier.</param>
    /// <param name="command">Repeat-safe participation command; Follow while Joined is a conflict.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the serialized participation and delivery changes.</param>
    /// <returns>A task completing after participation, history, and applicable calendar/reminder intent are persisted, or a valid repeat is a no-op.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access or lifecycle checks fail, command is invalid, or Follow conflicts with Joined.
    /// Join/Follow require Active resources before Quest end; Leave/Unfollow remain permitted in retained states.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task ParticipateAsync(Guid id, ParticipationCommand command, CancellationToken cancellationToken = default);
    /// <summary>Grants a named Event member access to an owner-managed private Active Quest without joining, following, or requiring acceptance.</summary>
    /// <param name="id">Internal private Quest identifier.</param>
    /// <param name="userId">Eligible recipient's internal account ID, not a directory object ID.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of grant and durable invitation creation.</param>
    /// <returns>A task completing after access grant, history, and mandatory invitation intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Ownership, target membership/eligibility, visibility, or lifecycle checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task InviteAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Revokes named invitation access and applicable participation, retaining independent owner-derived access and history.</summary>
    /// <param name="id">Internal Quest identifier managed by the actor.</param>
    /// <param name="userId">Internal invitee account identifier.</param>
    /// <param name="reason">Recorded revocation explanation for safe access-loss communication.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation without partial grant/participation cleanup.</param>
    /// <returns>A task completing after revocation, participation cleanup, and applicable durable calendar withdrawal intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, reason, or grant-state checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task RevokeInvitationAsync(Guid id, Guid userId, string reason, CancellationToken cancellationToken = default);
    /// <summary>Allows a Quest owner to remove an attendee with a mandatory reason; this is not a permanent public-Quest joining ban.</summary>
    /// <param name="id">Internal Quest identifier managed by the actor.</param>
    /// <param name="userId">Internal attendee account identifier.</param>
    /// <param name="reason">Mandatory reason, 10 through 2000 characters after trimming.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of attendance removal and delivery intent.</param>
    /// <returns>A task completing after removal, history, and required reason/calendar withdrawal intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Authorization, reason, or current-state checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task RemoveAttendeeAsync(Guid id, Guid userId, string reason, CancellationToken cancellationToken = default);
    /// <summary>Assigns an eligible existing Event member as an equal Quest owner, granting role-derived access but not participation.</summary>
    /// <param name="id">Internal Quest identifier already managed by the actor.</param>
    /// <param name="userId">Internal target account identifier; individual Event membership is required.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the atomic ownership change.</param>
    /// <returns>A task completing after owner assignment, audit, and ownership notification intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, target eligibility/membership, or lifecycle checks fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task AddOwnerAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Removes equal ownership while atomically retaining at least one eligible owner; separate invitation access remains independent.</summary>
    /// <param name="id">Internal Quest identifier managed by the actor.</param>
    /// <param name="userId">Internal owner account identifier; may be the current actor.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of the serialized ownership change.</param>
    /// <returns>A task completing after ownership removal, audit, and required notification intent are persisted.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Access, lifecycle, or last-eligible-owner guards fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task RemoveOwnerAsync(Guid id, Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Reads authorized Quest history, keeping moderation records restricted to permitted owners/moderators.</summary>
    /// <param name="id">Internal Quest identifier.</param>
    /// <param name="moderation">Selects the explicit non-draft Event-owner moderation view rather than ordinary Quest access.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation of history retrieval.</param>
    /// <returns>Only action history the actor may see; ordinary access does not imply full moderation history.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The Quest or selected history view is unavailable to the actor.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<IReadOnlyList<QuestHistoryItem>> HistoryAsync(Guid id, bool moderation = false, CancellationToken cancellationToken = default);
    /// <summary>Builds a minimal online-authorized snapshot of the actor's joined Quests for per-account offline display.</summary>
    /// <param name="cancellationToken">Requests cooperative cancellation of snapshot retrieval.</param>
    /// <returns>Joined-only display data, excluding descriptions, rosters, contacts, images, invitations, and tokens; local storage never grants authorization.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">Current identity or account eligibility is forbidden.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    Task<IReadOnlyList<OfflineQuest>> GetOfflineJoinedAsync(CancellationToken cancellationToken = default);
}
