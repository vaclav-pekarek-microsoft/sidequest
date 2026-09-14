using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Application.Security;
using Sidequest.Domain.Model;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Delivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreDelivery;

/// <summary>Verifies real SQL command ordering and same-Event serialization through the approved shared lock port.</summary>
public sealed class EventLockIntegrationTests
{
    /// <summary>Preference, Event override, read-state and calendar transactions acquire the Event lock before actor or child queries.</summary>
    [Fact]
    public async Task RecipientOperationsAcquireEventLockBeforeTransactionalAuthorization()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var observer = new SqlCommandOrderObserver();
        var factory = new ObservedContextFactory(s.Database, observer);
        var service = new NotificationService(factory, new ResourceAccess(StubCurrentUser.For(s.Seed.User)),
            s.Policy, new(s.Clock), s.Renderer, s.Clock);
        await FoundationSeed.PersistAsync(s.Database, new Notification
        {
            UserId = s.Seed.User.Id, SourceChangeId = Guid.NewGuid(), EventId = s.Seed.Event.Id,
            QuestId = s.Seed.Quest.Id, Kind = NotificationKind.QuestUpdated, Summary = "Safe test notification"
        });
        await service.SavePreferencesAsync(new(false, true, true, 1, null));
        await service.SetEventNewQuestEmailAsync(s.Seed.Event.Id, true);
        await service.MarkReadAsync(null);
        Assert.Contains("METHOD:REQUEST", await service.DownloadCalendarAsync(s.Seed.Quest.Id));
        var transactions = observer.Commands.GroupBy(x => x.TransactionId).ToArray();
        Assert.Equal(4, transactions.Length);
        foreach (var transaction in transactions)
        {
            Assert.Contains("UPDLOCK, HOLDLOCK", transaction.First().Sql);
            Assert.Contains(transaction, command => command.Sql.Contains("[Users]", StringComparison.Ordinal));
        }
    }

    /// <summary>Outbox, reminder, submission preparation and acceptance bookkeeping all lock the parent before domain reads.</summary>
    [Fact]
    public async Task WorkerTransactionsAcquireEventLockBeforeDomainReads()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        var observer = new SqlCommandOrderObserver();
        var factory = new ObservedContextFactory(s.Database, observer);
        var changeLease = (await s.Queue.ClaimAsync("outbox"))!;
        s.Execution.Lease = changeLease;
        await new ChangeOutboxHandler(factory, s.Policy, new(s.Clock), s.Execution, s.Clock)
            .ExecuteAsync(changeLease.Id, CancellationToken.None);
        var reminderLease = (await s.Queue.ClaimAsync("scheduled"))!;
        s.Execution.Lease = reminderLease;
        await new ReminderWorkHandler(factory, s.Policy, s.Execution, s.Options, s.Clock)
            .ExecuteAsync(reminderLease.Id, CancellationToken.None);
        await s.Queue.CompleteAsync(reminderLease);
        var gateway = new RecordingEmailGateway();
        var dispatcher = new DeliveryDispatcher(factory, s.Policy, s.Renderer, gateway, s.Clock, s.Options);
        for (var index = 0; index < 2; index++)
        {
            var lease = (await s.Queue.ClaimAsync("delivery"))!;
            await dispatcher.ExecuteAsync(lease, CancellationToken.None);
            await s.Queue.CompleteAsync(lease);
        }
        var transactions = observer.Commands.GroupBy(x => x.TransactionId).ToArray();
        Assert.Equal(6, transactions.Length);
        Assert.All(transactions, transaction => Assert.Contains("UPDLOCK, HOLDLOCK", transaction.First().Sql));
        Assert.Equal(2, gateway.Messages.Count);
    }

    /// <summary>Competing preference changes serialize on the Event and retain one preference and one logical reminder without a deadlock.</summary>
    [Fact]
    public async Task ConcurrentPreferenceChangesSerializeWithoutDuplicateSchedules()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var first = new NotificationService(s.Factory, new ResourceAccess(StubCurrentUser.For(s.Seed.User)),
            s.Policy, new(s.Clock), s.Renderer, s.Clock);
        var second = new NotificationService(s.Factory, new ResourceAccess(StubCurrentUser.For(s.Seed.User)),
            s.Policy, new(s.Clock), s.Renderer, s.Clock);
        await Task.WhenAll(first.SavePreferencesAsync(new(false, true, true, .5m, null)),
            second.SavePreferencesAsync(new(true, false, true, 1m, null)));
        await using var read = s.Database.CreateContext();
        Assert.Single(await read.NotificationPreferences.ToListAsync());
        Assert.Single(await read.ScheduledWork.ToListAsync());
        Assert.Equal(2, await read.AuditEntries.CountAsync());
    }
}
