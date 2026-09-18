namespace Sidequest.Application.Events;

/// <summary>Event configuration input; the application validates dates, text, lifecycle, and existing child intervals.</summary>
/// <param name="Name">Proposed Event name, 3 through 120 characters after trimming.</param>
/// <param name="Description">Full member-only plain-text description, at most 10000 characters after trimming.</param>
/// <param name="DiscoverySummary">Eligible-user discovery text, at most 300 characters after trimming.</param>
/// <param name="StartDate">Inclusive first local Event date.</param>
/// <param name="EndDate">Inclusive last local Event date; create and edit commands require a date strictly after StartDate.</param>
/// <param name="TimeZoneId">Valid IANA zone; cannot be changed after publication.</param>
public sealed record EventInput(string Name, string Description, string DiscoverySummary,
    DateOnly StartDate, DateOnly EndDate, string TimeZoneId);
