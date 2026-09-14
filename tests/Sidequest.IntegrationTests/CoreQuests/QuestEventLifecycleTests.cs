using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

/// <summary>Proves child state/history/delivery participates in the caller's actual SQL transaction and never commits independently.</summary>
/// <param name="database">Existing fixture owning a uniquely named migrated catalog.</param>
public sealed class QuestEventLifecycleTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Parent cancellation cancels drafts and active/suspended children, not completed/archived children; rollback reverses every effect.</summary>
    /// <param name="commit">Whether the caller commits or rolls back the same staged cascade.</param>
    /// <returns>Completion after child status, parent status, audit, outbox and revision assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCascade_UsesCallerTransaction_AndIsRepeatSafe(bool commit)
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        var service = scenario.Service();
        await service.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join);
        var draftId = await service.CreateAsync(scenario.Seed.Event.Id, scenario.Input());
        var completed = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        completed.Status = QuestStatus.Completed;
        var archived = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        archived.Status = QuestStatus.Archived;
        var suspended = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        suspended.Status = QuestStatus.Suspended;
        await FoundationSeed.PersistAsync(database, completed, archived, suspended);
        var lifecycle = new QuestEventLifecycle(new ChangeWriter());
        await using (var db = database.CreateContext())
        {
            await using var transaction = await db.BeginTransactionAsync();
            await db.LockEventAsync(scenario.Seed.Event.Id);
            var parent = await db.Events.SingleAsync(e => e.Id == scenario.Seed.Event.Id);
            parent.Status = EventStatus.Cancelled;
            await lifecycle.CancelForEventAsync(db, parent.Id, scenario.Seed.Other.Id, "The whole Event is cancelled.", scenario.Clock.Now);
            await lifecycle.CancelForEventAsync(db, parent.Id, scenario.Seed.Other.Id, "The whole Event is cancelled.", scenario.Clock.Now);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            Assert.Equal(3, db.ChangeTracker.Entries<QuestStatusHistory>().Count());
            await db.SaveChangesAsync();
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }
        await using var read = database.CreateContext();
        Assert.Equal(commit ? EventStatus.Cancelled : EventStatus.Active,
            (await read.Events.SingleAsync(e => e.Id == scenario.Seed.Event.Id)).Status);
        Assert.Equal(commit ? QuestStatus.Cancelled : QuestStatus.Active,
            (await read.Quests.SingleAsync(q => q.Id == scenario.Seed.Quest.Id)).Status);
        Assert.Equal(commit ? QuestStatus.Cancelled : QuestStatus.Draft, (await read.Quests.SingleAsync(q => q.Id == draftId)).Status);
        Assert.Equal(commit ? QuestStatus.Cancelled : QuestStatus.Suspended, (await read.Quests.SingleAsync(q => q.Id == suspended.Id)).Status);
        Assert.Equal(QuestStatus.Completed, (await read.Quests.SingleAsync(q => q.Id == completed.Id)).Status);
        Assert.Equal(QuestStatus.Archived, (await read.Quests.SingleAsync(q => q.Id == archived.Id)).Status);
        var childIds = new[] { scenario.Seed.Quest.Id, draftId, suspended.Id };
        Assert.Equal(commit ? 3 : 0, await read.QuestStatusHistory.CountAsync(h => childIds.Contains(h.QuestId)));
        Assert.Equal(commit ? 3 : 0, await read.AuditEntries.CountAsync(h => childIds.Contains(h.ResourceId) && h.Action.Contains("->Cancelled")));
        var envelopes = (await read.OutboxMessages.Where(x => childIds.Contains(x.AggregateId)).ToListAsync())
            .Select(x => JsonSerializer.Deserialize<ChangeEnvelope>(x.PayloadJson)!).ToArray();
        Assert.DoesNotContain(envelopes, x => x.Kind == NotificationKind.QuestCancelled);
        Assert.Equal(commit ? 1 : 0, envelopes.Count(x => x.Kind == NotificationKind.EventCancelled));
        if (commit)
        {
            var active = Assert.Single(envelopes, x => x.Kind == NotificationKind.EventCancelled && x.QuestId == scenario.Seed.Quest.Id);
            Assert.Equal(new[] { scenario.Seed.User.Id }, active.RecipientIds);
            Assert.Equal(new[] { scenario.Seed.User.Id }, active.PreviousAttendeeIds);
            Assert.True(active.CalendarChanged);
            Assert.Equal(9, active.CalendarRevision);
            Assert.DoesNotContain(envelopes, x => x.QuestId == draftId || x.QuestId == suspended.Id);
        }
    }

    /// <summary>Event cancellation sends only distinct attendee withdrawals for sibling Quests; direct cancellation retains each Quest's full status audience.</summary>
    /// <param name="parentCancellation">Whether the Event caller cancels both children or the Quest owner cancels each directly.</param>
    /// <returns>Completion after exact multi-child audience, calendar revision, prior-attendee, history, audit and repeat-safety assertions.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultiChildCancellation_CoalescesParentStatus_AndPreservesDirectQuestAudience(bool parentCancellation)
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        var follower = FoundationSeed.NewUser();
        var invitee = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, follower, invitee);
        await FoundationSeed.PersistAsync(database, scenario.Seed.Membership(follower.Id), scenario.Seed.Membership(invitee.Id));
        var owner = scenario.Service();
        var second = await owner.CreateAsync(scenario.Seed.Event.Id, scenario.Input(QuestVisibility.Private));
        await owner.ChangeStatusAsync(second, (await owner.GetAsync(second)).Summary.Version, QuestStatus.Active, "");
        var ids = new[] { scenario.Seed.Quest.Id, second };
        foreach (var id in ids)
        {
            await owner.InviteAsync(id, scenario.Seed.Other.Id);
            await owner.InviteAsync(id, follower.Id);
            await owner.InviteAsync(id, invitee.Id);
            await scenario.Service(scenario.Seed.Other).ParticipateAsync(id, ParticipationCommand.Join);
            await scenario.Service(follower).ParticipateAsync(id, ParticipationCommand.Follow);
        }
        await scenario.Service(scenario.Seed.Other).ChangeStatusAsync(second, (await owner.GetAsync(second)).Summary.Version,
            QuestStatus.Suspended, "The location needs review.");
        Dictionary<Guid, long> revisions;
        await using (var db = database.CreateContext())
            revisions = await db.Quests.Where(q => ids.Contains(q.Id)).ToDictionaryAsync(q => q.Id, q => q.CalendarRevision);
        const string reason = "The whole Event is cancelled.";
        if (parentCancellation)
        {
            await using var db = database.CreateContext();
            await using var transaction = await db.BeginTransactionAsync();
            await db.LockEventAsync(scenario.Seed.Event.Id);
            (await db.Events.SingleAsync(e => e.Id == scenario.Seed.Event.Id)).Status = EventStatus.Cancelled;
            var lifecycle = new QuestEventLifecycle(new ChangeWriter());
            await lifecycle.CancelForEventAsync(db, scenario.Seed.Event.Id, scenario.Seed.Other.Id, reason, scenario.Clock.Now);
            await lifecycle.CancelForEventAsync(db, scenario.Seed.Event.Id, scenario.Seed.Other.Id, reason, scenario.Clock.Now);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            await db.SaveChangesAsync();
            await lifecycle.CancelForEventAsync(db, scenario.Seed.Event.Id, scenario.Seed.Other.Id, reason, scenario.Clock.Now);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        else
        {
            foreach (var id in ids)
            {
                await owner.ChangeStatusAsync(id, (await owner.GetAsync(id)).Summary.Version, QuestStatus.Cancelled, reason);
                await owner.ChangeStatusAsync(id, (await owner.GetAsync(id)).Summary.Version, QuestStatus.Cancelled, reason);
            }
        }

        await using var read = database.CreateContext();
        var changes = (await read.OutboxMessages.Where(o => ids.Contains(o.AggregateId)).ToListAsync())
            .Select(o => Assert.IsType<ChangeEnvelope>(JsonSerializer.Deserialize<ChangeEnvelope>(o.PayloadJson)))
            .Where(e => e.Kind is NotificationKind.EventCancelled or NotificationKind.QuestCancelled).ToArray();
        Assert.Equal(2, changes.Length);
        var fullAudience = new[] { scenario.Seed.User.Id, scenario.Seed.Other.Id, follower.Id, invitee.Id };
        var expectedRecipients = parentCancellation ? [scenario.Seed.Other.Id] : fullAudience;
        var actor = parentCancellation ? scenario.Seed.Other.Id : scenario.Seed.User.Id;
        foreach (var id in ids)
        {
            var change = Assert.Single(changes, e => e.QuestId == id);
            Assert.Equal(parentCancellation ? NotificationKind.EventCancelled : NotificationKind.QuestCancelled, change.Kind);
            Assert.Equal(expectedRecipients.OrderBy(user => user), change.RecipientIds.OrderBy(user => user));
            Assert.Equal(new[] { scenario.Seed.Other.Id }, change.PreviousAttendeeIds);
            Assert.Equal(scenario.Seed.Event.Id, change.EventId);
            Assert.Equal(actor, change.ActorId);
            Assert.Equal(reason, change.Reason);
            Assert.Equal(scenario.Clock.Now, change.OccurredUtc);
            Assert.Equal(revisions[id] + 1, change.CalendarRevision);
            Assert.True(change.CalendarChanged);
            Assert.True(change.MaterialChange);
            var quest = await read.Quests.SingleAsync(q => q.Id == id);
            Assert.Equal(QuestStatus.Cancelled, quest.Status);
            Assert.Equal(change.CalendarRevision, quest.CalendarRevision);
            var history = Assert.Single(await read.QuestStatusHistory.Where(h => h.QuestId == id && h.Next == QuestStatus.Cancelled).ToListAsync());
            Assert.Equal(id == second ? QuestStatus.Suspended : QuestStatus.Active, history.Previous);
            Assert.Equal(actor, history.ActorId);
            Assert.Equal(reason, history.Reason);
            Assert.Equal(scenario.Clock.Now, history.OccurredUtc);
            var audit = Assert.Single(await read.AuditEntries.Where(a => a.ResourceId == id && a.Action.Contains("->Cancelled")).ToListAsync());
            Assert.Equal(change.ChangeId.ToString("N"), audit.CorrelationId);
            Assert.Equal(actor, audit.ActorId);
            Assert.Equal(reason, audit.Reason);
            Assert.Equal(ParticipationStatus.Joined, (await read.Participations.SingleAsync(p => p.QuestId == id && p.UserId == scenario.Seed.Other.Id)).Status);
            Assert.Equal(ParticipationStatus.Following, (await read.Participations.SingleAsync(p => p.QuestId == id && p.UserId == follower.Id)).Status);
        }
        Assert.Equal(parentCancellation ? EventStatus.Cancelled : EventStatus.Active,
            (await read.Events.SingleAsync(e => e.Id == scenario.Seed.Event.Id)).Status);
        Assert.False(await read.OutboxMessages.AnyAsync(o => o.AggregateId == scenario.Seed.Event.Id));
    }

    /// <summary>Event-end cleanup completes overdue active/suspended children and cancels drafts without participant delivery or calendar revisions.</summary>
    /// <returns>Completion after repeat-safe history and unchanged calendar/outbox assertions.</returns>
    [Fact]
    public async Task CompletionCleanup_PreservesHistoricalCalendars_AndCancelsOnlyUnpublishedDrafts()
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        await service.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join);
        var draftId = await service.CreateAsync(scenario.Seed.Event.Id, scenario.Input());
        var suspended = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        suspended.Status = QuestStatus.Suspended;
        await FoundationSeed.PersistAsync(database, suspended);
        var now = scenario.Seed.Quest.EndUtc;
        await using (var db = database.CreateContext())
        {
            await using var transaction = await db.BeginTransactionAsync();
            await db.LockEventAsync(scenario.Seed.Event.Id);
            var lifecycle = new QuestEventLifecycle(new ChangeWriter());
            await lifecycle.CompleteForEventAsync(db, scenario.Seed.Event.Id, now);
            await lifecycle.CompleteForEventAsync(db, scenario.Seed.Event.Id, now);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await using var read = database.CreateContext();
        var quest = await read.Quests.SingleAsync(q => q.Id == scenario.Seed.Quest.Id);
        Assert.Equal(QuestStatus.Completed, quest.Status);
        Assert.Equal(8, quest.CalendarRevision);
        Assert.Equal(QuestStatus.Completed, (await read.Quests.SingleAsync(q => q.Id == suspended.Id)).Status);
        Assert.Equal(7, (await read.Quests.SingleAsync(q => q.Id == suspended.Id)).CalendarRevision);
        Assert.Equal(QuestStatus.Cancelled, (await read.Quests.SingleAsync(q => q.Id == draftId)).Status);
        Assert.Equal(ParticipationStatus.Joined, (await read.Participations.SingleAsync(p => p.QuestId == quest.Id)).Status);
        var ids = new[] { quest.Id, suspended.Id, draftId };
        Assert.Equal(3, await read.QuestStatusHistory.CountAsync(h => ids.Contains(h.QuestId)));
        Assert.Single(await read.OutboxMessages.Where(x => ids.Contains(x.AggregateId)).ToListAsync());
    }

    /// <summary>Membership loss invalidates grants and both participation states without independently saving or restoring them when membership returns.</summary>
    /// <param name="commit">Whether the owning membership transaction commits or rolls back.</param>
    /// <returns>Completion after membership/grant/participation atomicity and withdrawal snapshot assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipLoss_AtomicallyRevokesInvitationsAndParticipation(bool commit)
    {
        var scenario = await QuestScenario.CreateAsync(database, true);
        var owner = scenario.Service();
        var recipient = scenario.Service(scenario.Seed.Other);
        await owner.InviteAsync(scenario.Seed.Quest.Id, scenario.Seed.Other.Id);
        await recipient.ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join);
        var second = FoundationSeed.NewQuest(scenario.Seed.Event.Id, scenario.Seed.User.Id);
        await FoundationSeed.PersistAsync(database, second);
        await recipient.ParticipateAsync(second.Id, ParticipationCommand.Follow);
        var lifecycle = new QuestEventLifecycle(new ChangeWriter());
        await using (var db = database.CreateContext())
        {
            await using var transaction = await db.BeginTransactionAsync();
            await db.LockEventAsync(scenario.Seed.Event.Id);
            var member = await db.EventMemberships.SingleAsync(m => m.EventId == scenario.Seed.Event.Id && m.UserId == scenario.Seed.Other.Id);
            member.Status = MembershipStatus.Removed;
            await lifecycle.RemoveMemberParticipationAsync(db, scenario.Seed.Event.Id, scenario.Seed.Other.Id,
                scenario.Seed.User.Id, "Event membership was removed.", scenario.Clock.Now);
            await lifecycle.RemoveMemberParticipationAsync(db, scenario.Seed.Event.Id, scenario.Seed.Other.Id,
                scenario.Seed.User.Id, "Event membership was removed.", scenario.Clock.Now);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            await db.SaveChangesAsync();
            if (commit) await transaction.CommitAsync();
            else await transaction.RollbackAsync();
        }
        await using var read = database.CreateContext();
        Assert.Equal(commit ? QuestInvitationStatus.Revoked : QuestInvitationStatus.Active,
            (await read.QuestInvitations.SingleAsync(i => i.QuestId == scenario.Seed.Quest.Id)).Status);
        Assert.Equal(commit ? ParticipationStatus.None : ParticipationStatus.Joined,
            (await read.Participations.SingleAsync(p => p.QuestId == scenario.Seed.Quest.Id)).Status);
        Assert.Equal(commit ? ParticipationStatus.None : ParticipationStatus.Following,
            (await read.Participations.SingleAsync(p => p.QuestId == second.Id)).Status);
        var changes = (await read.OutboxMessages.Where(x => x.AggregateId == scenario.Seed.Quest.Id || x.AggregateId == second.Id).ToListAsync())
            .Select(x => JsonSerializer.Deserialize<ChangeEnvelope>(x.PayloadJson)!).Where(x => x.Kind == NotificationKind.AccessRemoved).ToArray();
        Assert.Equal(commit ? 2 : 0, changes.Length);
        if (commit)
        {
            Assert.Equal(new[] { scenario.Seed.Other.Id }, Assert.Single(changes, c => c.QuestId == scenario.Seed.Quest.Id).PreviousAttendeeIds);
            Assert.All(changes, change => Assert.Equal(new[] { scenario.Seed.Other.Id }, change.AffectedUserIds));
            Assert.Empty(Assert.Single(changes, c => c.QuestId == second.Id).PreviousAttendeeIds!);
            Assert.Equal(new[] { scenario.Seed.Other.Id }, Assert.Single(changes, c => c.QuestId == second.Id).RecipientIds);
            var membership = await read.EventMemberships.SingleAsync(m => m.EventId == scenario.Seed.Event.Id && m.UserId == scenario.Seed.Other.Id);
            membership.Status = MembershipStatus.Active;
            await read.SaveChangesAsync();
            Assert.Equal(ParticipationStatus.None, (await recipient.GetAsync(second.Id)).Summary.Participation);
            Assert.Equal(Sidequest.Domain.Rules.ErrorCode.NotFound, (await Assert.ThrowsAsync<Sidequest.Domain.Rules.DomainException>(() =>
                recipient.GetAsync(scenario.Seed.Quest.Id))).Code);
        }
    }

    /// <summary>Parent cancellation reconciles already-ended published children as Completed instead of withdrawing their historical calendars.</summary>
    /// <param name="suspended">Whether the previously joined Quest had been explicitly suspended.</param>
    /// <returns>Completion after repeated staging, completed state, stable revision and absence of new cancellation delivery.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentCancellation_CompletesAlreadyEndedChildren_WithoutNewWithdrawal(bool suspended)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        var id = scenario.Seed.Quest.Id;
        await service.ParticipateAsync(id, ParticipationCommand.Join);
        if (suspended)
            await scenario.Service(scenario.Seed.Other).ChangeStatusAsync(id, (await service.GetAsync(id)).Summary.Version,
                QuestStatus.Suspended, "The location needs review.");
        await using (var db = database.CreateContext())
        {
            await using var transaction = await db.BeginTransactionAsync();
            await db.LockEventAsync(scenario.Seed.Event.Id);
            (await db.Events.SingleAsync(e => e.Id == scenario.Seed.Event.Id)).Status = EventStatus.Cancelled;
            var lifecycle = new QuestEventLifecycle(new ChangeWriter());
            await lifecycle.CancelForEventAsync(db, scenario.Seed.Event.Id, scenario.Seed.Other.Id,
                "The Event is now cancelled.", scenario.Seed.Quest.EndUtc);
            await lifecycle.CancelForEventAsync(db, scenario.Seed.Event.Id, scenario.Seed.Other.Id,
                "The Event is now cancelled.", scenario.Seed.Quest.EndUtc);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await using var read = database.CreateContext();
        var quest = await read.Quests.SingleAsync(q => q.Id == id);
        Assert.Equal(QuestStatus.Completed, quest.Status);
        Assert.Equal(suspended ? 9 : 8, quest.CalendarRevision);
        Assert.Single(await read.QuestStatusHistory.Where(h => h.QuestId == id && h.Next == QuestStatus.Completed).ToListAsync());
        var envelopes = (await read.OutboxMessages.Where(o => o.AggregateId == id).ToListAsync())
            .Select(o => JsonSerializer.Deserialize<ChangeEnvelope>(o.PayloadJson)!).ToArray();
        Assert.DoesNotContain(envelopes, e => e.Kind is NotificationKind.QuestCancelled or NotificationKind.EventCancelled);
        Assert.Equal(suspended ? 2 : 1, envelopes.Length);
    }
}
