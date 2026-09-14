using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

/// <summary>Runs the real completion producer/consumer against persisted work without assuming worker lease ownership.</summary>
/// <param name="database">Existing isolated migrated-SQL fixture.</param>
public sealed class QuestCompletionHandlerTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Completion uses current time and the exact scheduled end; replay creates no duplicate history or calendar withdrawal.</summary>
    /// <param name="position">Before, at, or stale scheduling boundary.</param>
    /// <returns>Completion after exact state/history/lease and calendar assertions.</returns>
    [Theory]
    [InlineData("before")]
    [InlineData("at")]
    [InlineData("stale")]
    public async Task Completion_ChecksDueAndCurrentEnd_AndReplaysWithoutCalendarCancellation(string position)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        var id = await service.CreateAsync(scenario.Seed.Event.Id, scenario.Input());
        await service.ChangeStatusAsync(id, (await service.GetAsync(id)).Summary.Version, QuestStatus.Active, "");
        await service.ParticipateAsync(id, ParticipationCommand.Join);
        ScheduledWork work;
        await using (var db = database.CreateContext())
        {
            work = await db.ScheduledWork.SingleAsync(w => w.QuestId == id);
            work.Status = WorkStatus.Processing;
            work.LeaseId = Guid.NewGuid();
            work.LeaseUntilUtc = scenario.Clock.Now.AddMinutes(1);
            await db.SaveChangesAsync();
        }
        var payload = JsonSerializer.Deserialize<QuestCompletionPayload>(work.PayloadJson)!;
        Assert.Equal(id, payload.QuestId);
        if (position == "stale")
            await service.EditAsync(id, (await service.GetAsync(id)).Summary.Version,
                scenario.Input() with { EndLocal = scenario.Input().EndLocal.AddHours(1) });
        scenario.Clock.Now = payload.EndUtc.AddTicks(position == "before" ? -1 : 0);
        var handler = new QuestCompletionHandler(new QuestTestFactory(database), new ChangeWriter(), scenario.Clock, scenario.Reconciler);
        Assert.Equal(WorkTypes.QuestCompletion, handler.WorkType);
        await ExecuteTwiceAsync(handler, work.Id, position == "before");
        await using var read = database.CreateContext();
        var quest = await read.Quests.SingleAsync(q => q.Id == id);
        Assert.Equal(position == "at" ? QuestStatus.Completed : QuestStatus.Active, quest.Status);
        Assert.Equal(position == "at" ? 1 : 0, await read.QuestStatusHistory.CountAsync(h => h.QuestId == id && h.Next == QuestStatus.Completed));
        var persistedWork = await read.ScheduledWork.SingleAsync(w => w.Id == work.Id);
        Assert.Equal(WorkStatus.Processing, persistedWork.Status);
        Assert.Equal(work.LeaseId, persistedWork.LeaseId);
        Assert.Equal(position == "stale" ? 2 : 1, quest.CalendarRevision);
        var envelopes = (await read.OutboxMessages.Where(x => x.AggregateId == id).ToListAsync())
            .Select(x => JsonSerializer.Deserialize<ChangeEnvelope>(x.PayloadJson)!).ToArray();
        Assert.DoesNotContain(envelopes, x => x.Kind is NotificationKind.QuestCancelled or NotificationKind.Left);
        Assert.Equal(ParticipationStatus.Joined, (await read.Participations.SingleAsync(p => p.QuestId == id)).Status);
    }

    /// <summary>Retry and administrative replay instants do not replace the captured Quest end or let the handler alter dispatcher-owned work state.</summary>
    /// <param name="position">A due retry, a due retry with a superseded Quest end, or administrative replay before the actual end.</param>
    /// <returns>Completion after durable lifecycle, exact end guards, repeat safety, unchanged delivery and worker-state assertions.</returns>
    [Theory]
    [InlineData("retry")]
    [InlineData("stale")]
    [InlineData("early-replay")]
    public async Task Completion_RetryDueUtc_PreservesEndGuardsAndWorkerState(string position)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var service = scenario.Service();
        var id = await service.CreateAsync(scenario.Seed.Event.Id, scenario.Input());
        await service.ChangeStatusAsync(id, (await service.GetAsync(id)).Summary.Version, QuestStatus.Active, "");
        await service.ParticipateAsync(id, ParticipationCommand.Join);
        ScheduledWork work;
        await using (var db = database.CreateContext())
            work = await db.ScheduledWork.AsNoTracking().SingleAsync(w => w.QuestId == id);
        var payload = Assert.IsType<QuestCompletionPayload>(JsonSerializer.Deserialize<QuestCompletionPayload>(work.PayloadJson));
        if (position == "stale")
            await service.EditAsync(id, (await service.GetAsync(id)).Summary.Version,
                scenario.Input() with { EndLocal = scenario.Input().EndLocal.AddHours(1) });
        scenario.Clock.Now = position == "early-replay" ? payload.EndUtc.AddTicks(-1) : payload.EndUtc.AddHours(2);
        var leaseId = Guid.NewGuid();
        var leaseUntil = scenario.Clock.Now.AddMinutes(1);
        long calendarRevision;
        Guid[] outboxIds;
        await using (var db = database.CreateContext())
        {
            var queued = await db.ScheduledWork.SingleAsync(w => w.Id == work.Id);
            queued.DueUtc = scenario.Clock.Now;
            queued.Status = WorkStatus.Processing;
            queued.Attempts = 2;
            queued.LeaseId = leaseId;
            queued.LeaseUntilUtc = leaseUntil;
            queued.LastError = "Synthetic previous execution failure.";
            await db.SaveChangesAsync();
            calendarRevision = (await db.Quests.SingleAsync(q => q.Id == id)).CalendarRevision;
            outboxIds = await db.OutboxMessages.Where(o => o.AggregateId == id).OrderBy(o => o.Id).Select(o => o.Id).ToArrayAsync();
        }

        var handler = new QuestCompletionHandler(new QuestTestFactory(database), new ChangeWriter(), scenario.Clock, scenario.Reconciler);
        await ExecuteTwiceAsync(handler, work.Id, position == "early-replay");

        await using var read = database.CreateContext();
        var quest = await read.Quests.SingleAsync(q => q.Id == id);
        Assert.Equal(position == "retry" ? QuestStatus.Completed : QuestStatus.Active, quest.Status);
        Assert.Equal(payload.EndUtc.AddHours(position == "stale" ? 1 : 0), quest.EndUtc);
        Assert.Equal(position == "retry" ? 1 : 0, await read.QuestStatusHistory.CountAsync(h => h.QuestId == id && h.Next == QuestStatus.Completed));
        Assert.Equal(calendarRevision, quest.CalendarRevision);
        Assert.Equal(outboxIds, await read.OutboxMessages.Where(o => o.AggregateId == id).OrderBy(o => o.Id).Select(o => o.Id).ToArrayAsync());
        Assert.Equal(ParticipationStatus.Joined, (await read.Participations.SingleAsync(p => p.QuestId == id)).Status);
        var persisted = await read.ScheduledWork.SingleAsync(w => w.Id == work.Id);
        Assert.NotEqual(payload.EndUtc, persisted.DueUtc);
        Assert.Equal(scenario.Clock.Now, persisted.DueUtc);
        Assert.Equal(work.PayloadJson, persisted.PayloadJson);
        Assert.Equal(WorkStatus.Processing, persisted.Status);
        Assert.Equal(2, persisted.Attempts);
        Assert.Equal(leaseId, persisted.LeaseId);
        Assert.Equal(leaseUntil, persisted.LeaseUntilUtc);
        Assert.Equal("Synthetic previous execution failure.", persisted.LastError);
    }

    /// <summary>Invalid type/reference/time payloads fail explicitly instead of being reported as successfully completed.</summary>
    /// <param name="malformation">Malformed JSON, empty identity, default end, mismatched reference, or wrong work discriminator.</param>
    /// <returns>Completion after validation failure and unchanged aggregate/work assertions.</returns>
    [Theory]
    [InlineData("json")]
    [InlineData("empty")]
    [InlineData("end")]
    [InlineData("reference")]
    [InlineData("type")]
    public async Task Completion_RejectsMalformedOrMismatchedWork(string malformation)
    {
        var scenario = await QuestScenario.CreateAsync(database);
        var work = new ScheduledWork
        {
            Type = malformation == "type" ? WorkTypes.Reminder : WorkTypes.QuestCompletion,
            DeduplicationKey = Guid.NewGuid().ToString("N"),
            QuestId = scenario.Seed.Quest.Id,
            DueUtc = scenario.Seed.Quest.EndUtc,
            PayloadJson = malformation switch
            {
                "json" => "{",
                "empty" => "{}",
                "end" => JsonSerializer.Serialize(new QuestCompletionPayload(scenario.Seed.Quest.Id, default)),
                "reference" => JsonSerializer.Serialize(new QuestCompletionPayload(Guid.NewGuid(), scenario.Seed.Quest.EndUtc)),
                _ => JsonSerializer.Serialize(new QuestCompletionPayload(scenario.Seed.Quest.Id, scenario.Seed.Quest.EndUtc))
            }
        };
        await FoundationSeed.PersistAsync(database, work);
        scenario.Clock.Now = work.DueUtc;
        var handler = new QuestCompletionHandler(new QuestTestFactory(database), new ChangeWriter(), scenario.Clock, scenario.Reconciler);
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() => handler.ExecuteAsync(work.Id, default))).Code);
        await using var read = database.CreateContext();
        Assert.Equal(QuestStatus.Active, (await read.Quests.SingleAsync(q => q.Id == scenario.Seed.Quest.Id)).Status);
        Assert.Equal(WorkStatus.Pending, (await read.ScheduledWork.SingleAsync(w => w.Id == work.Id)).Status);
        Assert.Empty(await read.QuestStatusHistory.Where(h => h.QuestId == scenario.Seed.Quest.Id).ToListAsync());
    }

    private static async Task ExecuteTwiceAsync(QuestCompletionHandler handler, Guid workId, bool premature)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (premature)
            {
                var failure = await Assert.ThrowsAsync<DomainException>(() => handler.ExecuteAsync(workId, default));
                Assert.Equal(ErrorCode.Conflict, failure.Code);
                Assert.Equal("Quest completion is not due yet.", failure.Message);
            }
            else
            {
                await handler.ExecuteAsync(workId, default);
            }
        }
    }
}
