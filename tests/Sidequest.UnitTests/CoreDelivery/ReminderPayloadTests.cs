using System.Text.Json;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.UnitTests.CoreDelivery;

/// <summary>Pins durable reminder identity, precise lead windows and malformed-data rejection independently of SQL.</summary>
public sealed class ReminderPayloadTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 15, 11, 0, 0, TimeSpan.Zero);
    private static ReminderPayload Valid => new(Guid.Parse("b4bc0606-a111-403b-883f-292cc36f2a53"),
        Guid.Parse("69225071-644e-4c6b-a76c-9d42e501066f"), 1, Start, Start.AddHours(-1));

    /// <summary>Nominal and clamped-inside-window due times retain the exact schedule, recipient and revision.</summary>
    /// <param name="secondsBefore">Remaining seconds until Quest start.</param>
    [Theory]
    [InlineData(3600)]
    [InlineData(1)]
    public void ParseRetainsValidReminderWindow(int secondsBefore)
    {
        var result = ReminderPayload.Parse(JsonSerializer.Serialize(Valid with { ScheduledUtc = Start.AddSeconds(-secondsBefore) }));
        Assert.Equal(Guid.Parse("69225071-644e-4c6b-a76c-9d42e501066f"), result.UserId);
        Assert.Equal(1, result.StartRevision);
        Assert.Equal(Start.AddSeconds(-secondsBefore), result.ScheduledUtc);
        Assert.Equal(1m, result.ReminderHours);
    }

    /// <summary>Malformed identity, revision, UTC or due-window values cannot be treated as successfully completed work.</summary>
    /// <param name="shape">Independently invalid durable field.</param>
    [Theory]
    [InlineData("quest")]
    [InlineData("recipient")]
    [InlineData("revision")]
    [InlineData("utc")]
    [InlineData("early")]
    [InlineData("start")]
    [InlineData("precision")]
    [InlineData("null")]
    [InlineData("json")]
    public void ParseRejectsMalformedReminder(string shape)
    {
        var payload = shape switch
        {
            "quest" => Valid with { QuestId = Guid.Empty },
            "recipient" => Valid with { UserId = Guid.Empty },
            "revision" => Valid with { StartRevision = -1 },
            "utc" => Valid with { ScheduledUtc = Valid.ScheduledUtc.ToOffset(TimeSpan.FromHours(1)) },
            "early" => Valid with { ScheduledUtc = Start.AddSeconds(-3601) },
            "start" => Valid with { ScheduledUtc = Start },
            "precision" => Valid with { ReminderHours = 1.001m },
            _ => Valid
        };
        var json = shape switch { "null" => "null", "json" => "{", _ => JsonSerializer.Serialize(payload) };
        Assert.Equal(ErrorCode.Validation, Assert.Throws<DomainException>(() => ReminderPayload.Parse(json)).Code);
    }
}
