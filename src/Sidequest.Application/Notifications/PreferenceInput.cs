namespace Sidequest.Application.Notifications;

/// <summary>User-controlled optional delivery preferences; mandatory service and calendar messages remain enabled independently.</summary>
/// <param name="NewQuestEmail">Default opt-in for optional new public Quest email; accepted default is false.</param>
/// <param name="ActivityEmail">Whether optional joined/followed activity email is enabled; accepted default is true.</param>
/// <param name="RemindersEnabled">Whether attendee reminders are enabled for both email and in-app channels; accepted default is true.</param>
/// <param name="ReminderHours">Lead time from 0.01 through 168 hours, with at most two decimal places; accepted default is one hour.</param>
/// <param name="TimeZoneId">Preferred IANA display zone, or null to use browser zone then the inherited Quest zone; does not alter scheduling.</param>
public sealed record PreferenceInput(bool NewQuestEmail, bool ActivityEmail, bool RemindersEnabled,
    decimal ReminderHours, string? TimeZoneId);
