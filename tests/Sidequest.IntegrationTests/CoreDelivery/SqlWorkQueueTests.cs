using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreDelivery;

/// <summary>Real isolated SQL claim, restart, expiry and bounded retry tests against the migrated M1 schema.</summary>
public sealed class SqlWorkQueueTests
{
    /// <summary>Two simultaneous claimers cannot own the same row; an expired lease is recovered and stale completion is rejected.</summary>
    [Fact]
    public async Task ConcurrentClaimsAndExpiredRecoveryFenceOldCompletion()
    {
        await using var scenario = await DeliveryScenario.CreateAsync();
        await scenario.AddChangeAsync();
        var claims = await Task.WhenAll(scenario.Queue.ClaimAsync("outbox"), scenario.Queue.ClaimAsync("outbox"));
        var first = Assert.Single(claims, x => x is not null)!;
        scenario.Clock.Now += scenario.Options.LeaseDuration;
        Assert.False(await scenario.Queue.CompleteAsync(first));
        Assert.False(await scenario.Queue.RenewAsync(first));
        var recovered = await scenario.Queue.ClaimAsync("outbox");
        Assert.NotNull(recovered);
        Assert.Equal(first.Id, recovered.Id);
        Assert.NotEqual(first.Token, recovered.Token);
        Assert.Equal(2, recovered.Attempts);
        Assert.False(await scenario.Queue.CompleteAsync(first));
        Assert.True(await scenario.Queue.CompleteAsync(recovered));
        await using var read = scenario.Database.CreateContext();
        Assert.Equal(WorkStatus.Completed, (await read.OutboxMessages.SingleAsync()).Status);
    }

    /// <summary>Renewal extends only live ownership and keeps the row unavailable to another worker.</summary>
    [Fact]
    public async Task RenewalKeepsLiveClaimExclusive()
    {
        await using var scenario = await DeliveryScenario.CreateAsync();
        await scenario.AddChangeAsync();
        var lease = (await scenario.Queue.ClaimAsync("outbox"))!;
        scenario.Clock.Now += TimeSpan.FromSeconds(60);
        Assert.True(await scenario.Queue.RenewAsync(lease));
        scenario.Clock.Now += TimeSpan.FromSeconds(40);
        Assert.Null(await scenario.Queue.ClaimAsync("outbox"));
        Assert.True(await scenario.Queue.CompleteAsync(lease));
    }

    /// <summary>Eight retryable failures dead-letter with a stable payload; provider Retry-After prevents early reclaiming.</summary>
    [Fact]
    public async Task EightFailuresDeadLetterAndHonorRetryAfter()
    {
        await using var scenario = await DeliveryScenario.CreateAsync();
        await scenario.AddChangeAsync();
        string? original = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var lease = (await scenario.Queue.ClaimAsync("outbox"))!;
            Assert.Equal(attempt, lease.Attempts);
            Assert.True(await scenario.Queue.FailAsync(lease,
                new DeliveryTransportException(TransportOutcome.Retryable, "raw-secret-must-not-persist", TimeSpan.FromMinutes(5))));
            await using var read = scenario.Database.CreateContext();
            var row = await read.OutboxMessages.SingleAsync();
            original ??= row.PayloadJson;
            Assert.Equal(original, row.PayloadJson);
            Assert.DoesNotContain("raw-secret", row.LastError!);
            Assert.Null(row.LeaseId);
            Assert.True(row.DueUtc >= scenario.Clock.Now.AddMinutes(5));
            Assert.Equal(attempt == 8 ? WorkStatus.DeadLetter : WorkStatus.Pending, row.Status);
            Assert.Null(await scenario.Queue.ClaimAsync("outbox"));
            scenario.Clock.Now = row.DueUtc;
        }
        Assert.Null(await scenario.Queue.ClaimAsync("outbox"));
    }

    /// <summary>Permanent errors dead-letter on the first attempt rather than cycling eight times.</summary>
    [Fact]
    public async Task PermanentFailureDeadLettersImmediately()
    {
        await using var scenario = await DeliveryScenario.CreateAsync();
        await FoundationSeed.PersistAsync(scenario.Database, new ScheduledWork
        {
            Type = "unknown.v9", DeduplicationKey = "poison", DueUtc = scenario.Clock.Now
        });
        var lease = (await scenario.Queue.ClaimAsync("scheduled"))!;
        Assert.True(await scenario.Queue.FailAsync(lease, new DeliveryTransportException(TransportOutcome.Permanent, "invalid")));
        await using var read = scenario.Database.CreateContext();
        var row = await read.ScheduledWork.SingleAsync();
        Assert.Equal(WorkStatus.DeadLetter, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.LeaseId);
    }

    /// <summary>A Retry-After beyond the bounded automatic horizon is held for investigation rather than retried before the provider permits it.</summary>
    [Fact]
    public async Task ExcessiveRetryAfterRequiresManualRecoveryRatherThanEarlyRetry()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        var lease = (await s.Queue.ClaimAsync("outbox"))!;
        await s.Queue.FailAsync(lease, new DeliveryTransportException(TransportOutcome.Retryable, "throttled", TimeSpan.FromDays(2)));
        s.Clock.Now += TimeSpan.FromDays(1);
        Assert.Null(await s.Queue.ClaimAsync("outbox"));
        await using var read = s.Database.CreateContext();
        Assert.Equal(WorkStatus.DeadLetter, (await read.OutboxMessages.SingleAsync()).Status);
    }
}
