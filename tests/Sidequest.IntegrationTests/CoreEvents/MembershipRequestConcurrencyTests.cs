using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events.Implementation;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.CoreBoundaries;
using Sidequest.IntegrationTests.CoreDelivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

/// <summary>Reproduces competing request-history reads through real Event services in exclusively owned SQL catalogs.</summary>
public sealed class MembershipRequestConcurrencyTests
{
    /// <summary>A failure after SQL saves the request, audit and outbox rolls back all three and releases the reservation.</summary>
    /// <returns>Completion after unchanged durable state and one successful, explicitly initiated request following the failure.</returns>
    [Fact]
    public async Task FailureAfterRequestSave_RollsBackRequestAuditAndOutbox()
    {
        var database = new SqlTestDatabase();
        try
        {
            await database.InitializeAsync();
            var context = new EventTestContext(database);
            var seed = await context.SeedAsync();
            var failure = new FailAfterRequestSave();
            var service = new EventService(new ObservedContextFactory(database, failure),
                new ResourceAccess(StubCurrentUser.For(seed.Other)), context.Directory, context.Quests,
                new ChangeWriter(), context.Clock, context.Options);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestMembershipAsync(seed.Event.Id));
            Assert.Equal("Injected failure after request save.", error.Message);
            Assert.True(failure.Saved);
            await using (var read = database.CreateContext())
            {
                Assert.False(await read.MembershipRequests.AnyAsync(x => x.EventId == seed.Event.Id));
                Assert.False(await read.AuditEntries.AnyAsync(x => x.ResourceId == seed.Event.Id));
                Assert.False(await read.OutboxMessages.AnyAsync(x => x.AggregateId == seed.Event.Id));
                Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
                Assert.Equal(seed.Event.Version, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Version);
            }
            await context.Service(seed.Other).RequestMembershipAsync(seed.Event.Id);
            await using var final = database.CreateContext();
            Assert.Equal(MembershipRequestStatus.Pending, (await final.MembershipRequests.SingleAsync()).Status);
            Assert.Equal("Membership.Requested", (await final.AuditEntries.SingleAsync()).Action);
            Assert.Equal(seed.Event.Id, (await final.OutboxMessages.SingleAsync()).AggregateId);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    /// <summary>Different Events reserve request state before compatible gap reads can deadlock their inserts; repeated requests remain no-ops.</summary>
    /// <param name="terminalHistory">Whether both actors have old withdrawn history while the pending index remains empty.</param>
    /// <returns>Completion after a SQL lock wait for empty history, successful competing commands, and exact atomic request/audit/outbox assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentEvents_ColdRequestRanges_CommitExactlyOnce(bool terminalHistory)
    {
        var database = new SqlTestDatabase();
        try
        {
            await database.InitializeAsync();
            var context = new EventTestContext(database);
            var first = await context.SeedAsync();
            var second = await context.SeedAsync();
            if (terminalHistory)
                await FoundationSeed.PersistAsync(database, History(first), History(second));
            await using (var before = database.CreateContext())
            {
                Assert.False(await before.MembershipRequests.AnyAsync(x => x.Status == MembershipRequestStatus.Pending));
                Assert.Equal(terminalHistory ? 2 : 0, await before.MembershipRequests.CountAsync());
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var firstGate = new MembershipRequestReadGate();
            var secondGate = new MembershipRequestReadGate();
            var firstService = Service(first, firstGate);
            var secondService = Service(second, secondGate);
            var firstRequest = firstService.RequestMembershipAsync(first.Event.Id, deadline.Token);
            Task secondRequest = Task.CompletedTask;
            Task blocked = Task.CompletedTask;
            try
            {
                await firstGate.Read.Task.WaitAsync(deadline.Token);
                var holder = await firstGate.Started.Task;
                secondRequest = secondService.RequestMembershipAsync(second.Event.Id, deadline.Token);
                var waiter = await secondGate.Started.Task.WaitAsync(deadline.Token);
                blocked = SqlBoundaryCoordinator.WaitForBlockAsync(database, waiter, holder, observation.Token);
                // On the broken implementation both shared reads finish. Release them together to
                // reproduce the actual insert deadlock rather than merely failing a timing assertion.
                var observed = await Task.WhenAny(blocked, secondGate.Read.Task).WaitAsync(deadline.Token);
                if (observed == blocked)
                    await blocked;
                firstGate.Release.TrySetResult();
                secondGate.Release.TrySetResult();
                await Task.WhenAll(firstRequest, secondRequest);
                if (!terminalHistory)
                    Assert.Same(blocked, observed);
                await Task.WhenAll(
                    firstService.RequestMembershipAsync(first.Event.Id, deadline.Token),
                    firstService.RequestMembershipAsync(first.Event.Id, deadline.Token),
                    secondService.RequestMembershipAsync(second.Event.Id, deadline.Token),
                    secondService.RequestMembershipAsync(second.Event.Id, deadline.Token));
            }
            finally
            {
                firstGate.Release.TrySetResult();
                secondGate.Release.TrySetResult();
                deadline.Cancel();
                observation.Cancel();
                await Record.ExceptionAsync(() => Task.WhenAll(firstRequest, secondRequest, blocked));
            }
            await using var read = database.CreateContext();
            foreach (var seed in new[] { first, second })
            {
                var requests = await read.MembershipRequests.Where(x => x.EventId == seed.Event.Id).ToListAsync();
                Assert.Equal(terminalHistory ? 2 : 1, requests.Count);
                var pending = Assert.Single(requests, x => x.Status == MembershipRequestStatus.Pending);
                Assert.Equal(seed.Other.Id, pending.UserId);
                Assert.Equal(FoundationSeed.Now, pending.CreatedUtc);
                var audit = Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == seed.Event.Id).ToListAsync());
                Assert.Equal("Membership.Requested", audit.Action);
                Assert.Equal(seed.Other.Id, audit.ActorId);
                var message = Assert.Single(await read.OutboxMessages.Where(x => x.AggregateId == seed.Event.Id).ToListAsync());
                var envelope = JsonSerializer.Deserialize<ChangeEnvelope>(message.PayloadJson)!;
                Assert.Equal(NotificationKind.MembershipRequested, envelope.Kind);
                Assert.Equal(seed.Other.Id, envelope.ActorId);
                Assert.Equal([seed.User.Id], envelope.RecipientIds);
                Assert.Equal(audit.CorrelationId, envelope.ChangeId.ToString("N"));
                Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
            }

            EventService Service(FoundationSeed seed, MembershipRequestReadGate gate) =>
                new(new ObservedContextFactory(database, gate), new ResourceAccess(StubCurrentUser.For(seed.Other)),
                    context.Directory, context.Quests, new ChangeWriter(), context.Clock, context.Options);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static EventMembershipRequest History(FoundationSeed seed) => new()
    {
        EventId = seed.Event.Id, UserId = seed.Other.Id, Status = MembershipRequestStatus.Withdrawn,
        CreatedUtc = FoundationSeed.Now.AddHours(-2), DecidedUtc = FoundationSeed.Now.AddHours(-2),
        DecidedById = seed.Other.Id
    };

    private sealed class FailAfterRequestSave : SaveChangesInterceptor
    {
        internal bool Saved { get; private set; }

        /// <inheritdoc />
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries().ToArray();
            if (entries.Any(x => x.Entity is EventMembershipRequest))
            {
                Assert.Equal(3, result);
                Assert.Single(entries, x => x.Entity is EventMembershipRequest && x.State == EntityState.Unchanged);
                Assert.Single(entries, x => x.Entity is AuditEntry && x.State == EntityState.Unchanged);
                Assert.Single(entries, x => x.Entity is OutboxMessage && x.State == EntityState.Unchanged);
                Saved = true;
                throw new InvalidOperationException("Injected failure after request save.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
