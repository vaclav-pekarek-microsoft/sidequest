namespace Sidequest.Domain.Model;

public enum EventStatus { Draft, Active, Completed, Cancelled, Archived }
public enum QuestStatus { Draft, Active, Suspended, Completed, Cancelled, Archived }
public enum QuestVisibility { Public, Private }
public enum MembershipStatus { Active, Removed }
public enum MembershipRequestStatus { Pending, Approved, Rejected, Withdrawn }
public enum EventInvitationStatus { Pending, Accepted, Declined, Revoked, Expired }
public enum QuestInvitationStatus { Active, Revoked }
public enum ParticipationStatus { None, Following, Joined }
public enum ParticipationCommand { Follow, Unfollow, Join, Leave }
public enum WorkStatus { Pending, Processing, Completed, DeadLetter, Superseded }
public enum MediaStatus { Pending, Ready, Failed }
public enum BulkMode { Add, Invite }
public enum BulkStatus { Expanding, Applying, Completed, Failed }
public enum BulkRecipientStatus { Pending, Applied, Skipped, Failed }
public enum ResourceKind { Event, Quest, User, System }
public enum NotificationKind
{
    QuestPublished, EventInvitation, QuestInvitation, MembershipRequested, MembershipDecided,
    MembershipAdded, AccessRemoved, Joined, Left, AttendeeRemoved, QuestUpdated,
    QuestSuspended, QuestReinstated, QuestCancelled, EventCancelled, Reminder,
    OwnershipChanged, SuspendedQuestEdited, BulkCompleted
}
