using System.ComponentModel.DataAnnotations;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Quests;

namespace Sidequest.UnitTests.CoreQuests;

/// <summary>Checks inherited-zone scheduling and client-side content boundaries without substituting for server authorization.</summary>
public sealed class QuestEditorModelTests
{
    /// <summary>Both repeated occurrences retain their exact offsets and all content without changing the selected instant.</summary>
    /// <param name="hours">Offset mapped internally from the first or second occurrence choice.</param>
    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public void ToInput_PreservesExplicitOffsetAndAllContent(string hours)
    {
        var value = TimeSpan.FromHours(double.Parse(hours, System.Globalization.CultureInfo.InvariantCulture));
        var model = new QuestEditorModel
        {
            Title = "Valid title", Description = "Plain <text>", Location = "Room", Capacity = 2,
            Start = new(2026, 10, 25, 2, 30, 0), End = new(2026, 10, 25, 4, 0, 0),
            StartOffset = value, EndOffset = null, Visibility = QuestVisibility.Private
        };
        var input = model.ToInput("Europe/Prague");
        Assert.Equal(model.Start, input.StartLocal);
        Assert.Equal(model.End, input.EndLocal);
        Assert.Equal(value, input.StartOffset);
        Assert.Null(input.EndOffset);
        Assert.Equal(model.Title, input.Title);
        Assert.Equal(model.Description, input.Description);
        Assert.Equal(model.Location, input.Location);
        Assert.Equal(2, input.SuggestedCapacity);
        Assert.Equal(QuestVisibility.Private, input.Visibility);
    }

    /// <summary>Offsets not matching either candidate fail rather than normalizing the submitted wall-clock time.</summary>
    /// <param name="hours">Offset outside the candidates for the Prague overlap.</param>
    [Theory]
    [InlineData(-14.01)]
    [InlineData(14.01)]
    public void ToInput_RejectsInvalidCandidateOffsets(double hours)
    {
        var model = new QuestEditorModel { Start = new(2026, 10, 25, 2, 30, 0), StartOffset = TimeSpan.FromHours(hours) };
        var error = Assert.Throws<DomainException>(() => model.ToInput("Europe/Prague"));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal("Offset", error.Field);
    }

    /// <summary>Ordinary local times infer the Event offset, including fractional-offset zones, without an editable offset field.</summary>
    /// <param name="zone">Inherited IANA Event zone.</param>
    /// <param name="hours">Expected offset from the bundled zone rules.</param>
    [Theory]
    [InlineData("Europe/Prague", 2)]
    [InlineData("Asia/Kathmandu", 5.75)]
    public void OrdinaryTimesInferBundledEventOffset(string zone, double hours)
    {
        var local = new DateTime(2026, 7, 15, 12, 0, 0);
        Assert.Equal(TimeSpan.FromHours(hours), Assert.Single(QuestLocalTime.Candidates(local, zone)));
        var input = new QuestEditorModel { Start = local, End = local.AddHours(1) }.ToInput(zone);
        Assert.Equal(new DateTimeOffset(local, TimeSpan.FromHours(hours)).ToUniversalTime(),
            TimeRules.ToUtc(input.StartLocal, zone, input.StartOffset));
    }

    /// <summary>Repeated times require an explicit human occurrence choice, and nonexistent times are never shifted.</summary>
    /// <param name="month">Month containing the clock transition.</param>
    /// <param name="day">Local transition date.</param>
    /// <param name="count">Expected number of real instants for the local time.</param>
    [Theory]
    [InlineData(3, 29, 0)]
    [InlineData(10, 25, 2)]
    public void MissingOrRepeatedTimeCannotBeSilentlyResolved(int month, int day, int count)
    {
        var local = new DateTime(2026, month, day, 2, 30, 0);
        Assert.Equal(count, QuestLocalTime.Candidates(local, "Europe/Prague").Count);
        var error = Assert.Throws<DomainException>(() =>
            new QuestEditorModel { Start = local, End = local.AddHours(2) }.ToInput("Europe/Prague"));
        Assert.Equal(ErrorCode.Validation, error.Code);
        if (count == 2)
            Assert.Contains("First occurrence or Second occurrence", error.Message);
        else
            Assert.Contains("does not exist", error.Message);
    }

    /// <summary>Occurrence ordering comes from actual candidate instants even when the clock transition is thirty minutes rather than an hour.</summary>
    [Fact]
    public void HalfHourOverlap_OrdersActualCandidateInstants()
    {
        var local = new DateTime(2026, 4, 5, 1, 45, 0);
        var candidates = QuestLocalTime.Candidates(local, "Australia/Lord_Howe");
        Assert.Equal(new[] { TimeSpan.FromHours(11), TimeSpan.FromHours(10.5) }, candidates);
        var first = TimeRules.ToUtc(local, "Australia/Lord_Howe", candidates[0]);
        var second = TimeRules.ToUtc(local, "Australia/Lord_Howe", candidates[1]);
        Assert.Equal(TimeSpan.FromMinutes(30), second - first);
    }

    /// <summary>Title boundaries are explicit client validation errors rather than requiring a round trip for obvious invalid input.</summary>
    /// <param name="length">Title length in characters.</param>
    /// <param name="valid">Expected validation outcome.</param>
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void TitleValidation_EnforcesExactLengthBoundaries(int length, bool valid)
    {
        var model = new QuestEditorModel { Title = new string('A', length) };
        var results = new List<ValidationResult>();
        Assert.Equal(valid, Validator.TryValidateObject(model, new ValidationContext(model), results, true));
        if (!valid)
            Assert.Contains(results, r => r.MemberNames.Contains(nameof(QuestEditorModel.Title)));
    }
}
