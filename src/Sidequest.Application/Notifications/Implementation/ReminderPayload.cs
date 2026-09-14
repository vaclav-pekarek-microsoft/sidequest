using System.Text.Json;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Notifications.Implementation;

/// <summary>Version-one reminder schedule snapshot; identity remains stable through preference replacement and retries.</summary>
/// <param name="QuestId">Quest whose current start revision must match.</param>
/// <param name="UserId">Sole intended attendee.</param>
/// <param name="StartRevision">Start revision defining one logical reminder.</param>
/// <param name="StartUtc">Expected start instant, checked independently of the revision.</param>
/// <param name="ScheduledUtc">Due time after immediate-window adjustment; used for bounded lateness.</param>
/// <param name="ReminderHours">Exact lead time used to distinguish explicit preference replacement from an outbox replay.</param>
public sealed record ReminderPayload(Guid QuestId, Guid UserId, long StartRevision,
    DateTimeOffset StartUtc, DateTimeOffset ScheduledUtc, decimal ReminderHours = 1m)
{
    /// <summary>Reads a version-one reminder and rejects malformed identity, UTC instants, lead precision and impossible due windows.</summary>
    /// <param name="json">Persisted schedule payload, never provider content.</param>
    /// <returns>A validated schedule snapshot; current domain state still requires separate authorization.</returns>
    /// <exception cref="DomainException">The persisted payload needs explicit repair before processing or replay.</exception>
    public static ReminderPayload Parse(string json)
    {
        ReminderPayload? payload;
        try { payload = JsonSerializer.Deserialize<ReminderPayload>(json); }
        catch (JsonException) { throw Invalid(); }
        if (payload is null || payload.QuestId == Guid.Empty || payload.UserId == Guid.Empty || payload.StartRevision < 0 ||
            payload.StartUtc == default || payload.ScheduledUtc == default ||
            payload.StartUtc.Offset != TimeSpan.Zero || payload.ScheduledUtc.Offset != TimeSpan.Zero ||
            payload.ScheduledUtc >= payload.StartUtc)
            throw Invalid();
        NotificationRules.Validate(new(false, true, true, payload.ReminderHours, null));
        var leadTicks = decimal.ToInt64(payload.ReminderHours * TimeSpan.TicksPerHour);
        if (payload.StartUtc.UtcTicks < leadTicks || payload.ScheduledUtc.UtcTicks < payload.StartUtc.UtcTicks - leadTicks)
            throw Invalid();
        return payload;
    }

    private static DomainException Invalid() => new(ErrorCode.Validation, "Stored reminder payload requires repair before processing.");
}
