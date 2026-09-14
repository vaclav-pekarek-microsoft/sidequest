using System.ComponentModel.DataAnnotations;

namespace Sidequest.Web.Components.Notifications;

/// <summary>Editable optional delivery fields; the application service enforces exact precision and current authorization again.</summary>
public sealed class NotificationPreferenceForm
{
    /// <summary>Default optional publication email opt-in.</summary>
    public bool NewQuestEmail { get; set; }
    /// <summary>Optional joined/followed activity email choice.</summary>
    public bool ActivityEmail { get; set; } = true;
    /// <summary>Whether both reminder channels are enabled.</summary>
    public bool RemindersEnabled { get; set; } = true;
    /// <summary>Lead time in hours; arbitrary numeric values at two-decimal precision are supported.</summary>
    [Range(typeof(decimal), "0.01", "168")]
    public decimal ReminderHours { get; set; } = 1m;
    /// <summary>Optional IANA display zone, not a calendar scheduling override.</summary>
    public string? TimeZoneId { get; set; }
}
