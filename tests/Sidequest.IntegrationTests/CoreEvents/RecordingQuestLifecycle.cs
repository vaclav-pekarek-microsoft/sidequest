using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Domain.Model;

namespace Sidequest.IntegrationTests.CoreEvents;

internal sealed class RecordingQuestLifecycle : IQuestEventLifecycle
{
    internal List<(string Action, Guid Event, Guid? User, Guid? Actor, string Reason, DateTimeOffset Now)> Calls { get; } = [];
    internal Exception? FailureAfterChildSave { get; set; }
    internal bool FlushChildSql { get; set; } = true;

    /// <inheritdoc />
    public async Task CancelForEventAsync(ISidequestDbContext db, Guid eventId, Guid? actorId, string reason,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Calls.Add(("Cancel", eventId, null, actorId, reason, now));
        await ChangeChildrenAsync(db, eventId, QuestStatus.Cancelled, cancellationToken);
    }

    /// <inheritdoc />
    public async Task CompleteForEventAsync(ISidequestDbContext db, Guid eventId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(("Complete", eventId, null, null, "", now));
        await ChangeChildrenAsync(db, eventId, QuestStatus.Completed, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveMemberParticipationAsync(ISidequestDbContext db, Guid eventId, Guid userId, Guid actorId,
        string reason, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(("Remove", eventId, userId, actorId, reason, now));
        return FailureAfterChildSave is null ? Task.CompletedTask : Task.FromException(FailureAfterChildSave);
    }

    private async Task ChangeChildrenAsync(ISidequestDbContext db, Guid eventId, QuestStatus status, CancellationToken cancellationToken)
    {
        var children = await db.Quests.Where(x => x.EventId == eventId).ToListAsync(cancellationToken);
        foreach (var child in children)
            child.Status = status;
        // Flush within the caller's transaction to prove rollback covers already-issued child SQL, not just tracking.
        if (FlushChildSql)
            await db.SaveChangesAsync(cancellationToken);
        if (FailureAfterChildSave is not null)
            throw FailureAfterChildSave;
    }
}
