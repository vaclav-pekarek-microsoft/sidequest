using NodaTime;

namespace Sidequest.Domain.Rules;

/// <summary>Converts IANA-zone local scheduling inputs and enforces Quest containment in inclusive Event dates.</summary>
/// <remarks>Methods do not mutate shared state and may be called concurrently. Returned zones are immutable TZDB values.</remarks>
public static class TimeRules
{
    /// <summary>Resolves a time zone from the bundled TZDB provider.</summary>
    /// <param name="id">Non-null IANA time-zone identifier.</param>
    /// <returns>The resolved zone and its daylight-saving rules.</returns>
    /// <exception cref="DomainException">The identifier is unknown (Validation on TimeZoneId).</exception>
    public static DateTimeZone Zone(string id) =>
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(id)
        ?? throw new DomainException(ErrorCode.Validation, "Choose a valid IANA time zone.", "TimeZoneId");

    /// <summary>Maps inclusive local Event dates to an inclusive-start, exclusive-end instant window.</summary>
    /// <param name="start">First included local date.</param>
    /// <param name="end">Last included local date; must not precede start or equal DateOnly.MaxValue.</param>
    /// <param name="zoneId">IANA Event zone inherited by its Quests.</param>
    /// <returns>Start-of-day boundaries at the zone's offsets; End is the start of the day after end, not a fixed 24-hour increment.</returns>
    /// <exception cref="DomainException">Dates are invalid, a whole local date is skipped, or the zone is unknown (Validation).</exception>
    /// <example>
    /// <code>
    /// var window = TimeRules.EventWindow(
    ///     new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 29), "Europe/Prague");
    /// var duration = window.End - window.Start;
    /// // The spring daylight-saving date spans 23 hours, not a fixed 24 hours.
    /// </code>
    /// </example>
    public static (DateTimeOffset Start, DateTimeOffset End) EventWindow(DateOnly start, DateOnly end, string zoneId)
    {
        if (end < start)
            throw new DomainException(ErrorCode.Validation, "End date must not precede start date.", "EndDate");
        if (end == DateOnly.MaxValue)
            throw new DomainException(ErrorCode.Validation, "End date is outside the supported range.", "EndDate");
        var zone = Zone(zoneId);
        try
        {
            var first = new LocalDate(start.Year, start.Month, start.Day).AtStartOfDayInZone(zone);
            var last = new LocalDate(end.Year, end.Month, end.Day).PlusDays(1).AtStartOfDayInZone(zone);
            return (first.ToDateTimeOffset(), last.ToDateTimeOffset());
        }
        catch (SkippedTimeException)
        {
            throw new DomainException(ErrorCode.Validation, "An Event date does not exist in the selected time zone.", "StartDate");
        }
    }

    /// <summary>Resolves a wall-clock value in the specified zone to UTC, rejecting gaps and requiring overlap disambiguation.</summary>
    /// <param name="local">Local wall-clock components; DateTime.Kind is deliberately ignored.</param>
    /// <param name="zoneId">IANA Event zone in which to interpret the value.</param>
    /// <param name="selectedOffset">Chosen UTC offset for an overlap, or null when the local time is unambiguous.</param>
    /// <returns>The selected instant normalized to offset zero.</returns>
    /// <exception cref="DomainException">The zone is invalid, time falls in a gap, an overlap lacks an offset, or the selected offset is invalid (Validation).</exception>
    /// <example>
    /// <code>
    /// var local = new DateTime(2026, 10, 25, 2, 30, 0, DateTimeKind.Unspecified);
    /// var firstOccurrence = TimeRules.ToUtc(local, "Europe/Prague", TimeSpan.FromHours(2));
    /// var secondOccurrence = TimeRules.ToUtc(local, "Europe/Prague", TimeSpan.FromHours(1));
    /// // The explicit choices differ by one hour; omitting the offset is invalid.
    /// </code>
    /// </example>
    public static DateTimeOffset ToUtc(DateTime local, string zoneId, TimeSpan? selectedOffset = null)
    {
        var zone = Zone(zoneId);
        var mapping = zone.MapLocal(LocalDateTime.FromDateTime(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)));
        if (mapping.Count == 0)
            throw new DomainException(ErrorCode.Validation, "This local time does not exist because of daylight saving.", "LocalTime");
        if (mapping.Count == 2 && selectedOffset is null)
            throw new DomainException(ErrorCode.Validation, "Choose the offset for this ambiguous local time.", "Offset");
        var candidates = mapping.Count == 1 ? new[] { mapping.Single() } : [mapping.First(), mapping.Last()];
        var chosen = selectedOffset is null
            ? candidates[0]
            : candidates.SingleOrDefault(x => x.Offset.ToTimeSpan() == selectedOffset);
        if (selectedOffset is not null && !candidates.Any(x => x.Offset.ToTimeSpan() == selectedOffset))
            throw new DomainException(ErrorCode.Validation, "The selected offset is not valid for this local time.", "Offset");
        return chosen.ToDateTimeOffset().ToUniversalTime();
    }

    /// <summary>Validates a positive Quest duration entirely contained in its parent Event's local-date window.</summary>
    /// <param name="start">Quest start instant; may equal the Event window start.</param>
    /// <param name="end">Quest end instant; must exceed start and may equal the exclusive Event window end.</param>
    /// <param name="eventStart">Inclusive first Event local date.</param>
    /// <param name="eventEnd">Inclusive last Event local date.</param>
    /// <param name="zoneId">Parent Event IANA zone; Quests do not define an independent zone.</param>
    /// <exception cref="DomainException">Duration, containment, Event dates, or zone is invalid (Validation).</exception>
    /// <example>
    /// <code>
    /// var start = TimeRules.ToUtc(new DateTime(2026, 9, 14, 10, 0, 0), "Europe/Prague");
    /// var end = TimeRules.ToUtc(new DateTime(2026, 9, 14, 11, 0, 0), "Europe/Prague");
    /// TimeRules.ValidateQuest(start, end, new DateOnly(2026, 9, 14),
    ///     new DateOnly(2026, 9, 14), "Europe/Prague");
    /// </code>
    /// </example>
    public static void ValidateQuest(DateTimeOffset start, DateTimeOffset end, DateOnly eventStart, DateOnly eventEnd, string zoneId)
    {
        var window = EventWindow(eventStart, eventEnd, zoneId);
        if (end <= start)
            throw new DomainException(ErrorCode.Validation, "Quest end must be after start.", "EndUtc");
        if (start < window.Start || end > window.End)
            throw new DomainException(ErrorCode.Validation, "Quest must fit within the Event dates.", "StartUtc");
    }
}
