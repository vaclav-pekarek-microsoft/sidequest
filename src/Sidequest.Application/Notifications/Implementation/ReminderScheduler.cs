using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Notifications.Implementation;

/// <summary>Stages reminder replacement in the caller's transaction; completed logical revisions are never recreated.</summary>
/// <param name="clock">Current UTC time seam.</param>
public sealed class ReminderScheduler(TimeProvider clock)
{
    /// <summary>Reconciles all of one user's joined quests after preferences change, without contacting external providers.</summary>
    /// <param name="db">Caller-owned Serializable context with all affected Event locks acquired before domain reads.</param>
    /// <param name="userId">Recipient whose schedules are affected.</param>
    /// <param name="preference">Current tracked preference values.</param>
    /// <param name="cancellationToken">Cancels reads.</param>
    /// <returns>A task completing after tracked schedule changes are staged, not committed.</returns>
    public async Task ReconcileUserAsync(ISidequestDbContext db, Guid userId, NotificationPreference preference,
        CancellationToken cancellationToken)
    {
        var quests = await db.Quests.Where(q => db.Participations.Any(p => p.QuestId == q.Id &&
            p.UserId == userId && p.Status == ParticipationStatus.Joined)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var quest in quests)
            await ReconcileAsync(db, quest, userId, preference, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces one recipient's obsolete schedule while preserving one logical key per start revision.</summary>
    /// <param name="db">Caller-owned Serializable context holding the Quest's parent Event lock, before inbox or calendar reservations.</param>
    /// <param name="quest">Current Quest state.</param>
    /// <param name="userId">Sole intended attendee.</param>
    /// <param name="preference">Optional current settings; null selects accepted defaults.</param>
    /// <param name="cancellationToken">Cancels persisted fact reads.</param>
    /// <returns>A task completing when changes are staged.</returns>
    public async Task ReconcileAsync(ISidequestDbContext db, Quest quest, Guid userId,
        NotificationPreference? preference, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var key = $"reminder:{quest.Id:N}:{userId:N}:{quest.StartRevision}";
        var existing = await db.ReadReminderSchedulesForUpdateAsync(quest.Id, userId, cancellationToken).ConfigureAwait(false);
        if (existing.Any(x => x.Type != WorkTypes.Reminder || x.QuestId != quest.Id || x.UserId != userId))
            throw new DomainException(ErrorCode.Validation, "Stored reminder identity requires repair before rescheduling.");
        foreach (var stale in existing.Where(x => x.DeduplicationKey != key &&
                     x.Status is WorkStatus.Pending or WorkStatus.Processing))
        {
            stale.Status = WorkStatus.Superseded;
            stale.LeaseId = null;
            stale.LeaseUntilUtc = null;
        }
        var work = existing.SingleOrDefault(x => x.DeduplicationKey == key);
        if (work?.Status == WorkStatus.Completed ||
            work is not null && await db.HasNotificationForUpdateAsync(work.Id, userId, NotificationKind.Reminder,
                cancellationToken).ConfigureAwait(false))
            return;
        var parent = await db.Events.SingleAsync(x => x.Id == quest.EventId, cancellationToken).ConfigureAwait(false);
        var joined = await db.Participations.AnyAsync(x => x.QuestId == quest.Id && x.UserId == userId &&
            x.Status == ParticipationStatus.Joined, cancellationToken).ConfigureAwait(false);
        var active = quest.Status == QuestStatus.Active && parent.Status == EventStatus.Active &&
            TimeRules.EventWindow(parent.StartDate, parent.EndDate, parent.TimeZoneId).End > now;
        var due = joined && active && (preference?.RemindersEnabled ?? true)
            ? NotificationRules.ReminderDue(quest.StartUtc, preference?.ReminderHours ?? 1m, now) : null;
        if (due is null)
        {
            if (work is not null && work.Status != WorkStatus.Completed)
            {
                work.Status = WorkStatus.Superseded;
                work.LeaseId = null;
                work.LeaseUntilUtc = null;
            }
            return;
        }
        if (work is not null && work.Status is WorkStatus.Pending or WorkStatus.Processing or WorkStatus.DeadLetter)
        {
            var previous = ReminderPayload.Parse(work.PayloadJson);
            if (previous.QuestId != quest.Id || previous.UserId != userId ||
                previous.StartRevision != quest.StartRevision)
                throw new DomainException(ErrorCode.Validation, "Stored reminder payload requires repair before rescheduling.");
            if (previous.StartUtc == quest.StartUtc &&
                previous.ReminderHours == (preference?.ReminderHours ?? 1m))
                return;
        }
        if (work is null)
        {
            work = new ScheduledWork { Type = WorkTypes.Reminder, DeduplicationKey = key, QuestId = quest.Id, UserId = userId };
            db.ScheduledWork.Add(work);
        }
        // Invalidating an old lease makes a concurrently resumed handler recheck its token.
        work.Status = WorkStatus.Pending;
        work.DueUtc = due.Value;
        work.LeaseId = null;
        work.LeaseUntilUtc = null;
        work.Attempts = 0;
        work.LastError = null;
        work.PayloadJson = JsonSerializer.Serialize(new ReminderPayload(quest.Id, userId, quest.StartRevision, quest.StartUtc,
            due.Value, preference?.ReminderHours ?? 1m));
    }
}
