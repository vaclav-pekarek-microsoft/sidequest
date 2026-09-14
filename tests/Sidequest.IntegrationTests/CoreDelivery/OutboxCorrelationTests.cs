using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Delivery;

namespace Sidequest.IntegrationTests.CoreDelivery;

/// <summary>Tests persisted outbox identity boundaries against real ChangeWriter output and isolated SQL mutations.</summary>
public sealed class OutboxCorrelationTests
{
    /// <summary>Mismatched logical or aggregate identities fail permanently before effects, including corruption after the preflight read.</summary>
    /// <param name="questChange">Whether the envelope identifies a Quest rather than its Event alone.</param>
    /// <param name="changeIdMismatch">Whether to corrupt the envelope ChangeId rather than the row AggregateId.</param>
    /// <param name="afterPreflight">Whether the mismatch is introduced immediately before the Event lock instead of before dispatch.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task MismatchedOutboxCorrelationDeadLettersWithoutEffects(bool questChange, bool changeIdMismatch, bool afterPreflight)
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var change = Change(s, questChange);
        await s.AddChangeAsync(change);
        var mutations = 0;
        async Task CorruptAsync(CancellationToken cancellationToken)
        {
            await using var update = s.Database.CreateContext();
            var row = await update.OutboxMessages.SingleAsync(x => x.Id == change.ChangeId, cancellationToken);
            if (changeIdMismatch)
                row.PayloadJson = JsonSerializer.Serialize(change with { ChangeId = Guid.NewGuid() });
            else
                row.AggregateId = questChange ? s.Seed.Event.Id : s.Seed.Quest.Id;
            await update.SaveChangesAsync(cancellationToken);
            mutations++;
        }
        ISidequestDbContextFactory factory = s.Factory;
        if (afterPreflight)
            factory = new ObservedContextFactory(s.Database, new BeforeEventLockInterceptor(CorruptAsync));
        else
            await CorruptAsync(CancellationToken.None);
        var handler = new ChangeOutboxHandler(factory, s.Policy, new(s.Clock), s.Execution, s.Clock);
        var gateway = new RecordingEmailGateway();
        var dispatcher = new DeliveryDispatcher(s.Factory, s.Policy, s.Renderer, gateway, s.Clock, s.Options);
        var runner = new DurableWorkRunner(s.Queue, [handler, s.Reminders], dispatcher,
            s.Execution, s.Clock, s.Options, NullLogger<DurableWorkRunner>.Instance);
        Assert.True(await runner.RunOnceAsync("outbox"));
        Assert.Equal(1, mutations);
        await using var read = s.Database.CreateContext();
        var failed = await read.OutboxMessages.SingleAsync();
        Assert.Equal(change.ChangeId, failed.Id);
        Assert.Equal(WorkStatus.DeadLetter, failed.Status);
        Assert.Equal(1, failed.Attempts);
        Assert.Null(failed.LeaseId);
        Assert.Contains("Permanent", failed.LastError!);
        Assert.Empty(await read.Notifications.ToListAsync());
        Assert.Empty(await read.NotificationDeliveries.ToListAsync());
        Assert.Empty(await read.CalendarDeliveryStates.ToListAsync());
        Assert.Empty(await read.ScheduledWork.ToListAsync());
        Assert.Equal(7, (await read.Quests.SingleAsync()).CalendarRevision);
        Assert.Empty(gateway.Messages);
    }

    /// <summary>Unmodified parent ChangeWriter envelopes complete under their original identity and use Quest-or-Event aggregate selection.</summary>
    /// <param name="questChange">Whether calendar and reminder effects accompany a Quest change.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidWriterCorrelationsPreserveEventAndQuestEffects(bool questChange)
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var change = Change(s, questChange);
        await s.AddChangeAsync(change);
        await s.ProcessChangeAsync();
        await using var read = s.Database.CreateContext();
        var completed = await read.OutboxMessages.SingleAsync();
        Assert.Equal(change.ChangeId, completed.Id);
        Assert.Equal(questChange ? s.Seed.Quest.Id : s.Seed.Event.Id, completed.AggregateId);
        Assert.Equal(WorkStatus.Completed, completed.Status);
        Assert.Equal(change.ChangeId, (await read.Notifications.SingleAsync()).SourceChangeId);
        Assert.Equal($"change:{change.ChangeId:N}:{s.Seed.User.Id:N}:email",
            (await read.NotificationDeliveries.SingleAsync()).DeduplicationKey);
        Assert.Equal(questChange ? 1 : 0, await read.CalendarDeliveryStates.CountAsync());
        Assert.Equal(questChange ? 1 : 0, await read.ScheduledWork.CountAsync());
    }

    private static ChangeEnvelope Change(DeliveryScenario s, bool questChange) =>
        new(Guid.NewGuid(), questChange ? NotificationKind.Joined : NotificationKind.MembershipAdded,
            s.Seed.Event.Id, questChange ? s.Seed.Quest.Id : null, s.Seed.User.Id, [s.Seed.User.Id], s.Clock.Now,
            questChange ? 7 : 0, PreviousAttendeeIds: [], CalendarChanged: questChange, AffectedUserIds: [s.Seed.User.Id]);
}
