using NodaTime;

namespace Sidequest.Domain.Rules;

public static class TimeRules
{
    public static DateTimeZone Zone(string id) =>
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(id)
        ?? throw new DomainException(ErrorCode.Validation, "Choose a valid IANA time zone.", "TimeZoneId");

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

    public static void ValidateQuest(DateTimeOffset start, DateTimeOffset end, DateOnly eventStart, DateOnly eventEnd, string zoneId)
    {
        var window = EventWindow(eventStart, eventEnd, zoneId);
        if (end <= start)
            throw new DomainException(ErrorCode.Validation, "Quest end must be after start.", "EndUtc");
        if (start < window.Start || end > window.End)
            throw new DomainException(ErrorCode.Validation, "Quest must fit within the Event dates.", "StartUtc");
    }
}
