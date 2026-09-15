using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Delivery;
using Sidequest.IntegrationTests.CoreDelivery;
using Sidequest.IntegrationTests.CoreQuests;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.SecondaryMedia;

/// <summary>Exercises actual durable-runner routing, acknowledgement and failure handling for real staged Media cleanup.</summary>
[Collection("SecondaryMedia")]
public sealed class MediaRunnerTests
{
    /// <summary>Runs Media cleanup through RunOnceAsync rather than manually invoking the handler or queue acknowledgement.</summary>
    /// <param name="outcome">Successful deletion, transient outage or permanent configuration failure.</param>
    /// <param name="expectedStatus">The independently specified persisted first-attempt outcome.</param>
    /// <returns>A task completing after actual handler execution, durable state, released scope and retry assertions.</returns>
    [Theory]
    [InlineData("success", WorkStatus.Completed)]
    [InlineData("transient", WorkStatus.Pending)]
    [InlineData("permanent", WorkStatus.DeadLetter)]
    public async Task RegisteredCleanup_RunsThroughWorkerAndPreservesRetryPolicy(string outcome, WorkStatus expectedStatus)
    {
        await using var database = new SqlTestDatabase();
        await database.InitializeAsync();
        var scenario = await MediaScenario.CreateAsync(database, draft: true);
        using var invalid = new MemoryStream([1, 2, 3]);
        var version = await scenario.VersionAsync();
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service().UploadCoverAsync(scenario.Seed.Quest.Id, version, invalid))).Code);
        ScheduledWork original;
        await using (var read = database.CreateContext())
            original = await read.ScheduledWork.AsNoTracking().SingleAsync();
        scenario.Clock.Now = original.DueUtc;
        var factory = new QuestTestFactory(database);
        var options = new DurableWorkOptions();
        var queue = new SqlWorkQueue(factory, scenario.Clock, options);
        var execution = new WorkExecutionContext();
        var gateway = new RecordingEmailGateway();
        var delivery = new DeliveryDispatcher(factory, new RecipientPolicy(),
            new RecipientCalendarRenderer(new EmailDeliveryOptions { SenderAddress = "organizer@example.invalid" }),
            gateway, scenario.Clock, options);
        var runner = new DurableWorkRunner(queue, [scenario.Cleanup], delivery, execution, scenario.Clock, options,
            NullLogger<DurableWorkRunner>.Instance);
        WorkLease? observedLease = null;
        scenario.Storage.BeforeDelete = () =>
        {
            observedLease = execution.Lease;
            if (outcome != "success")
                throw new DomainException(ErrorCode.DependencyUnavailable, "provider-detail-must-not-persist",
                    isPermanentDependencyFailure: outcome == "permanent");
            return Task.CompletedTask;
        };

        Assert.True(await runner.RunOnceAsync("scheduled"));

        var claimed = Assert.IsType<WorkLease>(observedLease);
        Assert.Equal(original.Id, claimed.Id);
        Assert.Equal(original.Type, claimed.Type);
        Assert.Equal(1, claimed.Attempts);
        Assert.Equal(1, scenario.Storage.Deletes);
        Assert.Null(execution.Lease);
        Assert.Empty(gateway.Messages);
        ScheduledWork first;
        await using (var read = database.CreateContext())
        {
            first = await read.ScheduledWork.AsNoTracking().SingleAsync();
            Assert.Equal(expectedStatus, first.Status);
            Assert.Equal(1, first.Attempts);
            Assert.Equal(original.PayloadJson, first.PayloadJson);
            Assert.Equal(original.DeduplicationKey, first.DeduplicationKey);
            Assert.Null(first.LeaseId);
            Assert.Null(first.LeaseUntilUtc);
            Assert.DoesNotContain("provider-detail", first.LastError ?? "", StringComparison.Ordinal);
            Assert.Equal(outcome == "success" ? 0 : 1, await read.MediaAssets.CountAsync());
            Assert.Equal(outcome == "success" ? 1 : 0, await read.AuditEntries.CountAsync(x => x.Action == "MediaCleanupCompleted"));
        }

        scenario.Storage.BeforeDelete = null;
        scenario.Clock.Now = first.DueUtc.AddMinutes(1);
        Assert.Equal(outcome == "transient", await runner.RunOnceAsync("scheduled"));
        Assert.Null(execution.Lease);
        await using var final = database.CreateContext();
        var stored = await final.ScheduledWork.SingleAsync();
        Assert.Equal(outcome == "permanent" ? WorkStatus.DeadLetter : WorkStatus.Completed, stored.Status);
        Assert.Equal(outcome == "transient" ? 2 : 1, stored.Attempts);
        Assert.Equal(outcome == "transient" ? 2 : 1, scenario.Storage.Deletes);
        Assert.Equal(original.PayloadJson, stored.PayloadJson);
        Assert.Equal(outcome == "permanent" ? 1 : 0, await final.MediaAssets.CountAsync());
        Assert.Equal(outcome == "permanent" ? 0 : 1, await final.AuditEntries.CountAsync(x => x.Action == "MediaCleanupCompleted"));
        Assert.Empty(gateway.Messages);
    }
}
