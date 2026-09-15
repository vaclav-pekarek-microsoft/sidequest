using Sidequest.Domain.Rules;

namespace Sidequest.UnitTests.FoundationDomain;

/// <summary>Checks that domain failures preserve their public error code, message, and optional field contract.</summary>
public sealed class DomainExceptionTests
{
    /// <summary>Checks every declared error code and the message/field available to an exception consumer.</summary>
    /// <param name="code">The declared domain error category to preserve.</param>
    [Theory]
    [InlineData(ErrorCode.Validation)]
    [InlineData(ErrorCode.NotFound)]
    [InlineData(ErrorCode.Forbidden)]
    [InlineData(ErrorCode.Conflict)]
    [InlineData(ErrorCode.DependencyUnavailable)]
    public void Constructor_PreservesCodeMessageAndField(ErrorCode code)
    {
        Action throwError = () => throw new DomainException(code, "Quest end must be after start.", "EndUtc");
        Exception caught = Assert.Throws<DomainException>(throwError);

        var error = Assert.IsType<DomainException>(caught);
        Assert.Equal(code, error.Code);
        Assert.Equal("Quest end must be after start.", caught.Message);
        Assert.Equal("EndUtc", error.Field);
        Assert.False(error.IsPermanentDependencyFailure);
        Assert.Null(error.RetryAfter);
    }

    /// <summary>Checks omitted, null, empty, and named fields without losing empty or multiline messages.</summary>
    /// <param name="omitField">Whether to invoke the constructor without the optional field argument.</param>
    /// <param name="field">The explicit field name, or null when no field applies.</param>
    /// <param name="message">The exact empty or multiline message expected from the exception.</param>
    [Theory]
    [InlineData(true, null, "")]
    [InlineData(false, null, "")]
    [InlineData(false, "", "")]
    [InlineData(false, "Quest.StartUtc", "")]
    [InlineData(true, null, "Cannot save!\nReload, then retry.")]
    [InlineData(false, null, "Cannot save!\nReload, then retry.")]
    [InlineData(false, "", "Cannot save!\nReload, then retry.")]
    [InlineData(false, "Quest.StartUtc", "Cannot save!\nReload, then retry.")]
    public void Constructor_DefaultAndExplicitField_ArePreserved(bool omitField, string? field, string message)
    {
        Action throwError = () =>
        {
            if (omitField)
                throw new DomainException(ErrorCode.Conflict, message);
            throw new DomainException(ErrorCode.Conflict, message, field);
        };
        var error = Assert.Throws<DomainException>(throwError);

        Assert.Equal(ErrorCode.Conflict, error.Code);
        Assert.Equal(message, error.Message);
        Assert.Equal(field, error.Field);
        Assert.False(error.IsPermanentDependencyFailure);
        Assert.Null(error.RetryAfter);
    }

    /// <summary>Preserves the dependency presentation contract while explicitly distinguishing operator-required failures from ordinary retryable outages.</summary>
    /// <param name="permanent">The explicitly selected dependency retry classification.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PermanentDependencyMarker_PreservesCategoryMessageAndField(bool permanent)
    {
        var error = new DomainException(ErrorCode.DependencyUnavailable, "Private storage is unavailable.", "Storage",
            isPermanentDependencyFailure: permanent);

        Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
        Assert.Equal("Private storage is unavailable.", error.Message);
        Assert.Equal("Storage", error.Field);
        Assert.Equal(permanent, error.IsPermanentDependencyFailure);
    }

    /// <summary>Rejects attaching dependency-specific retry metadata to an unrelated business category.</summary>
    /// <param name="code">A non-dependency category which must retain its own established semantics.</param>
    [Theory]
    [InlineData(ErrorCode.Validation)]
    [InlineData(ErrorCode.NotFound)]
    [InlineData(ErrorCode.Forbidden)]
    [InlineData(ErrorCode.Conflict)]
    public void PermanentDependencyMarker_RejectsOtherCategories(ErrorCode code)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new DomainException(code, "Unavailable.", isPermanentDependencyFailure: true));

        Assert.Equal("isPermanentDependencyFailure", error.ParamName);
        Assert.StartsWith("Only dependency-unavailable failures may be marked as permanent dependencies.", error.Message, StringComparison.Ordinal);
        var retryError = Assert.Throws<ArgumentException>(() =>
            new DomainException(code, "Unavailable.", retryAfter: TimeSpan.FromSeconds(30)));
        Assert.Equal("retryAfter", retryError.ParamName);
    }

    /// <summary>Preserves nonnegative provider timing without changing dependency presentation or operator-required metadata.</summary>
    /// <param name="seconds">Zero, an ordinary delay, or a delay beyond the automatic queue horizon.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    [InlineData(172800)]
    public void DependencyRetryAfter_PreservesNonnegativeDelay(int seconds)
    {
        var error = new DomainException(ErrorCode.DependencyUnavailable, "Storage unavailable.", "Storage",
            isPermanentDependencyFailure: true, retryAfter: TimeSpan.FromSeconds(seconds));

        Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
        Assert.Equal("Storage", error.Field);
        Assert.Equal("Storage unavailable.", error.Message);
        Assert.True(error.IsPermanentDependencyFailure);
        Assert.Equal(TimeSpan.FromSeconds(seconds), error.RetryAfter);
    }

    /// <summary>Rejects even the first negative tick rather than scheduling before the provider's timing contract.</summary>
    [Fact]
    public void DependencyRetryAfter_RejectsNegativeDelay()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DomainException(ErrorCode.DependencyUnavailable, "Unavailable.", retryAfter: TimeSpan.FromTicks(-1)));

        Assert.Equal("retryAfter", error.ParamName);
    }
}
