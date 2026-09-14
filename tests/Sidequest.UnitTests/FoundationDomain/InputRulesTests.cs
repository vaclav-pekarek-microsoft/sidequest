using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.UnitTests.FoundationDomain;

/// <summary>Checks normalized text, advisory capacity, and opaque rowversion validation without persistence side effects.</summary>
public sealed class InputRulesTests
{
    /// <summary>Checks trimming, internal whitespace preservation, nullable empty normalization, and inclusive text lengths.</summary>
    /// <param name="value">The unnormalized input, including null and surrounding whitespace.</param>
    /// <param name="minimum">The inclusive minimum UTF-16 length.</param>
    /// <param name="maximum">The inclusive maximum UTF-16 length.</param>
    /// <param name="expected">The independently specified normalized result.</param>
    [Theory]
    [InlineData(null, 0, 0, "")]
    [InlineData("", 0, 3, "")]
    [InlineData(" \t\r\n", 0, 3, "")]
    [InlineData(" \t ab \r\n", 2, 4, "ab")]
    [InlineData(" abcd ", 2, 4, "abcd")]
    [InlineData(" a b ", 2, 4, "a b")]
    [InlineData(" \ud83d\ude00 ", 2, 2, "\ud83d\ude00")]
    public void Text_NormalizesBeforeInclusiveBounds_ReturnsExactText(string? value, int minimum, int maximum, string expected)
    {
        Assert.Equal(expected, InputRules.Text(value, "Title", minimum, maximum));
    }

    /// <summary>Checks missing, too-short, and too-long normalized text with exact field-specific diagnostics.</summary>
    /// <param name="value">The value whose trimmed length lies outside two through four UTF-16 code units.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData(" a ")]
    [InlineData(" abcde ")]
    public void Text_OutsideNormalizedBounds_ReportsExactField(string? value)
    {
        AssertError(() => InputRules.Text(value, "Title", 2, 4), ErrorCode.Validation,
            "Title", "Title must contain 2 to 4 characters.");
    }

    /// <summary>Checks inclusive mandatory-reason boundaries after trimming rather than before trimming.</summary>
    /// <param name="length">The normalized reason length in UTF-16 code units.</param>
    /// <param name="accepted">Whether the reason lies within ten through two thousand characters.</param>
    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(100, true)]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public void Reason_TrimmedLengthBoundaries_RequireTenThroughTwoThousand(int length, bool accepted)
    {
        var expected = new string('r', length);
        var value = $" \t{expected}\r\n ";
        if (accepted)
            Assert.Equal(expected, InputRules.Reason(value));
        else
            AssertError(() => InputRules.Reason(value), ErrorCode.Validation,
                "Reason", "Reason must contain 10 to 2000 characters.");
    }

    /// <summary>Checks that absent or whitespace-only mandatory reasons are explicit validation failures.</summary>
    /// <param name="value">The missing or blank reason input.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Reason_MissingOrBlank_ReportsReasonValidation(string? value)
    {
        AssertError(() => InputRules.Reason(value), ErrorCode.Validation,
            "Reason", "Reason must contain 10 to 2000 characters.");
    }

    /// <summary>Checks optional and valid advisory capacities, with a rejected control so a no-op validator cannot pass.</summary>
    /// <param name="capacity">Null for no suggestion, or an allowed attendee count.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(250)]
    [InlineData(10000)]
    public void Capacity_NullAndInclusiveBounds_AcceptsWithoutHardAttendanceLimit(int? capacity)
    {
        Assert.Null(Record.Exception(() => InputRules.Capacity(capacity)));
        AssertError(() => InputRules.Capacity(0), ErrorCode.Validation,
            "SuggestedCapacity", "Suggested capacity must be between 1 and 10000.");
    }

    /// <summary>Checks both adjacent invalid capacity boundaries and representative extreme invalid values.</summary>
    /// <param name="capacity">The invalid suggested attendee count.</param>
    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10001)]
    [InlineData(int.MaxValue)]
    public void Capacity_OutsideBounds_ReportsSuggestedCapacityValidation(int capacity)
    {
        AssertError(() => InputRules.Capacity(capacity), ErrorCode.Validation,
            "SuggestedCapacity", "Suggested capacity must be between 1 and 10000.");
    }

    /// <summary>Checks null, blank, malformed, and seven/nine-byte Base64 values without mutating the supplied entity.</summary>
    /// <param name="expected">The invalid client rowversion representation.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    [InlineData("not-base64!")]
    [InlineData("AQIDBAUGBwg")]
    [InlineData("AQIDBAUGBw==")]
    [InlineData("AQIDBAUGBwgJ")]
    public void Version_InvalidEncodingOrLength_ReportsValidationAndPreservesEntity(string? expected)
    {
        var entity = VersionedEntity();
        AssertError(() => InputRules.Version(entity, expected), ErrorCode.Validation,
            "Version", "A valid item version is required.");
        AssertUnchanged(entity);
    }

    /// <summary>Checks correct decoding of a nonuniform eight-byte token and explicit conflict for an altered byte.</summary>
    /// <param name="expected">The valid Base64 token, optionally containing accepted Base64 whitespace.</param>
    [Theory]
    [InlineData("AQIDBAUGBwg=")]
    [InlineData(" \tAQID\r\nBAUGBwg= ")]
    public void Version_MatchingEightBytes_AcceptsWithoutChangingEntity(string expected)
    {
        var entity = VersionedEntity();
        Assert.Null(Record.Exception(() => InputRules.Version(entity, expected)));
        AssertError(() => InputRules.Version(entity, "AQIDBAUGBwk="), ErrorCode.Conflict,
            null, "This item changed. Reload before saving.");
        AssertUnchanged(entity);
    }

    /// <summary>Checks first-byte, last-byte, and all-byte mismatches as Conflict rather than token-format validation.</summary>
    /// <param name="expected">A well-formed eight-byte token different from the persisted rowversion.</param>
    [Theory]
    [InlineData("CQIDBAUGBwg=")]
    [InlineData("AQIDBAUGBwk=")]
    [InlineData("AAAAAAAAAAA=")]
    public void Version_DifferentEightBytes_ReportsConflictWithoutMutation(string expected)
    {
        var entity = VersionedEntity();
        AssertError(() => InputRules.Version(entity, expected), ErrorCode.Conflict,
            null, "This item changed. Reload before saving.");
        AssertUnchanged(entity);
    }

    private static UserAccount VersionedEntity() => new()
    {
        Id = Guid.Parse("12345678-1234-4234-8234-123456789012"),
        DisplayName = "Unchanged account",
        Version = [1, 2, 3, 4, 5, 6, 7, 8]
    };

    private static void AssertUnchanged(UserAccount entity)
    {
        Assert.Equal(Guid.Parse("12345678-1234-4234-8234-123456789012"), entity.Id);
        Assert.Equal("Unchanged account", entity.DisplayName);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, entity.Version);
    }

    private static void AssertError(Action action, ErrorCode code, string? field, string message)
    {
        var error = Assert.Throws<DomainException>(action);
        Assert.Equal(code, error.Code);
        Assert.Equal(field, error.Field);
        Assert.Equal(message, error.Message);
    }
}
