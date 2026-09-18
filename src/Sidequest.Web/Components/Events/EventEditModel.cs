using System.ComponentModel.DataAnnotations;
using NodaTime;
using Sidequest.Application.Events;

namespace Sidequest.Web.Components.Events;

/// <summary>Mutable form state kept separate from authorized Event DTOs and server business rules.</summary>
public sealed class EventEditModel : IValidatableObject
{
    /// <summary>Plain-text name, trimmed and authoritatively validated by the application service.</summary>
    [Required, StringLength(120, MinimumLength = 3)]
    public string Name { get; set; } = "";

    /// <summary>Member-only plain-text description; never included in discovery.</summary>
    [StringLength(10000)]
    public string Description { get; set; } = "";

    /// <summary>Optional public-to-eligible-users discovery summary.</summary>
    [StringLength(300)]
    public string DiscoverySummary { get; set; } = "";

    /// <summary>Inclusive first date in the Event zone, not a UTC timestamp.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Inclusive last date in the Event zone, strictly later than the start date for saved changes.</summary>
    public DateOnly EndDate { get; set; }

    /// <summary>IANA zone identifier, locked after publication.</summary>
    [Required, StringLength(100)]
    public string TimeZoneId { get; set; } = "Etc/UTC";

    /// <summary>Checks the editable date range and bundled IANA zone before invoking the server mutation.</summary>
    /// <param name="validationContext">The form's validation context.</param>
    /// <returns>Field-specific errors for a non-increasing date range or an unknown time zone.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EndDate <= StartDate)
            yield return new ValidationResult("End date must be after start date.", [nameof(EndDate)]);
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(TimeZoneId ?? "") is null)
            yield return new ValidationResult("Choose an available IANA time zone.", [nameof(TimeZoneId)]);
    }

    /// <summary>Builds an immutable command input without changing the current server version or lifecycle.</summary>
    /// <returns>The proposed replacement configuration.</returns>
    public EventInput ToInput() => new(Name, Description, DiscoverySummary, StartDate, EndDate, TimeZoneId);

    /// <summary>Copies a command DTO so form binding cannot mutate parent-owned input.</summary>
    /// <param name="input">Initial proposed or persisted configuration.</param>
    /// <returns>New independent form state.</returns>
    public static EventEditModel From(EventInput input) => new()
    {
        Name = input.Name, Description = input.Description, DiscoverySummary = input.DiscoverySummary,
        StartDate = input.StartDate, EndDate = input.EndDate, TimeZoneId = input.TimeZoneId
    };
}
