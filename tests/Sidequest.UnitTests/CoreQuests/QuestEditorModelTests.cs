using System.ComponentModel.DataAnnotations;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Quests;

namespace Sidequest.UnitTests.CoreQuests;

/// <summary>Checks the Quest form's explicit DST-offset input and client-side content boundaries without substituting for server validation.</summary>
public sealed class QuestEditorModelTests
{
    /// <summary>Missing offset remains null; fractional positive and negative offsets are preserved without guessing or rounding.</summary>
    /// <param name="hours">Explicit offset hours, or null for an unresolved local time.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("-3.5")]
    [InlineData("5.75")]
    public void ToInput_PreservesExplicitOffsetAndAllContent(string? hours)
    {
        var value = hours is null ? (decimal?)null : decimal.Parse(hours, System.Globalization.CultureInfo.InvariantCulture);
        var model = new QuestEditorModel
        {
            Title = "Valid title", Description = "Plain <text>", Location = "Room", Capacity = 2,
            Start = new(2026, 10, 25, 2, 30, 0), End = new(2026, 10, 25, 4, 0, 0),
            StartOffsetHours = value, EndOffsetHours = value, Visibility = QuestVisibility.Private
        };
        var input = model.ToInput();
        Assert.Equal(model.Start, input.StartLocal);
        Assert.Equal(model.End, input.EndLocal);
        Assert.Equal(value is null ? null : TimeSpan.FromHours((double)value.Value), input.StartOffset);
        Assert.Equal(input.StartOffset, input.EndOffset);
        Assert.Equal(model.Title, input.Title);
        Assert.Equal(model.Description, input.Description);
        Assert.Equal(model.Location, input.Location);
        Assert.Equal(2, input.SuggestedCapacity);
        Assert.Equal(QuestVisibility.Private, input.Visibility);
    }

    /// <summary>Out-of-range offsets fail explicitly rather than overflowing TimeSpan or silently normalizing input.</summary>
    /// <param name="hours">Offset outside the accepted civil range.</param>
    [Theory]
    [InlineData(-14.01)]
    [InlineData(14.01)]
    public void ToInput_RejectsOutOfRangeOffsets(double hours)
    {
        var model = new QuestEditorModel { StartOffsetHours = (decimal)hours };
        var error = Assert.Throws<DomainException>(() => model.ToInput());
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal("Offset", error.Field);
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
