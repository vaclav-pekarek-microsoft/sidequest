using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Notifications.Implementation;

/// <summary>Pure notification preference, reminder and safe-default message rules.</summary>
public static class NotificationRules
{
    /// <summary>Validates exact hour precision without rounding and validates an optional IANA display zone.</summary>
    /// <param name="input">Submitted optional delivery settings.</param>
    /// <exception cref="DomainException">A setting is outside its accepted range or precision.</exception>
    public static void Validate(PreferenceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ReminderHours < .01m || input.ReminderHours > 168m ||
            decimal.Round(input.ReminderHours, 2) != input.ReminderHours)
            throw new DomainException(ErrorCode.Validation, "Reminder hours must be 0.01–168 with at most two decimal places.", "ReminderHours");
        if (input.TimeZoneId is not null)
            TimeRules.Zone(input.TimeZoneId);
    }

    /// <summary>Calculates the current reminder due time, advancing an already-open window to now.</summary>
    /// <param name="start">Quest start in UTC.</param>
    /// <param name="hours">Validated lead time in hours.</param>
    /// <param name="now">Current UTC clock instant.</param>
    /// <returns>Future due instant, now inside the window, or null at/after start.</returns>
    public static DateTimeOffset? ReminderDue(DateTimeOffset start, decimal hours, DateTimeOffset now)
    {
        Validate(new(false, true, true, hours, null));
        if (start <= now)
            return null;
        var due = start - TimeSpan.FromTicks(decimal.ToInt64(hours * TimeSpan.TicksPerHour));
        return due < now ? now : due;
    }

    /// <summary>Returns a safe non-executable default without names, rosters, contacts, descriptions, or action reasons.</summary>
    /// <param name="kind">Business trigger.</param>
    /// <returns>Plain English message suitable for access-loss disclosure where explicitly authorized.</returns>
    public static string Summary(NotificationKind kind) => kind switch
    {
        NotificationKind.QuestPublished => "A new Quest is available in your Event.",
        NotificationKind.EventInvitation => "You have an Event invitation. Sign in to review it.",
        NotificationKind.QuestInvitation => "You have a Quest invitation. Sign in to review it.",
        NotificationKind.MembershipRequested => "An Event membership request needs your review.",
        NotificationKind.MembershipDecided => "An Event access decision has been recorded.",
        NotificationKind.MembershipAdded => "You have been added to an Event.",
        NotificationKind.AccessRemoved => "Your access has changed. Previously shared content may no longer be available.",
        NotificationKind.Joined => "Quest attendance changed. Joining requires service and calendar messages.",
        NotificationKind.Left => "Quest attendance ended. Leave in Sidequest to change attendance, not in Outlook.",
        NotificationKind.AttendeeRemoved => "Quest attendance was removed. Sign in for any currently authorized details.",
        NotificationKind.QuestUpdated => "Quest details changed. Sign in to view current details.",
        NotificationKind.QuestSuspended => "A Quest was suspended. Calendar withdrawal may require action in your calendar client.",
        NotificationKind.QuestReinstated => "A Quest was reinstated. Review the latest details in Sidequest.",
        NotificationKind.QuestCancelled => "A Quest was cancelled.",
        NotificationKind.EventCancelled => "An Event was cancelled.",
        NotificationKind.Reminder => "Your joined Quest starts soon. Check Sidequest for current details.",
        NotificationKind.OwnershipChanged => "A resource ownership assignment changed.",
        NotificationKind.SuspendedQuestEdited => "Suspended Quest details changed and require moderator review.",
        NotificationKind.BulkCompleted => "Your bulk membership operation has an updated outcome.",
        _ => throw new DomainException(ErrorCode.Validation, "Unknown notification kind.")
    };
}
