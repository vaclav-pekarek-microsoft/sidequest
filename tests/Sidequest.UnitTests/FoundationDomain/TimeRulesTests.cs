using System.Globalization;
using Sidequest.Domain.Rules;

namespace Sidequest.UnitTests.FoundationDomain;

/// <summary>Checks independently derived UTC instants, Event date windows, and tick-precise Quest containment.</summary>
public sealed class TimeRulesTests
{
    private const string Prague = "Europe/Prague";
    private const string InvalidZone = "Invalid/Sidequest";
    private const string InvalidOffsetMessage = "The selected offset is not valid for this local time.";
    private static readonly DateOnly SpringDay = new(2026, 3, 29);
    private static readonly DateOnly AutumnDay = new(2026, 10, 25);
    private static readonly DateTimeOffset SpringStart = Utc("2026-03-28T23:00:00Z");
    private static readonly DateTimeOffset SpringEnd = Utc("2026-03-29T22:00:00Z");

    /// <summary>Checks Prague's IANA identity and its independently expected winter/summer UTC offsets.</summary>
    [Fact]
    public void Zone_ValidIanaId_ReturnsExpectedZone()
    {
        var zone = TimeRules.Zone(Prague);
        Assert.Equal(Prague, zone.Id);
        Assert.Equal(NodaTime.Offset.FromHours(1), zone.GetUtcOffset(NodaTime.Instant.FromUtc(2026, 1, 15, 12, 0)));
        Assert.Equal(NodaTime.Offset.FromHours(2), zone.GetUtcOffset(NodaTime.Instant.FromUtc(2026, 7, 15, 12, 0)));
    }

    /// <summary>Checks that UTC conversion preserves an ordinary wall time including its 100-nanosecond ticks.</summary>
    [Fact]
    public void Zone_UtcIdentity_LeavesOrdinaryInstantUnshifted()
    {
        Assert.Equal("UTC", TimeRules.Zone("UTC").Id);
        AssertUtc(Utc("2026-07-15T12:34:56.1234567Z"),
            TimeRules.ToUtc(Local("2026-07-15T12:34:56.1234567"), "UTC"));
    }

    /// <summary>Checks that the Event zone, rather than DateTime.Kind, determines the converted instant.</summary>
    /// <param name="kind">The kind metadata attached to identical local wall-clock fields.</param>
    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void ToUtc_InputKindDoesNotOverrideEventZone(DateTimeKind kind)
    {
        var wallTime = new DateTime(2026, 7, 15, 12, 34, 56, kind).AddTicks(1234567);
        AssertUtc(Utc("2026-07-15T10:34:56.1234567Z"), TimeRules.ToUtc(wallTime, Prague));
        AssertUtc(Utc("2026-07-15T12:34:56.1234567Z"), TimeRules.ToUtc(wallTime, "UTC"));
    }

    /// <summary>Checks that every time-rule entry point reports the invalid IANA zone as a field validation error.</summary>
    /// <param name="caller">The Zone, EventWindow, ToUtc, or ValidateQuest entry point to exercise.</param>
    [Theory]
    [InlineData("Zone")]
    [InlineData("EventWindow")]
    [InlineData("ToUtc")]
    [InlineData("ValidateQuest")]
    public void ZoneAndCallers_InvalidIanaId_ThrowValidationForTimeZoneId(string caller)
    {
        Action call = caller switch
        {
            "Zone" => () => TimeRules.Zone(InvalidZone),
            "EventWindow" => () => TimeRules.EventWindow(SpringDay, SpringDay, InvalidZone),
            "ToUtc" => () => TimeRules.ToUtc(Local("2026-07-15T12:00:00"), InvalidZone),
            "ValidateQuest" => () => TimeRules.ValidateQuest(
                Utc("2026-03-29T10:00:00Z"), Utc("2026-03-29T11:00:00Z"), SpringDay, SpringDay, InvalidZone),
            _ => throw new ArgumentOutOfRangeException(nameof(caller))
        };
        AssertValidation(call, "TimeZoneId", "Choose a valid IANA time zone.");
    }

    /// <summary>Gets ordinary Prague summer/winter wall times, optional offsets, and independently derived UTC instants.</summary>
    public static TheoryData<DateTime, TimeSpan?, DateTimeOffset> SeasonalTimes => new()
    {
        { Local("2026-07-15T12:00:00"), null, Utc("2026-07-15T10:00:00Z") },
        { Local("2026-07-15T12:00:00"), TimeSpan.FromHours(2), Utc("2026-07-15T10:00:00Z") },
        { Local("2026-01-15T12:00:00"), null, Utc("2026-01-15T11:00:00Z") },
        { Local("2026-01-15T12:00:00"), TimeSpan.FromHours(1), Utc("2026-01-15T11:00:00Z") }
    };

    /// <summary>Checks ordinary seasonal conversions both with omitted offsets and explicit correct offsets.</summary>
    /// <param name="local">The Prague wall-clock value with unspecified DateTime kind.</param>
    /// <param name="offset">The selected UTC offset, or null for an unambiguous automatic selection.</param>
    /// <param name="expected">The independently specified UTC instant with zero offset.</param>
    [Theory]
    [MemberData(nameof(SeasonalTimes))]
    public void ToUtc_OrdinarySeasonalTimes_ReturnIndependentUtc(DateTime local, TimeSpan? offset, DateTimeOffset expected)
    {
        AssertUtc(expected, TimeRules.ToUtc(local, Prague, offset));
    }

    /// <summary>Checks that an explicit offset inconsistent with a normal seasonal wall time is rejected.</summary>
    /// <param name="local">The invariant-format Prague wall-clock timestamp.</param>
    /// <param name="hours">The incorrect selected UTC offset in whole hours.</param>
    [Theory]
    [InlineData("2026-07-15T12:00:00", 1)]
    [InlineData("2026-07-15T12:00:00", 0)]
    [InlineData("2026-01-15T12:00:00", 2)]
    [InlineData("2026-01-15T12:00:00", -1)]
    public void ToUtc_OrdinarySeasonalTimes_RejectWrongOffset(string local, int hours)
    {
        AssertValidation(() => TimeRules.ToUtc(Local(local), Prague, TimeSpan.FromHours(hours)), "Offset", InvalidOffsetMessage);
    }

    /// <summary>Builds the first, interior, and last tick of Prague's 2026 spring gap with all relevant offset choices.</summary>
    /// <returns>Nonexistent local times paired with null, one-hour, or two-hour selected offsets.</returns>
    public static TheoryData<DateTime, TimeSpan?> GapTimes()
    {
        var data = new TheoryData<DateTime, TimeSpan?>();
        foreach (var local in new[]
                 {
                     Local("2026-03-29T02:00:00"),
                     Local("2026-03-29T02:30:00"),
                     Local("2026-03-29T02:59:59.9999999")
                 })
        {
            data.Add(local, null);
            data.Add(local, TimeSpan.FromHours(1));
            data.Add(local, TimeSpan.FromHours(2));
        }
        return data;
    }

    /// <summary>Checks that choosing an offset never makes a nonexistent spring-gap wall time valid.</summary>
    /// <param name="local">A wall time within Prague's 2026 daylight-saving gap.</param>
    /// <param name="offset">The absent or explicitly chosen offset on either side of the gap.</param>
    [Theory]
    [MemberData(nameof(GapTimes))]
    public void ToUtc_SpringGap_RejectsNonexistentLocalTime(DateTime local, TimeSpan? offset)
    {
        AssertValidation(() => TimeRules.ToUtc(local, Prague, offset),
            "LocalTime", "This local time does not exist because of daylight saving.");
    }

    /// <summary>Gets immediately adjacent valid spring/autumn wall times and exact expected UTC ticks.</summary>
    public static TheoryData<DateTime, DateTimeOffset> DstAdjacentTimes => new()
    {
        { Local("2026-03-29T01:59:59.9999999"), Utc("2026-03-29T00:59:59.9999999Z") },
        { Local("2026-03-29T03:00:00"), Utc("2026-03-29T01:00:00Z") },
        { Local("2026-10-25T01:59:59.9999999"), Utc("2026-10-24T23:59:59.9999999Z") },
        { Local("2026-10-25T03:00:00"), Utc("2026-10-25T02:00:00Z") }
    };

    /// <summary>Checks valid neighbors of DST discontinuities without losing ticks or a UTC date rollover.</summary>
    /// <param name="local">The valid wall time immediately before or after the discontinuity.</param>
    /// <param name="expected">The independently derived UTC instant.</param>
    [Theory]
    [MemberData(nameof(DstAdjacentTimes))]
    public void ToUtc_AdjacentToDstDiscontinuity_ReturnsExactTick(DateTime local, DateTimeOffset expected)
    {
        AssertUtc(expected, TimeRules.ToUtc(local, Prague));
    }

    /// <summary>Checks distinct errors for omitted and invalid offsets during Prague's autumn overlap.</summary>
    /// <param name="wallTime">An invariant-format ambiguous local time in the repeated hour.</param>
    [Theory]
    [InlineData("2026-10-25T02:00:00")]
    [InlineData("2026-10-25T02:30:00")]
    [InlineData("2026-10-25T02:59:59.9999999")]
    public void ToUtc_AutumnOverlap_RequiresExplicitValidOffset(string wallTime)
    {
        var local = Local(wallTime);
        AssertValidation(() => TimeRules.ToUtc(local, Prague),
            "Offset", "Choose the offset for this ambiguous local time.");
        AssertValidation(() => TimeRules.ToUtc(local, Prague, TimeSpan.FromHours(3)),
            "Offset", InvalidOffsetMessage);
    }

    /// <summary>Gets overlap boundary/interior wall times with independently specified earlier and later UTC instants.</summary>
    public static TheoryData<DateTime, DateTimeOffset, DateTimeOffset> OverlapTimes => new()
    {
        { Local("2026-10-25T02:00:00"), Utc("2026-10-25T00:00:00Z"), Utc("2026-10-25T01:00:00Z") },
        { Local("2026-10-25T02:30:00"), Utc("2026-10-25T00:30:00Z"), Utc("2026-10-25T01:30:00Z") },
        { Local("2026-10-25T02:59:59.9999999"), Utc("2026-10-25T00:59:59.9999999Z"), Utc("2026-10-25T01:59:59.9999999Z") }
    };

    /// <summary>Checks both explicit overlap branches and their exact one-hour separation.</summary>
    /// <param name="local">An ambiguous Prague wall time.</param>
    /// <param name="earlierExpected">The UTC instant selected by the two-hour summer offset.</param>
    /// <param name="laterExpected">The UTC instant selected by the one-hour winter offset.</param>
    [Theory]
    [MemberData(nameof(OverlapTimes))]
    public void ToUtc_AutumnOverlap_BothBranchesReturnIndependentInstants(
        DateTime local, DateTimeOffset earlierExpected, DateTimeOffset laterExpected)
    {
        var earlier = TimeRules.ToUtc(local, Prague, TimeSpan.FromHours(2));
        var later = TimeRules.ToUtc(local, Prague, TimeSpan.FromHours(1));
        AssertUtc(earlierExpected, earlier);
        AssertUtc(laterExpected, later);
        Assert.Equal(TimeSpan.FromHours(1), later - earlier);
    }

    /// <summary>Checks same-day and multi-day Event windows through midnight after the inclusive end date.</summary>
    /// <param name="endDay">The inclusive end day in July 2026.</param>
    /// <param name="expectedEnd">The invariant-format expected exclusive UTC endpoint.</param>
    /// <param name="hours">The expected elapsed window length in hours.</param>
    [Theory]
    [InlineData(15, "2026-07-15T22:00:00Z", 24)]
    [InlineData(16, "2026-07-16T22:00:00Z", 48)]
    public void EventWindow_SameDayAndMultiDay_IncludesWholeEndDate(int endDay, string expectedEnd, int hours)
    {
        var (start, end) = TimeRules.EventWindow(new DateOnly(2026, 7, 15), new DateOnly(2026, 7, endDay), Prague);
        Assert.Equal(Utc("2026-07-14T22:00:00Z"), start.ToUniversalTime());
        Assert.Equal(Utc(expectedEnd), end.ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(hours), end - start);
        Assert.Equal(TimeSpan.FromHours(2), start.Offset);
        Assert.Equal(TimeSpan.FromHours(2), end.Offset);
    }

    /// <summary>Checks 23-hour and 25-hour Event days with independently expected endpoints and differing local offsets.</summary>
    /// <param name="month">The DST-transition month in 2026.</param>
    /// <param name="day">The inclusive single-day Event date within that month.</param>
    /// <param name="expectedStart">The invariant-format expected UTC start.</param>
    /// <param name="expectedEnd">The invariant-format expected exclusive UTC end.</param>
    /// <param name="hours">The actual elapsed duration in hours.</param>
    /// <param name="startOffset">The local start's UTC offset in hours.</param>
    /// <param name="endOffset">The local end's UTC offset in hours.</param>
    [Theory]
    [InlineData(3, 29, "2026-03-28T23:00:00Z", "2026-03-29T22:00:00Z", 23, 1, 2)]
    [InlineData(10, 25, "2026-10-24T22:00:00Z", "2026-10-25T23:00:00Z", 25, 2, 1)]
    public void EventWindow_DstDays_HaveExactInclusiveDateBoundaries(
        int month, int day, string expectedStart, string expectedEnd, int hours, int startOffset, int endOffset)
    {
        var date = new DateOnly(2026, month, day);
        var (start, end) = TimeRules.EventWindow(date, date, Prague);
        Assert.Equal(Utc(expectedStart), start.ToUniversalTime());
        Assert.Equal(Utc(expectedEnd), end.ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(hours), end - start);
        Assert.Equal(TimeSpan.FromHours(startOffset), start.Offset);
        Assert.Equal(TimeSpan.FromHours(endOffset), end.Offset);
    }

    /// <summary>Gets reversed and maximum-date Event ranges with exact expected validation messages.</summary>
    public static TheoryData<DateOnly, DateOnly, string> InvalidEventDates => new()
    {
        { new DateOnly(2026, 7, 15), new DateOnly(2026, 7, 14), "End date must not precede start date." },
        { new DateOnly(2026, 7, 15), DateOnly.MaxValue, "End date is outside the supported range." },
        { DateOnly.MaxValue, DateOnly.MaxValue, "End date is outside the supported range." }
    };

    /// <summary>Checks explicit EndDate validation rather than accepting reversed dates or leaking date overflow.</summary>
    /// <param name="start">The requested inclusive Event start date.</param>
    /// <param name="end">The invalid inclusive Event end date.</param>
    /// <param name="message">The exact domain validation message required for this partition.</param>
    [Theory]
    [MemberData(nameof(InvalidEventDates))]
    public void EventWindow_InvalidDateRange_ThrowsEndDateValidation(DateOnly start, DateOnly end, string message)
    {
        AssertValidation(() => TimeRules.EventWindow(start, end, Prague), "EndDate", message);
    }

    /// <summary>Checks the last supported inclusive UTC end date and its representable next-midnight endpoint.</summary>
    [Fact]
    public void EventWindow_LastSupportedEndDate_ReturnsMaxDateExclusiveMidnight()
    {
        var (start, end) = TimeRules.EventWindow(new DateOnly(9999, 12, 30), new DateOnly(9999, 12, 30), "UTC");
        AssertUtc(Utc("9999-12-30T00:00:00Z"), start);
        AssertUtc(Utc("9999-12-31T00:00:00Z"), end);
        Assert.Equal(TimeSpan.FromDays(1), end - start);
    }

    /// <summary>Checks a single-day UTC Event beginning at DateOnly.MinValue without lower-bound overflow.</summary>
    [Fact]
    public void EventWindow_FirstSupportedUtcDate_IncludesExactlyOneDay()
    {
        var (start, end) = TimeRules.EventWindow(DateOnly.MinValue, DateOnly.MinValue, "UTC");
        AssertUtc(Utc("0001-01-01T00:00:00Z"), start);
        AssertUtc(Utc("0001-01-02T00:00:00Z"), end);
        Assert.Equal(TimeSpan.FromDays(1), end - start);
    }

    /// <summary>Checks domain validation when either window endpoint falls on Apia's skipped civil date.</summary>
    /// <param name="day">The December 2011 day whose start or exclusive next-day endpoint is skipped.</param>
    [Theory]
    [InlineData(30)]
    [InlineData(29)]
    public void EventWindow_SkippedDate_ThrowsDomainValidation(int day)
    {
        // Day 30: skipped start; day 29: exclusive end falls on the skipped day.
        var date = new DateOnly(2011, 12, day);
        AssertValidation(() => TimeRules.EventWindow(date, date, "Pacific/Apia"),
            "StartDate", "An Event date does not exist in the selected time zone.");
    }

    /// <summary>Gets exact-boundary, interior, and one-tick-long valid intervals on Prague's DST-transition dates.</summary>
    public static TheoryData<DateTimeOffset, DateTimeOffset, DateOnly> AcceptedIntervals => new()
    {
        { SpringStart, SpringEnd, SpringDay },
        { Utc("2026-03-29T10:00:00Z"), Utc("2026-03-29T11:00:00Z"), SpringDay },
        { SpringStart.AddTicks(1), SpringEnd, SpringDay },
        { SpringStart, SpringEnd.AddTicks(-1), SpringDay },
        { SpringStart.AddTicks(1), SpringEnd.AddTicks(-1), SpringDay },
        { Utc("2026-03-29T10:00:00Z"), Utc("2026-03-29T10:00:00.0000001Z"), SpringDay },
        { SpringStart, SpringStart.AddTicks(1), SpringDay },
        { SpringEnd.AddTicks(-1), SpringEnd, SpringDay },
        { Utc("2026-10-24T22:00:00Z"), Utc("2026-10-25T23:00:00Z"), AutumnDay }
    };

    /// <summary>Checks inclusive containment while pairing every accepted interval with a zero-duration rejection.</summary>
    /// <param name="start">The independently specified Quest start instant.</param>
    /// <param name="end">The strictly later Quest end instant.</param>
    /// <param name="date">The parent Event's single inclusive date in Prague.</param>
    [Theory]
    [MemberData(nameof(AcceptedIntervals))]
    public void ValidateQuest_ExactAndInteriorWindowIntervals_AreAccepted(
        DateTimeOffset start, DateTimeOffset end, DateOnly date)
    {
        Assert.Null(Record.Exception(() => TimeRules.ValidateQuest(start, end, date, date, Prague)));
        AssertValidation(() => TimeRules.ValidateQuest(start, start, date, date, Prague),
            "EndUtc", "Quest end must be after start.");
    }

    /// <summary>Gets equal endpoints and an end one 100-nanosecond tick before the start.</summary>
    public static TheoryData<DateTimeOffset, DateTimeOffset> NonPositiveIntervals => new()
    {
        { Utc("2026-03-29T10:00:00Z"), Utc("2026-03-29T10:00:00Z") },
        { SpringStart, SpringStart },
        { SpringEnd, SpringEnd },
        { Utc("2026-03-29T10:00:00Z"), Utc("2026-03-29T09:59:59.9999999Z") }
    };

    /// <summary>Checks strict positive Quest duration at interior and Event-window boundaries.</summary>
    /// <param name="start">The Quest start instant.</param>
    /// <param name="end">The equal or earlier end instant that must fail EndUtc validation.</param>
    [Theory]
    [MemberData(nameof(NonPositiveIntervals))]
    public void ValidateQuest_NonPositiveInterval_ThrowsEndUtcValidation(DateTimeOffset start, DateTimeOffset end)
    {
        AssertValidation(() => TimeRules.ValidateQuest(start, end, SpringDay, SpringDay, Prague),
            "EndUtc", "Quest end must be after start.");
    }

    /// <summary>Gets intervals crossing either containment bound by one tick and intervals wholly outside the Event.</summary>
    public static TheoryData<DateTimeOffset, DateTimeOffset> OutsideIntervals => new()
    {
        { SpringStart.AddTicks(-1), SpringEnd },
        { SpringStart, SpringEnd.AddTicks(1) },
        { SpringStart.AddTicks(-1), SpringEnd.AddTicks(1) },
        { Utc("2026-03-28T20:00:00Z"), SpringStart.AddTicks(-1) },
        { SpringEnd.AddTicks(1), Utc("2026-03-30T01:00:00Z") }
    };

    /// <summary>Checks StartUtc containment validation for lower, upper, combined, and wholly outside violations.</summary>
    /// <param name="start">The Quest start instant in the independently specified outside interval.</param>
    /// <param name="end">The strictly later Quest end instant.</param>
    [Theory]
    [MemberData(nameof(OutsideIntervals))]
    public void ValidateQuest_OneTickOutside_ThrowsStartUtcValidation(DateTimeOffset start, DateTimeOffset end)
    {
        AssertValidation(() => TimeRules.ValidateQuest(start, end, SpringDay, SpringDay, Prague),
            "StartUtc", "Quest must fit within the Event dates.");
    }

    /// <summary>Checks that containment compares instants rather than the wall clocks of differing offsets.</summary>
    /// <param name="startTicks">The number of 100-nanosecond ticks added to the exact Event start.</param>
    /// <param name="endTicks">The number of 100-nanosecond ticks added to the exact Event end.</param>
    /// <param name="accepted">Whether both UTC and offset representations must satisfy containment.</param>
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, -1, true)]
    [InlineData(-1, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(-1, 1, false)]
    public void ValidateQuest_EquivalentOffsetRepresentations_UseInstants(int startTicks, int endTicks, bool accepted)
    {
        var start = SpringStart.AddTicks(startTicks);
        var end = SpringEnd.AddTicks(endTicks);
        // Deliberately different offsets push wall-clock values beyond both event bounds.
        var representedStart = start.ToOffset(TimeSpan.FromHours(-5));
        var representedEnd = end.ToOffset(TimeSpan.FromHours(9));
        if (accepted)
        {
            Assert.Null(Record.Exception(() => TimeRules.ValidateQuest(start, end, SpringDay, SpringDay, Prague)));
            Assert.Null(Record.Exception(() => TimeRules.ValidateQuest(
                representedStart, representedEnd, SpringDay, SpringDay, Prague)));
            AssertValidation(() => TimeRules.ValidateQuest(
                    representedStart, representedStart, SpringDay, SpringDay, Prague),
                "EndUtc", "Quest end must be after start.");
        }
        else
        {
            AssertValidation(() => TimeRules.ValidateQuest(start, end, SpringDay, SpringDay, Prague),
                "StartUtc", "Quest must fit within the Event dates.");
            AssertValidation(() => TimeRules.ValidateQuest(representedStart, representedEnd, SpringDay, SpringDay, Prague),
                "StartUtc", "Quest must fit within the Event dates.");
        }
    }

    /// <summary>Checks that Quest validation preserves parent Event date errors before interval evaluation.</summary>
    /// <param name="start">The requested inclusive parent start date.</param>
    /// <param name="end">The invalid inclusive parent end date.</param>
    /// <param name="message">The exact parent-date validation message expected through the Quest API.</param>
    [Theory]
    [MemberData(nameof(InvalidEventDates))]
    public void ValidateQuest_InvalidEventDates_PropagatesDateValidation(DateOnly start, DateOnly end, string message)
    {
        AssertValidation(() => TimeRules.ValidateQuest(
                Utc("2026-07-15T10:00:00Z"), Utc("2026-07-15T11:00:00Z"), start, end, Prague),
            "EndDate", message);
    }

    private static DateTime Local(string value) =>
        DateTime.SpecifyKind(DateTime.Parse(value, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);

    private static DateTimeOffset Utc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static void AssertUtc(DateTimeOffset expected, DateTimeOffset actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(TimeSpan.Zero, actual.Offset);
    }

    private static void AssertValidation(Action action, string field, string message)
    {
        var error = Assert.Throws<DomainException>(action);
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal(field, error.Field);
        Assert.Equal(message, error.Message);
    }
}
