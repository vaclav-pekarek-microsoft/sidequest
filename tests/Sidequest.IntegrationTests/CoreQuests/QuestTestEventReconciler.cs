using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Persistence;

namespace Sidequest.IntegrationTests.CoreQuests;

internal sealed class QuestTestEventReconciler : IEventLifecycleReconciler
{
    private int calls;
    internal int Calls => Volatile.Read(ref calls);
    internal Func<ISidequestDbContext, Guid, DateTimeOffset, CancellationToken, Task<bool>>? OnReconcileAsync { get; set; }

    /// <inheritdoc />
    public async Task<bool> ReconcileAsync(ISidequestDbContext db, Guid eventId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        Assert.NotNull(Assert.IsType<SidequestDbContext>(db).Database.CurrentTransaction);
        if (OnReconcileAsync is not null)
            return await OnReconcileAsync(db, eventId, now, cancellationToken);
        var parent = await db.Events.SingleAsync(e => e.Id == eventId, cancellationToken);
        if (parent.Status == EventStatus.Active &&
            TimeRules.EventWindow(parent.StartDate, parent.EndDate, parent.TimeZoneId).End <= now)
            throw new InvalidOperationException("Configure the Event reconciliation contract double for an overdue-parent scenario.");
        return false;
    }
}
