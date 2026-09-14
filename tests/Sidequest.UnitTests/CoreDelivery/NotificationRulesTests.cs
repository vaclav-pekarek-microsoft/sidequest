using Sidequest.Application.Notifications;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Background;

namespace Sidequest.UnitTests.CoreDelivery;

/// <summary>Deterministic boundary tests for exact reminder hours, due-time clamping and bounded retry configuration.</summary>
public sealed class NotificationRulesTests
{
    /// <summary>Accepted hour values retain exact duration, including lower/upper bounds and interior fractions.</summary>
    /// <param name="value">Exact invariant-culture decimal input.</param>
    [Theory]
    [InlineData("0.01")]
    [InlineData("0.5")]
    [InlineData("1")]
    [InlineData("167.99")]
    [InlineData("168")]
    public void ReminderHours_ValidBoundariesPreserveExactDuration(string value)
    {
        var hours = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        var start = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        var actual = NotificationRules.ReminderDue(start, hours, start.AddDays(-8));
        Assert.Equal(start.AddTicks(-decimal.ToInt64(hours * TimeSpan.TicksPerHour)), actual);
    }

    /// <summary>Out-of-range and excessive-precision inputs fail explicitly rather than being clamped or rounded.</summary>
    /// <param name="value">Rejected hour value.</param>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.009")]
    [InlineData("0.011")]
    [InlineData("1.001")]
    [InlineData("168.001")]
    [InlineData("169")]
    public void ReminderHours_InvalidBoundsAndPrecisionFail(string value)
    {
        var input = new PreferenceInput(false, true, true, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), null);
        var error = Assert.Throws<DomainException>(() => NotificationRules.Validate(input));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal("ReminderHours", error.Field);
    }

    /// <summary>Before-window due dates remain exact; inside-window work is immediate; start and later are suppressed.</summary>
    [Fact]
    public void ReminderDue_BeforeInsideAtAndAfterStartAreDistinct()
    {
        var start = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(start.AddHours(-1), NotificationRules.ReminderDue(start, 1, start.AddHours(-2)));
        Assert.Equal(start.AddMinutes(-1), NotificationRules.ReminderDue(start, 1, start.AddMinutes(-1)));
        Assert.Equal(start.AddTicks(-1), NotificationRules.ReminderDue(start, 1, start.AddTicks(-1)));
        Assert.Null(NotificationRules.ReminderDue(start, 1, start));
        Assert.Null(NotificationRules.ReminderDue(start, 1, start.AddTicks(1)));
    }

    /// <summary>Unknown zones and unknown business discriminators fail explicitly.</summary>
    [Fact]
    public void UnknownZoneAndNotificationKindFailExplicitly()
    {
        Assert.Throws<DomainException>(() => NotificationRules.Validate(new(false, true, true, 1, "Not/AZone")));
        Assert.Throws<DomainException>(() => NotificationRules.Summary((Sidequest.Domain.Model.NotificationKind)999));
    }

    /// <summary>Backoff is deterministic, increases across attempts and caps without overflow.</summary>
    [Fact]
    public void RetryDelay_IsDeterministicIncreasingAndBounded()
    {
        var id = Guid.Parse("10203040-0000-0000-0000-000000000000");
        var first = SqlWorkQueue.RetryDelay(1, id);
        Assert.Equal(first, SqlWorkQueue.RetryDelay(1, id));
        Assert.True(SqlWorkQueue.RetryDelay(2, id) > first);
        Assert.InRange(SqlWorkQueue.RetryDelay(int.MaxValue, id), TimeSpan.FromSeconds(900), TimeSpan.FromSeconds(910));
        Assert.InRange(first, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
    }

    /// <summary>Poll periods longer than thirty seconds and leases longer than two minutes cannot silently weaken recovery.</summary>
    [Fact]
    public void WorkerOptions_RejectOutOfContractDurations()
    {
        new DurableWorkOptions().Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkOptions { PollInterval = TimeSpan.FromSeconds(31) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkOptions { LeaseDuration = TimeSpan.FromSeconds(121) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkOptions { ReminderLateness = TimeSpan.Zero }.Validate());
    }
}
