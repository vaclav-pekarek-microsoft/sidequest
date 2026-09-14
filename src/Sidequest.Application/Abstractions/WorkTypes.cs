namespace Sidequest.Application.Abstractions;

/// <summary>Stable versioned durable-work discriminators shared by producers and handlers.</summary>
public static class WorkTypes
{
    /// <summary>Version-1 business change envelope dispatched from the outbox.</summary>
    public const string Change = "sidequest.change.v1";
    /// <summary>Version-1 Event local-end completion work.</summary>
    public const string EventCompletion = "event.complete.v1";
    /// <summary>Version-1 Quest end-time completion work.</summary>
    public const string QuestCompletion = "quest.complete.v1";
    /// <summary>Version-1 one-time group expansion and individual membership/invitation work.</summary>
    public const string BulkMembership = "event.bulk-membership.v1";
    /// <summary>Version-1 attendee reminder work, deduplicated by user, Quest, and start revision.</summary>
    public const string Reminder = "quest.reminder.v1";
}
