namespace Sidequest.Domain.Model;

/// <summary>Base for persisted records identified independently of directory identities.</summary>
public abstract class Entity
{
    /// <summary>Application-generated internal identifier; not an Entra object ID.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Opaque SQL rowversion concurrency token; empty before persistence and not a timestamp.</summary>
    public byte[] Version { get; set; } = [];
}

/// <summary>Local account keyed externally by the Entra tenant/object pair, with independently checked eligibility.</summary>
public sealed class UserAccount : Entity
{
    /// <summary>Entra tenant identifier forming the external identity key with <see cref="ObjectId"/>.</summary>
    public Guid TenantId { get; set; }
    /// <summary>Entra user object identifier, distinct from the inherited internal identifier.</summary>
    public Guid ObjectId { get; set; }
    /// <summary>Mutable directory display label; never an authorization key.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Trusted directory contact address; never an authorization key.</summary>
    public string Email { get; set; } = "";
    /// <summary>Whether the account satisfies the workforce eligibility policy; verified departure still denies access.</summary>
    public bool IsEligible { get; set; } = true;
    /// <summary>Most recent successful sign-in instant in UTC, or null before the first sign-in.</summary>
    public DateTimeOffset? LastSignedInUtc { get; set; }
    /// <summary>UTC instant of verified organizational departure, or null when departure has not been verified.</summary>
    public DateTimeOffset? DepartureVerifiedUtc { get; set; }
}

/// <summary>Explicit global administrator assignment; grants no implicit Event membership or private content access.</summary>
public sealed class Administrator : Entity
{
    /// <summary>Internal account identifier receiving administrative privileges.</summary>
    public Guid UserId { get; set; }
}

/// <summary>Dated container for individually authorized members and Quests, with equal owners and an inherited child time zone.</summary>
public sealed class Event : Entity
{
    /// <summary>Internal creator account identifier retained for history, not an ongoing authorization grant.</summary>
    public Guid CreatorId { get; set; }
    /// <summary>Event name included in authorized discovery summaries.</summary>
    public string Name { get; set; } = "";
    /// <summary>Full content restricted to authorized Event readers, unlike the discovery summary.</summary>
    public string Description { get; set; } = "";
    /// <summary>Short description safe to disclose during eligible-user discovery of an Active Event.</summary>
    public string DiscoverySummary { get; set; } = "";
    /// <summary>Inclusive first local calendar date in <see cref="TimeZoneId"/>.</summary>
    public DateOnly StartDate { get; set; }
    /// <summary>Inclusive last local calendar date; completion occurs at the following local midnight.</summary>
    public DateOnly EndDate { get; set; }
    /// <summary>IANA zone inherited by every child Quest; fixed after publication.</summary>
    public string TimeZoneId { get; set; } = "Etc/UTC";
    /// <summary>Persisted lifecycle state; callers must also enforce time-based completion boundaries.</summary>
    public EventStatus Status { get; set; }
    /// <summary>UTC instant of Event creation.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>UTC instant of the latest recorded Event update.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>One equal Event owner assignment; owners require individual membership and have no primary-owner rank.</summary>
public sealed class EventOwner : Entity
{
    /// <summary>Internal identifier of the managed Event.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal account identifier of the owner.</summary>
    public Guid UserId { get; set; }
}

/// <summary>Authoritative individual Event/user access record; group expansion never creates an ongoing group grant.</summary>
public sealed class EventMembership : Entity
{
    /// <summary>Internal identifier of the Event whose access is controlled.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal account identifier of the individual member.</summary>
    public Guid UserId { get; set; }
    /// <summary>Whether individual membership is active or has been deactivated; eligibility is checked separately.</summary>
    public MembershipStatus Status { get; set; }
    /// <summary>UTC instant of the latest membership state change.</summary>
    public DateTimeOffset ChangedUtc { get; set; }
    /// <summary>Internal account identifier responsible for the latest membership state change.</summary>
    public Guid ChangedById { get; set; }
}

/// <summary>Retained request for individual Event access; a pending request grants no membership.</summary>
public sealed class EventMembershipRequest : Entity
{
    /// <summary>Internal identifier of the requested Event.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal account identifier of the requester.</summary>
    public Guid UserId { get; set; }
    /// <summary>Current request decision or withdrawal state.</summary>
    public MembershipRequestStatus Status { get; set; }
    /// <summary>Decision explanation visible to the requester, including mandatory rejection reasons.</summary>
    public string Reason { get; set; } = "";
    /// <summary>UTC instant when the request was submitted.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>UTC resolution instant, or null while no decision is recorded.</summary>
    public DateTimeOffset? DecidedUtc { get; set; }
    /// <summary>Internal decision-maker identifier, or null when no user decision-maker is recorded.</summary>
    public Guid? DecidedById { get; set; }
}

/// <summary>Named Event invitation requiring acceptance before membership; unlike a Quest invitation, it expires.</summary>
public sealed class EventInvitation : Entity
{
    /// <summary>Internal identifier of the Event offering membership.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal account identifier of the invited person.</summary>
    public Guid UserId { get; set; }
    /// <summary>Internal account identifier of the inviting manager.</summary>
    public Guid InvitedById { get; set; }
    /// <summary>Pending or terminal response/revocation/expiration state.</summary>
    public EventInvitationStatus Status { get; set; }
    /// <summary>UTC instant when the invitation was created.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>UTC acceptance deadline, bounded by seven days and the Event's local end boundary.</summary>
    public DateTimeOffset ExpiresUtc { get; set; }
    /// <summary>UTC resolution instant, or null while the invitation remains unresolved.</summary>
    public DateTimeOffset? ResolvedUtc { get; set; }
}

/// <summary>Activity inside an Event's date window, inheriting its zone and using separate ownership, invitation, and participation records.</summary>
public sealed class Quest : Entity
{
    /// <summary>Internal parent Event identifier; the parent supplies the time zone and membership boundary.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal creator identifier retained for history, without special privileges after creation.</summary>
    public Guid CreatorId { get; set; }
    /// <summary>Activity title disclosed only through authorized views.</summary>
    public string Title { get; set; } = "";
    /// <summary>Activity details included in authorized content and calendar updates.</summary>
    public string Description { get; set; } = "";
    /// <summary>Free-form physical or online meeting location.</summary>
    public string Location { get; set; } = "";
    /// <summary>Advisory attendee capacity from 1 through 10000, or null for no suggestion; not an attendance limit.</summary>
    public int? SuggestedCapacity { get; set; }
    /// <summary>UTC start instant within the parent Event's local date window.</summary>
    public DateTimeOffset StartUtc { get; set; }
    /// <summary>UTC exclusive end instant, strictly after start and no later than the Event window end.</summary>
    public DateTimeOffset EndUtc { get; set; }
    /// <summary>Member discovery/access policy, fixed after publication.</summary>
    public QuestVisibility Visibility { get; set; }
    /// <summary>Persisted lifecycle state; editing a suspended Quest does not reinstate it.</summary>
    public QuestStatus Status { get; set; }
    /// <summary>Participant-facing explanation of the recorded lifecycle state.</summary>
    public string StatusReason { get; set; } = "";
    /// <summary>Internal cover media identifier, or null when no cover is assigned.</summary>
    public Guid? CoverAssetId { get; set; }
    /// <summary>Monotonic revision allocated transactionally for calendar-affecting changes, including recipient withdrawals.</summary>
    public long CalendarRevision { get; set; }
    /// <summary>Start-time revision used to deduplicate reminders per Quest and recipient.</summary>
    public long StartRevision { get; set; }
    /// <summary>UTC instant of Quest creation.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>UTC instant of the latest recorded Quest update.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>Equal Quest owner assignment granting private access but never automatically joining or following.</summary>
public sealed class QuestOwner : Entity
{
    /// <summary>Internal identifier of the managed Quest.</summary>
    public Guid QuestId { get; set; }
    /// <summary>Internal account identifier of an owner who must also be an Event member.</summary>
    public Guid UserId { get; set; }
}

/// <summary>Identity-bound private Quest access grant, without acceptance, expiration, or automatic participation.</summary>
public sealed class QuestInvitation : Entity
{
    /// <summary>Internal identifier of the private Quest.</summary>
    public Guid QuestId { get; set; }
    /// <summary>Internal invited account identifier; access also requires current Event membership.</summary>
    public Guid UserId { get; set; }
    /// <summary>Internal account identifier of the inviting Quest owner.</summary>
    public Guid InvitedById { get; set; }
    /// <summary>Whether the invitation-derived access grant remains active or has been revoked.</summary>
    public QuestInvitationStatus Status { get; set; }
    /// <summary>UTC instant of the latest grant or revocation.</summary>
    public DateTimeOffset ChangedUtc { get; set; }
}

/// <summary>Single mutually exclusive participation state for a Quest/user pair, independent of ownership and invitations.</summary>
public sealed class QuestParticipation : Entity
{
    /// <summary>Internal identifier of the Quest being joined or followed.</summary>
    public Guid QuestId { get; set; }
    /// <summary>Internal account identifier of the participant.</summary>
    public Guid UserId { get; set; }
    /// <summary>None, Following, or Joined; following and attendance cannot coexist.</summary>
    public ParticipationStatus Status { get; set; }
    /// <summary>UTC instant of the latest participation transition.</summary>
    public DateTimeOffset ChangedUtc { get; set; }
}

/// <summary>Retained audit record of a resource action, written transactionally with its state change.</summary>
public sealed class AuditEntry : Entity
{
    /// <summary>Category identifying the namespace of the audited resource.</summary>
    public ResourceKind ResourceKind { get; set; }
    /// <summary>Internal identifier of the audited resource.</summary>
    public Guid ResourceId { get; set; }
    /// <summary>Internal acting account identifier, or null for a system action.</summary>
    public Guid? ActorId { get; set; }
    /// <summary>Stable action label describing the audited operation.</summary>
    public string Action { get; set; } = "";
    /// <summary>Safe explanation of the action, without secrets or unauthorized content.</summary>
    public string Reason { get; set; } = "";
    /// <summary>Identifier linking related state, audit, and durable-delivery work.</summary>
    public string CorrelationId { get; set; } = "";
    /// <summary>UTC instant when the action occurred.</summary>
    public DateTimeOffset OccurredUtc { get; set; }
}

/// <summary>Retained Event lifecycle transition, including prior state and user or system attribution.</summary>
public sealed class EventStatusHistory : Entity
{
    /// <summary>Internal identifier of the Event whose status changed.</summary>
    public Guid EventId { get; set; }
    /// <summary>Lifecycle state before the transition.</summary>
    public EventStatus Previous { get; set; }
    /// <summary>Lifecycle state after the transition.</summary>
    public EventStatus Next { get; set; }
    /// <summary>Internal actor identifier, or null for a system transition.</summary>
    public Guid? ActorId { get; set; }
    /// <summary>Recorded explanation for the lifecycle transition.</summary>
    public string Reason { get; set; } = "";
    /// <summary>UTC instant of the transition.</summary>
    public DateTimeOffset OccurredUtc { get; set; }
}

/// <summary>Retained Quest lifecycle transition; completion does not erase suspension or cancellation history.</summary>
public sealed class QuestStatusHistory : Entity
{
    /// <summary>Internal identifier of the Quest whose status changed.</summary>
    public Guid QuestId { get; set; }
    /// <summary>Lifecycle state before the transition.</summary>
    public QuestStatus Previous { get; set; }
    /// <summary>Lifecycle state after the transition.</summary>
    public QuestStatus Next { get; set; }
    /// <summary>Internal actor identifier, or null for a system transition.</summary>
    public Guid? ActorId { get; set; }
    /// <summary>Recorded explanation for the lifecycle transition.</summary>
    public string Reason { get; set; } = "";
    /// <summary>UTC instant of the transition.</summary>
    public DateTimeOffset OccurredUtc { get; set; }
}

/// <summary>Durable per-recipient in-app item whose protected content must be reauthorized when read.</summary>
public sealed class Notification : Entity
{
    /// <summary>Internal account identifier of the sole recipient.</summary>
    public Guid UserId { get; set; }
    /// <summary>Stable originating change identifier used to coalesce repeated processing.</summary>
    public Guid SourceChangeId { get; set; }
    /// <summary>Business trigger determining recipient and delivery policy.</summary>
    public NotificationKind Kind { get; set; }
    /// <summary>Related Event identifier, or null for an item without an Event reference.</summary>
    public Guid? EventId { get; set; }
    /// <summary>Related Quest identifier, or null for an item without a Quest reference.</summary>
    public Guid? QuestId { get; set; }
    /// <summary>Stored display text; existing text must not expose protected content after access loss.</summary>
    public string Summary { get; set; } = "";
    /// <summary>Whether the item is a minimal access-loss notice eligible for disclosure without current content access.</summary>
    public bool IsAccessLossNotice { get; set; }
    /// <summary>UTC instant when the notification was created.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>UTC instant when the recipient marked the item read, or null while unread.</summary>
    public DateTimeOffset? ReadUtc { get; set; }
}

/// <summary>Per-user optional delivery choices; mandatory service messages cannot be disabled by these preferences.</summary>
public sealed class NotificationPreference : Entity
{
    /// <summary>Internal account identifier whose preferences are stored.</summary>
    public Guid UserId { get; set; }
    /// <summary>Default opt-in for optional newly published public Quest email.</summary>
    public bool NewQuestEmail { get; set; }
    /// <summary>Whether optional joined/followed Quest activity email is enabled.</summary>
    public bool ActivityEmail { get; set; } = true;
    /// <summary>Whether attendee reminders are enabled for both email and in-app channels.</summary>
    public bool RemindersEnabled { get; set; } = true;
    /// <summary>Reminder lead time in hours; accepted input is 0.01 through 168 with at most two decimal places.</summary>
    public decimal ReminderHours { get; set; } = 1;
    /// <summary>Preferred IANA display zone, or null when no user override is recorded; does not change Quest scheduling.</summary>
    public string? TimeZoneId { get; set; }
}

/// <summary>Event-specific override of a user's optional new-Quest email preference.</summary>
public sealed class EventNotificationPreference : Entity
{
    /// <summary>Internal identifier of the Event to which the override applies.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal account identifier choosing the override.</summary>
    public Guid UserId { get; set; }
    /// <summary>Whether optional new-Quest email is enabled for this Event instead of the user default.</summary>
    public bool NewQuestEmail { get; set; }
}

/// <summary>Versioned notification template using allowlisted non-executable placeholders.</summary>
public sealed class NotificationTemplate : Entity
{
    /// <summary>Allowlisted template lookup key.</summary>
    public string Key { get; set; } = "";
    /// <summary>Template content revision retained for controlled updates.</summary>
    public int Revision { get; set; }
    /// <summary>Email subject template.</summary>
    public string Subject { get; set; } = "";
    /// <summary>HTML body template whose substituted user data must be HTML-encoded.</summary>
    public string HtmlBody { get; set; } = "";
    /// <summary>Plain-text alternative body template.</summary>
    public string TextBody { get; set; } = "";
    /// <summary>UTC instant of the latest template change.</summary>
    public DateTimeOffset ChangedUtc { get; set; }
    /// <summary>Internal administrator account identifier responsible for the change.</summary>
    public Guid ChangedById { get; set; }
}

/// <summary>Persisted allowlisted business setting; deployment secrets are not application settings.</summary>
public sealed class ApplicationSetting : Entity
{
    /// <summary>Allowlisted business configuration key.</summary>
    public string Key { get; set; } = "";
    /// <summary>Serialized configuration value validated according to its key.</summary>
    public string Value { get; set; } = "";
}

/// <summary>Durable change work committed with domain state, then dispatched outside the SQL transaction using leases and retries.</summary>
public sealed class OutboxMessage : Entity
{
    /// <summary>Versioned work discriminator selecting the payload handler.</summary>
    public string Type { get; set; } = "";
    /// <summary>Payload schema metadata for compatible deserialization; defaults to version 1.</summary>
    public int SchemaVersion { get; set; } = 1;
    /// <summary>Internal affected aggregate identifier, typically Quest ID or its Event ID.</summary>
    public Guid AggregateId { get; set; }
    /// <summary>Serialized durable payload; retries must preserve the logical change and recipient basis.</summary>
    public string PayloadJson { get; set; } = "";
    /// <summary>Correlation identifier linking the originating change and subsequent work.</summary>
    public string CorrelationId { get; set; } = "";
    /// <summary>UTC instant when the originating change occurred.</summary>
    public DateTimeOffset OccurredUtc { get; set; }
    /// <summary>Dispatch lifecycle state, including terminal failure and supersession.</summary>
    public WorkStatus Status { get; set; }
    /// <summary>Recorded processing attempt count used by the retry policy.</summary>
    public int Attempts { get; set; }
    /// <summary>UTC instant at or after which processing or retry becomes due.</summary>
    public DateTimeOffset DueUtc { get; set; }
    /// <summary>Current worker claim token, or null when no lease is recorded.</summary>
    public Guid? LeaseId { get; set; }
    /// <summary>UTC lease expiry permitting abandoned work to be reclaimed, or null without a recorded lease.</summary>
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    /// <summary>Last recorded safe failure detail, or null when no error is recorded.</summary>
    public string? LastError { get; set; }
}

/// <summary>Durable timed work for lifecycle cleanup, reminders, or bulk processing, with repeat-safe leasing.</summary>
public sealed class ScheduledWork : Entity
{
    /// <summary>Versioned work discriminator selecting the execution handler.</summary>
    public string Type { get; set; } = "";
    /// <summary>Stable logical-work key preventing duplicate scheduling or replay effects.</summary>
    public string DeduplicationKey { get; set; } = "";
    /// <summary>Associated Quest identifier, or null for work not scoped to a Quest.</summary>
    public Guid? QuestId { get; set; }
    /// <summary>Associated internal user identifier, or null for work not scoped to a recipient.</summary>
    public Guid? UserId { get; set; }
    /// <summary>Serialized work input interpreted according to the versioned work type.</summary>
    public string PayloadJson { get; set; } = "";
    /// <summary>Execution lifecycle state used for claims, retries, and terminal outcomes.</summary>
    public WorkStatus Status { get; set; }
    /// <summary>UTC instant at or after which execution or retry is due.</summary>
    public DateTimeOffset DueUtc { get; set; }
    /// <summary>Recorded execution attempt count used by the retry policy.</summary>
    public int Attempts { get; set; }
    /// <summary>Current worker claim token, or null when no lease is recorded.</summary>
    public Guid? LeaseId { get; set; }
    /// <summary>UTC claim expiry, or null without a recorded lease.</summary>
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    /// <summary>Last recorded safe failure detail, or null when no error is recorded.</summary>
    public string? LastError { get; set; }
}

/// <summary>Durable per-recipient delivery attempt state; provider acceptance is not guaranteed mailbox arrival.</summary>
public sealed class NotificationDelivery : Entity
{
    /// <summary>Internal identifier of the corresponding in-app notification.</summary>
    public Guid NotificationId { get; set; }
    /// <summary>Internal account identifier of the sole delivery recipient.</summary>
    public Guid UserId { get; set; }
    /// <summary>Stable logical delivery key reused across retries and administrative replay.</summary>
    public string DeduplicationKey { get; set; } = "";
    /// <summary>Serialized delivery input; dispatch must recheck current access and optional preferences.</summary>
    public string PayloadJson { get; set; } = "";
    /// <summary>Delivery processing state, including visible dead-letter failures.</summary>
    public WorkStatus Status { get; set; }
    /// <summary>UTC instant at or after which the next attempt is due.</summary>
    public DateTimeOffset DueUtc { get; set; }
    /// <summary>Recorded delivery attempt count.</summary>
    public int Attempts { get; set; }
    /// <summary>Current dispatcher claim token, or null without a recorded lease.</summary>
    public Guid? LeaseId { get; set; }
    /// <summary>UTC claim expiry permitting recovery, or null without a recorded lease.</summary>
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    /// <summary>Provider acceptance identifier, or null when no acceptance identifier was recorded.</summary>
    public string? ProviderMessageId { get; set; }
    /// <summary>Last recorded safe delivery failure detail, or null when none is recorded.</summary>
    public string? LastError { get; set; }
}

/// <summary>Per-Quest/recipient calendar ordering and uncertainty state used to supersede stale deliveries safely.</summary>
public sealed class CalendarDeliveryState : Entity
{
    /// <summary>Internal Quest identifier associated with the stable calendar UID.</summary>
    public Guid QuestId { get; set; }
    /// <summary>Internal account identifier of the sole calendar recipient.</summary>
    public Guid UserId { get; set; }
    /// <summary>Latest desired iCalendar sequence allocated from the Quest's monotonic calendar revision.</summary>
    public long IntendedSequence { get; set; }
    /// <summary>Last recorded sent sequence, or null before a successful send is recorded.</summary>
    public long? SentSequence { get; set; }
    /// <summary>Desired iTIP method, such as REQUEST or CANCEL.</summary>
    public string IntendedMethod { get; set; } = "";
    /// <summary>Serialized calendar content retained unchanged for retries of the same logical delivery.</summary>
    public string Payload { get; set; } = "";
    /// <summary>Whether prior delivery may have reached the provider, requiring withdrawal even after an uncertain outcome.</summary>
    public bool MayHaveBeenDelivered { get; set; }
    /// <summary>UTC instant of the latest calendar delivery-state change.</summary>
    public DateTimeOffset ChangedUtc { get; set; }
}

/// <summary>Private Quest media metadata; the blob reference is not a public permanent URL or access grant.</summary>
public sealed class MediaAsset : Entity
{
    /// <summary>Internal Quest identifier whose authorization protects the asset.</summary>
    public Guid QuestId { get; set; }
    /// <summary>Internal account identifier of the media creator.</summary>
    public Guid CreatedById { get; set; }
    /// <summary>Private storage blob key used only through authorized media delivery.</summary>
    public string BlobName { get; set; } = "";
    /// <summary>Validated media MIME type.</summary>
    public string ContentType { get; set; } = "";
    /// <summary>Stored content size in bytes.</summary>
    public long SizeBytes { get; set; }
    /// <summary>Image width in pixels.</summary>
    public int Width { get; set; }
    /// <summary>Image height in pixels.</summary>
    public int Height { get; set; }
    /// <summary>Upload/readiness state; pending or failed content is not ready for normal delivery.</summary>
    public MediaStatus Status { get; set; }
    /// <summary>UTC instant when the asset record was created.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>One-time group expansion workflow that snapshots users before applying individual add/invite operations.</summary>
public sealed class BulkMembershipOperation : Entity
{
    /// <summary>Internal identifier of the target Event.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal initiating manager identifier, reauthorized for each recipient operation.</summary>
    public Guid ActorId { get; set; }
    /// <summary>Entra source group object ID retained only for workflow/audit; never an authorization grant or synchronization link.</summary>
    public Guid SourceGroupId { get; set; }
    /// <summary>Whether snapshot recipients receive direct membership additions or consent-based invitations.</summary>
    public BulkMode Mode { get; set; }
    /// <summary>Expansion/application progress state, including visible failure.</summary>
    public BulkStatus Status { get; set; }
    /// <summary>UTC instant when the explicit bulk action was initiated.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>UTC instant when the complete recipient snapshot was recorded, or null before completion; not an atomic directory snapshot time.</summary>
    public DateTimeOffset? SnapshotUtc { get; set; }
    /// <summary>Last recorded safe workflow failure detail, or null when none is recorded.</summary>
    public string? LastError { get; set; }
}

/// <summary>Frozen individual bulk recipient and resumable outcome; later source-group changes do not alter this snapshot.</summary>
public sealed class BulkMembershipRecipient : Entity
{
    /// <summary>Internal identifier of the one-time bulk operation.</summary>
    public Guid OperationId { get; set; }
    /// <summary>Internal account identifier resolved during complete directory expansion.</summary>
    public Guid UserId { get; set; }
    /// <summary>Per-recipient processing outcome used to resume unfinished work without duplicate grants.</summary>
    public BulkRecipientStatus Status { get; set; }
    /// <summary>Safe outcome explanation, including skip/failure details, or null when no detail is recorded.</summary>
    public string? Detail { get; set; }
}
