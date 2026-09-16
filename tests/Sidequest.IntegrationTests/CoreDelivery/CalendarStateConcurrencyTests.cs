using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Delivery;
using Sidequest.IntegrationTests.CoreBoundaries;
using Sidequest.IntegrationTests.CoreQuests;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreDelivery;

/// <summary>Exercises independent leased outbox handlers against empty and retained calendar state with their complete durable effects.</summary>
public sealed class CalendarStateConcurrencyTests
{
    /// <summary>Concurrent valid Join changes commit exact calendar, inbox, transport and optional reminder effects once under separate Event locks.</summary>
    /// <param name="retained">Whether both recipients have an older retained calendar intent rather than an entirely empty calendar table.</param>
    /// <param name="future">Whether future starts also require inserting reminder schedules.</param>
    /// <param name="separateEffectRanges">Whether retained boundaries separate notification and scheduling gaps to expose downstream calendar conflicts.</param>
    /// <returns>Completion after controlled SQL overlap, exact state/sequence/deduplication assertions and repeat-safe completed lease handling.</returns>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task DifferentEvents_StageCalendarAndCompleteLeasesExactlyOnce(bool retained, bool future, bool separateEffectRanges)
    {
        var database = new SqlTestDatabase();
        try
        {
            await database.InitializeAsync();
            var scenarios = new[] { await QuestScenario.CreateAsync(database), await QuestScenario.CreateAsync(database) };
            var changes = new Dictionary<Guid, Guid>();
            var oldStates = new Dictionary<Guid, CalendarDeliveryState>();
            foreach (var scenario in scenarios)
            {
                await using (var setup = database.CreateContext())
                {
                    (await setup.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id)).StartUtc =
                        scenario.Clock.GetUtcNow().AddHours(future ? 2 : 0);
                    if (retained)
                    {
                        var state = new CalendarDeliveryState
                        {
                            QuestId = scenario.Seed.Quest.Id, UserId = scenario.Seed.User.Id,
                            IntendedSequence = 6, SentSequence = 5, IntendedMethod = "REQUEST",
                            MayHaveBeenDelivered = true, ChangedUtc = scenario.Clock.GetUtcNow().AddHours(-1),
                            Payload = "{}"
                        };
                        setup.CalendarDeliveryStates.Add(state);
                        oldStates.Add(state.QuestId, state);
                    }
                    await setup.SaveChangesAsync();
                }
                await ParticipationTestServices.Service(scenario, scenario.Seed.User)
                    .ParticipateAsync(scenario.Seed.Quest.Id, ParticipationCommand.Join);
                await using var read = database.CreateContext();
                changes.Add(scenario.Seed.Quest.Id, (await read.OutboxMessages.SingleAsync(x => x.AggregateId == scenario.Seed.Quest.Id)).Id);
            }
            if (separateEffectRanges)
            {
                await using var setup = database.CreateContext();
                foreach (var scenario in scenarios)
                {
                    // Bracket Joined within each exact change/user key so inbox gap contention cannot hide calendar or reminder conflicts.
                    foreach (var kind in new[] { NotificationKind.AccessRemoved, NotificationKind.Left })
                    {
                        setup.Notifications.Add(new Notification
                        {
                            SourceChangeId = changes[scenario.Seed.Quest.Id], UserId = scenario.Seed.User.Id,
                            Kind = kind, EventId = scenario.Seed.Event.Id, QuestId = scenario.Seed.Quest.Id,
                            CreatedUtc = scenario.Clock.GetUtcNow(), Summary = "Retained range boundary"
                        });
                    }
                    foreach (var boundary in new[] { "00000000000000000000000000000000", "ffffffffffffffffffffffffffffffff" })
                    {
                        setup.ScheduledWork.Add(new ScheduledWork
                        {
                            Type = WorkTypes.Reminder, QuestId = scenario.Seed.Quest.Id, UserId = Guid.ParseExact(boundary, "N"),
                            DeduplicationKey = $"reminder:{scenario.Seed.Quest.Id:N}:{boundary}:0",
                            Status = WorkStatus.Completed
                        });
                    }
                }
                await setup.SaveChangesAsync();
            }
            var queue = new SqlWorkQueue(new QuestTestFactory(database), scenarios[0].Clock, new DurableWorkOptions());
            var leases = new[] { await queue.ClaimAsync("outbox"), await queue.ClaimAsync("outbox") };
            Assert.All(leases, Assert.NotNull);
            var firstLease = leases.Single(x => x!.Id == changes[scenarios[0].Seed.Quest.Id])!;
            var secondLease = leases.Single(x => x!.Id == changes[scenarios[1].Seed.Quest.Id])!;
            var firstGate = new CrossResourceSaveGate();
            var secondGate = new CrossResourceSaveGate();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var firstHandler = Handler(scenarios[0], firstLease, firstGate);
            var secondHandler = Handler(scenarios[1], secondLease, secondGate);
            var first = firstHandler.ExecuteAsync(firstLease.Id, deadline.Token);
            Task second = Task.CompletedTask;
            Task blocked = Task.CompletedTask;
            try
            {
                await firstGate.Ready.Task.WaitAsync(deadline.Token);
                second = secondHandler.ExecuteAsync(secondLease.Id, deadline.Token);
                var waiter = await secondGate.Session.Task.WaitAsync(deadline.Token);
                blocked = SqlBoundaryCoordinator.WaitForBlockAsync(database, waiter, await firstGate.Session.Task, observation.Token);
                var observed = await Task.WhenAny(blocked, secondGate.Ready.Task).WaitAsync(deadline.Token);
                if (observed == blocked)
                    await blocked;
                firstGate.Release.TrySetResult();
                secondGate.Release.TrySetResult();
                await Task.WhenAll(first, second);
            }
            finally
            {
                firstGate.Release.TrySetResult();
                secondGate.Release.TrySetResult();
                deadline.Cancel();
                observation.Cancel();
                await Record.ExceptionAsync(() => Task.WhenAll(first, second, blocked));
            }
            await firstHandler.ExecuteAsync(firstLease.Id, CancellationToken.None);
            await secondHandler.ExecuteAsync(secondLease.Id, CancellationToken.None);
            await using var final = database.CreateContext();
            Assert.Equal(2, await final.CalendarDeliveryStates.CountAsync());
            Assert.Equal(separateEffectRanges ? 6 : 2, await final.Notifications.CountAsync());
            Assert.Equal(2, await final.NotificationDeliveries.CountAsync());
            Assert.Equal((future ? 2 : 0) + (separateEffectRanges ? 4 : 0), await final.ScheduledWork.CountAsync());
            Assert.Equal(separateEffectRanges ? 4 : 0, await final.ScheduledWork.CountAsync(x => x.Status == WorkStatus.Completed));
            foreach (var scenario in scenarios)
            {
                var quest = await final.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id);
                var state = await final.CalendarDeliveryStates.SingleAsync(x => x.QuestId == quest.Id);
                Assert.Equal(scenario.Seed.User.Id, state.UserId);
                Assert.Equal(quest.CalendarRevision, state.IntendedSequence);
                Assert.Equal("REQUEST", state.IntendedMethod);
                Assert.Equal(retained ? 5L : null, state.SentSequence);
                Assert.Equal(retained, state.MayHaveBeenDelivered);
                if (retained)
                    Assert.Equal(oldStates[quest.Id].Id, state.Id);
                var snapshot = JsonSerializer.Deserialize<CalendarSnapshot>(state.Payload)!;
                Assert.Equal(quest.Id, snapshot.QuestId);
                Assert.Equal(quest.StartUtc, snapshot.StartUtc);
                Assert.Equal(quest.EndUtc, snapshot.EndUtc);
                Assert.Equal(quest.CalendarRevision, snapshot.Sequence);
                Assert.Equal(scenario.Seed.User.Email, snapshot.Recipient);
                var notification = await final.Notifications.SingleAsync(x => x.QuestId == quest.Id && x.Kind == NotificationKind.Joined);
                Assert.Equal(changes[quest.Id], notification.SourceChangeId);
                Assert.Equal(state.UserId, notification.UserId);
                Assert.Equal(NotificationKind.Joined, notification.Kind);
                var transport = await final.NotificationDeliveries.SingleAsync(x => x.NotificationId == notification.Id);
                Assert.Equal($"change:{changes[quest.Id]:N}:{state.UserId:N}:email", transport.DeduplicationKey);
                Assert.Equal(WorkStatus.Pending, transport.Status);
                if (future)
                {
                    var reminder = await final.ScheduledWork.SingleAsync(x => x.QuestId == quest.Id && x.UserId == state.UserId);
                    Assert.Equal(state.UserId, reminder.UserId);
                    Assert.Equal(WorkTypes.Reminder, reminder.Type);
                    Assert.Equal($"reminder:{quest.Id:N}:{state.UserId:N}:{quest.StartRevision}", reminder.DeduplicationKey);
                    Assert.Equal(WorkStatus.Pending, reminder.Status);
                    Assert.Equal(quest.StartUtc.AddHours(-1), reminder.DueUtc);
                    var reminderPayload = ReminderPayload.Parse(reminder.PayloadJson);
                    Assert.Equal(quest.StartRevision, reminderPayload.StartRevision);
                    Assert.Equal(quest.StartUtc, reminderPayload.StartUtc);
                    Assert.Equal(reminder.DueUtc, reminderPayload.ScheduledUtc);
                    Assert.Equal(state.UserId, reminderPayload.UserId);
                    Assert.Equal(quest.Id, reminderPayload.QuestId);
                }
                var payload = JsonSerializer.Deserialize<DeliveryPayload>(transport.PayloadJson)!;
                Assert.Equal(snapshot, payload.Calendar);
                var outbox = await final.OutboxMessages.SingleAsync(x => x.Id == changes[quest.Id]);
                Assert.Equal(WorkStatus.Completed, outbox.Status);
                Assert.Null(outbox.LeaseId);
                Assert.Null(outbox.LeaseUntilUtc);
                Assert.Equal(1, outbox.Attempts);
            }
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static ChangeOutboxHandler Handler(QuestScenario scenario, WorkLease lease, CrossResourceSaveGate gate) =>
        new(new ObservedContextFactory(scenario.Database, gate, gate.Commands, new ReminderReadProbe(gate)), new RecipientPolicy(),
            new ReminderScheduler(scenario.Clock), new WorkExecutionContext { Lease = lease }, scenario.Clock);

    private sealed class ReminderReadProbe(CrossResourceSaveGate gate) : DbCommandInterceptor
    {
        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[ScheduledWork]", StringComparison.Ordinal))
                gate.Session.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);
            return ValueTask.FromResult(result);
        }
    }
}
