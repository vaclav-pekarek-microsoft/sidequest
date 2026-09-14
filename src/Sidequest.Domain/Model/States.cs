namespace Sidequest.Domain.Model;

/// <summary>Event lifecycle states; terminal states retain authorized historical access.</summary>
public enum EventStatus
{
    /// <summary>Unpublished configuration visible only to Event owners.</summary>
    Draft,
    /// <summary>Published Event, subject to its local date completion boundary.</summary>
    Active,
    /// <summary>Event ended; new activity stops without calendar cancellation.</summary>
    Completed,
    /// <summary>Event explicitly cancelled, with applicable child cancellation and delivery cleanup.</summary>
    Cancelled,
    /// <summary>Read-only historical Event; access revocation and required bookkeeping remain possible.</summary>
    Archived
}
/// <summary>Quest lifecycle states, separate from participation and access grants.</summary>
public enum QuestStatus
{
    /// <summary>Unpublished activity visible only to its owners.</summary>
    Draft,
    /// <summary>Published activity permitting new participation while its Event is Active and the Quest has not ended.</summary>
    Active,
    /// <summary>Moderator suspension freezing new participation and withdrawing calendars, while allowing owner edits.</summary>
    Suspended,
    /// <summary>Activity ended without automatically cancelling historical calendar entries.</summary>
    Completed,
    /// <summary>Activity cancelled; valid invitees retain historical read access while Event membership remains.</summary>
    Cancelled,
    /// <summary>Read-only history hidden from default lists, without freezing access grants permanently.</summary>
    Archived
}
/// <summary>Ordinary Quest visibility within the parent Event's individual membership boundary.</summary>
public enum QuestVisibility
{
    /// <summary>Non-draft content readable by eligible Event members.</summary>
    Public,
    /// <summary>Ordinary access limited to named invitees and Quest owners; separate moderation rules apply.</summary>
    Private
}
/// <summary>Authoritative individual Event membership state.</summary>
public enum MembershipStatus
{
    /// <summary>Membership grants access subject to eligibility and lifecycle checks.</summary>
    Active,
    /// <summary>Membership deactivated; restoration never restores prior Quest participation.</summary>
    Removed
}
/// <summary>Request lifecycle; only approval activates membership.</summary>
public enum MembershipRequestStatus
{
    /// <summary>Awaiting a manager decision; grants no access.</summary>
    Pending,
    /// <summary>Resolved by membership activation.</summary>
    Approved,
    /// <summary>Denied or closed with a reason retained for the requester.</summary>
    Rejected,
    /// <summary>Retracted by the requester.</summary>
    Withdrawn
}
/// <summary>Consent-based Event invitation lifecycle, distinct from a Quest access grant.</summary>
public enum EventInvitationStatus
{
    /// <summary>Awaiting response before the invitation deadline.</summary>
    Pending,
    /// <summary>Resolved by membership activation, including an explicit direct add.</summary>
    Accepted,
    /// <summary>Recipient chose not to accept membership.</summary>
    Declined,
    /// <summary>Manager withdrew the outstanding invitation.</summary>
    Revoked,
    /// <summary>Deadline or Event lifecycle no longer permits acceptance.</summary>
    Expired
}
/// <summary>Private Quest invitation grant state; no acceptance or expiration workflow exists.</summary>
public enum QuestInvitationStatus
{
    /// <summary>Named access remains valid while Event membership and eligibility remain.</summary>
    Active,
    /// <summary>Invitation-derived access withdrawn; independent ownership access is unaffected.</summary>
    Revoked
}
/// <summary>Mutually exclusive Quest/user participation, independent of ownership and invitations.</summary>
public enum ParticipationStatus
{
    /// <summary>Neither following nor attending.</summary>
    None,
    /// <summary>Receiving follower updates without attendance, calendar invitations, or attendee reminders.</summary>
    Following,
    /// <summary>Attending; replaces Following and enables attendee delivery subject to lifecycle and preferences.</summary>
    Joined
}
/// <summary>Repeat-safe user commands over the exclusive participation state.</summary>
public enum ParticipationCommand
{
    /// <summary>Start following; conflicts rather than silently leaving when already Joined.</summary>
    Follow,
    /// <summary>Stop following without changing attendance.</summary>
    Unfollow,
    /// <summary>Attend, atomically replacing Following if present.</summary>
    Join,
    /// <summary>Stop attending without starting or restoring Following.</summary>
    Leave
}
/// <summary>Durable leased-work lifecycle; successful processing does not guarantee mailbox arrival.</summary>
public enum WorkStatus
{
    /// <summary>Awaiting a due initial or retry attempt.</summary>
    Pending,
    /// <summary>Claimed for processing under an expiring lease.</summary>
    Processing,
    /// <summary>Handler completed the logical work.</summary>
    Completed,
    /// <summary>Visible terminal failure awaiting investigation or authorized replay.</summary>
    DeadLetter,
    /// <summary>Obsolete work replaced by a newer intended state.</summary>
    Superseded
}
/// <summary>Private media upload/readiness lifecycle.</summary>
public enum MediaStatus
{
    /// <summary>Upload or preparation has not completed.</summary>
    Pending,
    /// <summary>Validated media is available for authorized delivery.</summary>
    Ready,
    /// <summary>Upload or preparation failed.</summary>
    Failed
}
/// <summary>One-time individual operation applied to a frozen group expansion snapshot.</summary>
public enum BulkMode
{
    /// <summary>Add eligible individuals directly, without silently restoring removed membership.</summary>
    Add,
    /// <summary>Create individual Event invitations requiring consent.</summary>
    Invite
}
/// <summary>One-time directory expansion and application workflow state.</summary>
public enum BulkStatus
{
    /// <summary>Enumerating and deduplicating all recipients before any membership changes.</summary>
    Expanding,
    /// <summary>Applying ordinary individual operations against the saved recipient snapshot.</summary>
    Applying,
    /// <summary>Recipient processing finished; individual skipped or failed outcomes remain inspectable.</summary>
    Completed,
    /// <summary>Workflow failed with an actionable recorded error.</summary>
    Failed
}
/// <summary>Persisted outcome of one snapshot recipient's bulk operation.</summary>
public enum BulkRecipientStatus
{
    /// <summary>Recipient has not yet reached a recorded outcome.</summary>
    Pending,
    /// <summary>Requested individual operation was applied.</summary>
    Applied,
    /// <summary>No change was needed or a safeguard prevented applying stale work.</summary>
    Skipped,
    /// <summary>Recipient operation failed with inspectable detail.</summary>
    Failed
}
/// <summary>Namespace of a resource referenced by audit records.</summary>
public enum ResourceKind
{
    /// <summary>An Event aggregate or Event-scoped action.</summary>
    Event,
    /// <summary>A Quest aggregate or Quest-scoped action.</summary>
    Quest,
    /// <summary>An internal user account or account-scoped action.</summary>
    User,
    /// <summary>A global system operation not scoped to Event or Quest content.</summary>
    System
}
/// <summary>Business change categories selecting notification recipients and mandatory or optional delivery policy.</summary>
public enum NotificationKind
{
    /// <summary>Quest publication eligible for authorized new-Quest fan-out.</summary>
    QuestPublished,
    /// <summary>Named Event membership invitation requiring acceptance.</summary>
    EventInvitation,
    /// <summary>Named private Quest access grant without automatic participation.</summary>
    QuestInvitation,
    /// <summary>Request submitted for an Event manager's decision.</summary>
    MembershipRequested,
    /// <summary>Event membership request or invitation decision communicated to affected users.</summary>
    MembershipDecided,
    /// <summary>Individual Event membership activated by a direct add.</summary>
    MembershipAdded,
    /// <summary>Access revoked, requiring minimal access-loss communication and applicable withdrawals.</summary>
    AccessRemoved,
    /// <summary>User became an attendee, requiring their service/calendar invitation.</summary>
    Joined,
    /// <summary>User left attendance, requiring withdrawal where previously invited.</summary>
    Left,
    /// <summary>Manager removed an attendee with a mandatory reason.</summary>
    AttendeeRemoved,
    /// <summary>Quest content changed; material and calendar flags refine delivery requirements.</summary>
    QuestUpdated,
    /// <summary>Moderator suspended the Quest and withdrew applicable attendee calendars.</summary>
    QuestSuspended,
    /// <summary>Moderator explicitly reinstated the Quest using current details.</summary>
    QuestReinstated,
    /// <summary>Quest cancelled with applicable status delivery and calendar withdrawals.</summary>
    QuestCancelled,
    /// <summary>Event cancelled with affected-member delivery and child calendar cleanup.</summary>
    EventCancelled,
    /// <summary>Configured pre-start reminder for a current joined attendee, never a follower.</summary>
    Reminder,
    /// <summary>Equal-owner assignment changed or was recovered, without automatic participation.</summary>
    OwnershipChanged,
    /// <summary>Suspended content edited for moderator review without participant updates or reinstatement.</summary>
    SuspendedQuestEdited,
    /// <summary>Bulk workflow outcome reported to its initiating manager.</summary>
    BulkCompleted
}
