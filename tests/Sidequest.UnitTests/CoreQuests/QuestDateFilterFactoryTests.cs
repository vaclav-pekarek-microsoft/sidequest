using System.Globalization;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Quests;

namespace Sidequest.UnitTests.CoreQuests;

/// <summary>Verifies civil date filters preserve DST day lengths and explicitly selected UTC fallback semantics.</summary>
public sealed class QuestDateFilterFactoryTests
{
    /// <summary>A local inclusive day becomes the exact exclusive UTC interval, including 23-hour and 25-hour DST days.</summary>
    /// <param name="day">Inclusive local date.</param>
    /// <param name="zone">Explicit IANA zone.</param>
    /// <param name="expectedFrom">Expected inclusive UTC lower boundary.</param>
    /// <param name="expectedUntil">Expected exclusive UTC upper boundary.</param>
    [Theory]
    [InlineData("2026-03-29", "Europe/Prague", "2026-03-28T23:00:00Z", "2026-03-29T22:00:00Z")]
    [InlineData("2026-10-25", "Europe/Prague", "2026-10-24T22:00:00Z", "2026-10-25T23:00:00Z")]
    [InlineData("2026-07-15", "Etc/UTC", "2026-07-15T00:00:00Z", "2026-07-16T00:00:00Z")]
    public void LocalDay_MapsExactUtcBoundaries(string day, string zone, string expectedFrom, string expectedUntil)
    {
        var date = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var filter = QuestDateFilterFactory.FromDates(date, date, zone);
        Assert.Equal(DateTimeOffset.Parse(expectedFrom, CultureInfo.InvariantCulture), filter.FromUtc);
        Assert.Equal(DateTimeOffset.Parse(expectedUntil, CultureInfo.InvariantCulture), filter.UntilUtc);
    }

    /// <summary>Unspecified boundaries remain unbounded rather than silently restricting discovery to the current date.</summary>
    [Fact]
    public void MissingDates_KeepIndependentUnboundedEndpoints()
    {
        var date = new DateOnly(2026, 7, 15);
        var none = QuestDateFilterFactory.FromDates(null, null, "Etc/UTC");
        Assert.Null(none.FromUtc);
        Assert.Null(none.UntilUtc);
        var from = QuestDateFilterFactory.FromDates(date, null, "Etc/UTC");
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero), from.FromUtc);
        Assert.Null(from.UntilUtc);
        var until = QuestDateFilterFactory.FromDates(null, date, "Etc/UTC");
        Assert.Null(until.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero), until.UntilUtc);
    }

    /// <summary>Reversed, nonexistent civil dates and invalid zones fail explicitly.</summary>
    [Fact]
    public void InvalidDateRanges_AreRejectedWithoutGuessing()
    {
        Assert.Equal(ErrorCode.Validation, Assert.Throws<DomainException>(() =>
            QuestDateFilterFactory.FromDates(new DateOnly(2026, 7, 16), new DateOnly(2026, 7, 15), "Etc/UTC")).Code);
        Assert.Equal(ErrorCode.Validation, Assert.Throws<DomainException>(() =>
            QuestDateFilterFactory.FromDates(new DateOnly(2011, 12, 30), null, "Pacific/Apia")).Code);
        Assert.Equal(ErrorCode.Validation, Assert.Throws<DomainException>(() =>
            QuestDateFilterFactory.FromDates(null, null, "Invalid/Zone")).Code);
    }
}
