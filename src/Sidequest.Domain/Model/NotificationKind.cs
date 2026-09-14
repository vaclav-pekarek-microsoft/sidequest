namespace Sidequest.Domain.Model;

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
