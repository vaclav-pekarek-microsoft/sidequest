using NodaTime;
using Sidequest.Web.Components.Events;

namespace Sidequest.UnitTests.CoreEvents;

/// <summary>Verifies friendly zone grouping without replacing real daylight-saving rules or existing identifiers.</summary>
public sealed class EventTimeZoneTests
{
    /// <summary>Regional options group cities, remain unique, and sort numerically across negative and fractional offsets.</summary>
    [Fact]
    public void RegionalChoicesAreGroupedUniqueAndOffsetSorted()
    {
        var options = EventTimeZones.Create(new(2026, 7, 15), "Europe/Prague");
        Assert.Equal(options.Count, options.Select(option => option.Id).Distinct().Count());
        Assert.Equal(options.OrderBy(option => option.OffsetSeconds).ThenBy(option => option.Label, StringComparer.Ordinal), options);
        Assert.Equal(-12 * 3600, options[0].OffsetSeconds);
        Assert.Equal(14 * 3600, options[^1].OffsetSeconds);
        var prague = Assert.Single(options, option => option.Id == "Europe/Prague");
        Assert.Contains("Prague", prague.Label);
        Assert.Contains("Budapest", prague.Label);
        Assert.Equal(7200, prague.OffsetSeconds);
        Assert.All(options, option =>
        {
            Assert.NotNull(DateTimeZoneProviders.Tzdb.GetZoneOrNull(option.Id));
            Assert.Matches(@"^\(UTC[+-]\d{2}:\d{2}\) .+", option.Label);
            Assert.DoesNotContain("_", option.Label);
            Assert.DoesNotContain("/", option.Label);
        });
    }

    /// <summary>Identifiers including aliases and unmapped fixed zones survive editor rendering without normalization.</summary>
    /// <param name="id">Supported stored Event identifier.</param>
    [Theory]
    [InlineData("Europe/Prague")]
    [InlineData("US/Eastern")]
    [InlineData("Etc/UTC")]
    [InlineData("Etc/GMT+3")]
    [InlineData("Asia/Kathmandu")]
    public void ExistingIdentifiersArePreserved(string id)
    {
        var options = EventTimeZones.Create(new(2026, 7, 15), id);
        Assert.Single(options, option => option.Id == id);
        Assert.Equal(options.Count, options.Select(option => option.Id).Distinct().Count());
    }

    /// <summary>Seasonal offsets follow the selected date, and regions with different daylight-saving rules remain distinct.</summary>
    /// <param name="month">Winter or summer reference month.</param>
    /// <param name="newYorkOffset">Expected Eastern offset in seconds.</param>
    [Theory]
    [InlineData(1, -18000)]
    [InlineData(7, -14400)]
    public void SeasonalOffsetsDoNotMergeDifferentDaylightSavingRules(int month, int newYorkOffset)
    {
        var options = EventTimeZones.Create(new(2026, month, 15), "America/New_York");
        Assert.Equal(newYorkOffset, Assert.Single(options, option => option.Id == "America/New_York").OffsetSeconds);
        Assert.Equal(-18000, Assert.Single(options, option => option.Id == "America/Bogota").OffsetSeconds);
        Assert.Equal(20700, Assert.Single(options, option => option.Label.Contains("Kathmandu", StringComparison.Ordinal) ||
            option.Label.Contains("Katmandu", StringComparison.Ordinal)).OffsetSeconds);
        Assert.Equal(-34200, Assert.Single(options, option => option.Id == "Pacific/Marquesas").OffsetSeconds);
    }

    /// <summary>Unrecognized input is not turned into a valid selectable zone or silently substituted.</summary>
    [Fact]
    public void UnknownZoneIsNotAddedToChoices()
    {
        var options = EventTimeZones.Create(new(2026, 7, 15), "Unknown/Zone");
        Assert.DoesNotContain(options, option => option.Id == "Unknown/Zone");
    }
}
