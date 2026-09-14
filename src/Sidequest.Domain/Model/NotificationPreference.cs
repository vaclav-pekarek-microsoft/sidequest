namespace Sidequest.Domain.Model;

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
