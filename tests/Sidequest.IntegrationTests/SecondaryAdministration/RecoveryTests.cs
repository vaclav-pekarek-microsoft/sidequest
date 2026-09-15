using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Administration;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.CoreDelivery;

namespace Sidequest.IntegrationTests.SecondaryAdministration;

/// <summary>Verifies narrowly scoped recovery against migrated SQL with external evidence, privacy, atomicity and concurrency guards.</summary>
public sealed class RecoveryTests
{
    /// <summary>Verified departure permits recovery without lifecycle edits or administrator content access, including private and terminal resources.</summary>
    /// <param name="kind">Exact resource namespace under recovery.</param>
    /// <param name="eventStatus">Parent lifecycle state that recovery must preserve.</param>
    /// <param name="questStatus">Private child lifecycle state that recovery must preserve.</param>
    [Theory]
    [InlineData(ResourceKind.Event, EventStatus.Active, QuestStatus.Active)]
    [InlineData(ResourceKind.Quest, EventStatus.Active, QuestStatus.Active)]
    [InlineData(ResourceKind.Quest, EventStatus.Archived, QuestStatus.Archived)]
    [InlineData(ResourceKind.Quest, EventStatus.Cancelled, QuestStatus.Cancelled)]
    [InlineData(ResourceKind.Quest, EventStatus.Completed, QuestStatus.Completed)]
    [InlineData(ResourceKind.Quest, EventStatus.Draft, QuestStatus.Draft)]
    public async Task VerifiedRecoveryPreservesContentLifecycleAndNoAdminBypass(ResourceKind kind, EventStatus eventStatus, QuestStatus questStatus)
    {
        await using var s = await AdministrationScenario.CreateAsync();
        await using (var setup = s.Database.CreateContext())
        {
            (await setup.Events.SingleAsync()).Status = eventStatus;
            (await setup.Quests.SingleAsync()).Status = questStatus;
            setup.EventMemberships.Add(s.Seed.Membership(s.Replacement.Id, MembershipStatus.Removed));
            setup.Participations.Add(new QuestParticipation
            {
                QuestId = s.Seed.Quest.Id,
                UserId = s.Replacement.Id,
                Status = ParticipationStatus.None,
                ChangedUtc = s.Clock.Now
            });
            setup.QuestInvitations.Add(new QuestInvitation
            {
                QuestId = s.Seed.Quest.Id,
                UserId = s.Replacement.Id,
                InvitedById = s.Seed.Other.Id,
                Status = QuestInvitationStatus.Revoked,
                ChangedUtc = s.Clock.Now
            });
            await setup.SaveChangesAsync();
        }
        var preview = await s.PreviewAsync(kind);
        var serialized = JsonSerializer.Serialize(preview);
        Assert.DoesNotContain(s.Seed.Quest.Title, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(s.Seed.Event.Name, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(s.Seed.Other.Id.ToString(), serialized, StringComparison.Ordinal);
        await s.Service.RecoverAsync(preview, s.Replacement.Id, s.Replacement.Version, "Verified departure procedure executed.");
        await using var db = s.Database.CreateContext();
        var parent = await db.Events.SingleAsync();
        var quest = await db.Quests.SingleAsync();
        Assert.Equal(eventStatus, parent.Status);
        Assert.Equal(questStatus, quest.Status);
        Assert.Equal(s.Seed.Quest.Title, quest.Title);
        Assert.Equal(s.Seed.Quest.Description, quest.Description);
        Assert.Equal(s.Seed.Quest.Location, quest.Location);
        Assert.Equal(s.Seed.Quest.CalendarRevision, quest.CalendarRevision);
        Assert.Equal(QuestVisibility.Private, quest.Visibility);
        Assert.Equal(MembershipStatus.Active, (await db.EventMemberships.SingleAsync(x => x.UserId == s.Replacement.Id)).Status);
        Assert.Equal(2, await db.EventMemberships.CountAsync());
        Assert.Equal(ParticipationStatus.None, (await db.Participations.SingleAsync()).Status);
        Assert.Equal(QuestInvitationStatus.Revoked, (await db.QuestInvitations.SingleAsync()).Status);
        Assert.Empty(await db.CalendarDeliveryStates.ToArrayAsync());
        Assert.Empty(await db.EventStatusHistory.ToArrayAsync());
        Assert.Empty(await db.QuestStatusHistory.ToArrayAsync());
        if (kind == ResourceKind.Event)
        {
            Assert.Equal(s.Replacement.Id, (await db.EventOwners.SingleAsync()).UserId);
            Assert.Equal(s.Seed.Other.Id, (await db.QuestOwners.SingleAsync()).UserId);
        }
        else
        {
            Assert.Equal(s.Replacement.Id, (await db.QuestOwners.SingleAsync()).UserId);
            Assert.Equal(s.Seed.Other.Id, (await db.EventOwners.SingleAsync()).UserId);
        }
        var audits = await db.AuditEntries.ToArrayAsync();
        Assert.Equal(2, audits.Length);
        Assert.All(audits, x => Assert.Equal(s.Seed.User.Id, x.ActorId));
        Assert.Contains(audits, x => x.Action == "OwnershipRecovered" && x.Reason == "Verified departure procedure executed.");
        Assert.Contains(audits, x => x.Action == "DepartureVerificationProcedure" && x.Reason == "SYNTHETIC-TEST-PROCEDURE");
        var outbox = Assert.Single(await db.OutboxMessages.ToArrayAsync());
        var envelope = JsonSerializer.Deserialize<ChangeEnvelope>(outbox.PayloadJson)!;
        Assert.Equal(NotificationKind.OwnershipChanged, envelope.Kind);
        Assert.Equal(new[] { s.Replacement.Id }, envelope.RecipientIds);
        Assert.Equal(s.Seed.Event.Id, envelope.EventId);
        Assert.Equal(kind == ResourceKind.Quest ? s.Seed.Quest.Id : null, envelope.QuestId);
        Assert.Equal(0, envelope.CalendarRevision);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Access.RequireQuestAsync(db, quest.Id, s.Seed.User.Id))).Code);
        Assert.False(await db.EventMemberships.AnyAsync(x => x.UserId == s.Seed.User.Id));
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Service.RecoverAsync(preview, s.Replacement.Id, s.Replacement.Version, "Repeated recovery must not duplicate."))).Code);
    }

    /// <summary>Disabled accounts, merely missing proof, future timestamps and any eligible owner never authorize departure recovery.</summary>
    /// <param name="evidence">Invalid departure evidence partition.</param>
    [Theory]
    [InlineData("disabled-only")]
    [InlineData("eligible-owner")]
    [InlineData("future-proof")]
    [InlineData("second-eligible-owner")]
    public async Task UnverifiedOrExistingEligibleOwnerDeniesRecovery(string evidence)
    {
        await using var s = await AdministrationScenario.CreateAsync();
        await using (var db = s.Database.CreateContext())
        {
            var owner = await db.Users.SingleAsync(x => x.Id == s.Seed.Other.Id);
            if (evidence is "disabled-only" or "eligible-owner") owner.DepartureVerifiedUtc = null;
            if (evidence == "eligible-owner") owner.IsEligible = true;
            if (evidence == "future-proof") owner.DepartureVerifiedUtc = s.Clock.Now.AddDays(1);
            if (evidence == "second-eligible-owner") db.QuestOwners.Add(new QuestOwner { QuestId = s.Seed.Quest.Id, UserId = s.Replacement.Id });
            await db.SaveChangesAsync();
        }
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() => s.PreviewAsync())).Code);
        await using var verify = s.Database.CreateContext();
        Assert.Empty(await verify.AuditEntries.ToArrayAsync());
        Assert.Empty(await verify.OutboxMessages.ToArrayAsync());
        Assert.False(await verify.EventMemberships.AnyAsync(x => x.UserId == s.Replacement.Id));
    }

    /// <summary>The operational gate defaults closed; neither persisted timestamps nor a UI confirmation substitutes for deployment approval.</summary>
    [Fact]
    public async Task ClosedExternalGateAndMissingProcedureDenyRecovery()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        s.Gate.Enabled = false;
        Assert.Equal(ErrorCode.DependencyUnavailable, (await Assert.ThrowsAsync<DomainException>(() => s.PreviewAsync())).Code);
        s.Gate.Enabled = true;
        s.Gate.ProcedureReference = "";
        Assert.Equal(ErrorCode.DependencyUnavailable, (await Assert.ThrowsAsync<DomainException>(() => s.PreviewAsync())).Code);
        await using var db = s.Database.CreateContext();
        Assert.Empty(await db.AuditEntries.ToArrayAsync());
        Assert.Equal(s.Seed.Other.Id, (await db.QuestOwners.SingleAsync()).UserId);
    }

    /// <summary>Concurrent evidence changes, replacement revocation, foreign targets and short reasons leave all assignments and outbox untouched.</summary>
    /// <param name="failure">Mutation precondition to invalidate after preview.</param>
    [Theory]
    [InlineData("evidence-version")]
    [InlineData("replacement-revoked")]
    [InlineData("foreign-replacement")]
    [InlineData("short-reason")]
    [InlineData("actor-revoked")]
    public async Task ReauthorizationAndConcurrencyPreventPartialRecovery(string failure)
    {
        await using var s = await AdministrationScenario.CreateAsync();
        var preview = await s.PreviewAsync();
        await using (var db = s.Database.CreateContext())
        {
            if (failure == "evidence-version") (await db.Users.SingleAsync(x => x.Id == s.Seed.Other.Id)).DisplayName = "Changed evidence";
            if (failure == "replacement-revoked") (await db.Users.SingleAsync(x => x.Id == s.Replacement.Id)).IsEligible = false;
            if (failure == "foreign-replacement") (await db.Users.SingleAsync(x => x.Id == s.Replacement.Id)).TenantId = Guid.NewGuid();
            if (failure == "actor-revoked") db.Administrators.Remove(await db.Administrators.SingleAsync());
            await db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<DomainException>(() => s.Service.RecoverAsync(preview,
            s.Replacement.Id, s.Replacement.Version, failure == "short-reason" ? "short" : "Verified operational departure."));
        Assert.Equal(failure switch
        {
            "evidence-version" => ErrorCode.Conflict,
            "actor-revoked" => ErrorCode.Forbidden,
            _ => ErrorCode.Validation
        }, error.Code);
        await using var verify = s.Database.CreateContext();
        Assert.Equal(s.Seed.Other.Id, (await verify.QuestOwners.SingleAsync()).UserId);
        Assert.False(await verify.EventMemberships.AnyAsync(x => x.UserId == s.Replacement.Id));
        Assert.Empty(await verify.AuditEntries.ToArrayAsync());
        Assert.Empty(await verify.OutboxMessages.ToArrayAsync());
    }

    /// <summary>Injected staging failure rolls back owner replacement, individual membership and both audits in the same real SQL transaction.</summary>
    [Fact]
    public async Task OutboxFailureRollsBackMembershipOwnershipAndAudit()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        var preview = await s.PreviewAsync();
        var service = new AdministrationService(s.Factory, s.Access, new ThrowingChangeWriter(), s.Clock, s.Gate);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecoverAsync(preview, s.Replacement.Id, s.Replacement.Version, "Verified departure with injected staging failure."));
        await using var db = s.Database.CreateContext();
        Assert.Equal(s.Seed.Other.Id, (await db.QuestOwners.SingleAsync()).UserId);
        Assert.False(await db.EventMemberships.AnyAsync(x => x.UserId == s.Replacement.Id));
        Assert.Empty(await db.AuditEntries.ToArrayAsync());
        Assert.Empty(await db.OutboxMessages.ToArrayAsync());
    }

    /// <summary>Actual recovery outbox expansion stages exactly one mandatory replacement notice without sending to departed owners.</summary>
    [Fact]
    public async Task RecoveryProducerStagesOnlyEligibleReplacementNotification()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        var preview = await s.PreviewAsync();
        await s.Service.RecoverAsync(preview, s.Replacement.Id, s.Replacement.Version, "Verified operational departure.");
        var options = new Sidequest.Infrastructure.Background.DurableWorkOptions();
        var queue = new Sidequest.Infrastructure.Background.SqlWorkQueue(s.Factory, s.Clock, options);
        var lease = (await queue.ClaimAsync("outbox"))!;
        var execution = new Sidequest.Infrastructure.Background.WorkExecutionContext { Lease = lease };
        var handler = new Sidequest.Infrastructure.Delivery.ChangeOutboxHandler(s.Factory, new(), new(s.Clock), execution, s.Clock);
        await handler.ExecuteAsync(lease.Id, CancellationToken.None);
        await using var db = s.Database.CreateContext();
        var notification = Assert.Single(await db.Notifications.ToArrayAsync());
        Assert.Equal(s.Replacement.Id, notification.UserId);
        Assert.Equal(NotificationKind.OwnershipChanged, notification.Kind);
        var delivery = Assert.Single(await db.NotificationDeliveries.ToArrayAsync());
        Assert.Equal(s.Replacement.Id, delivery.UserId);
        Assert.True(JsonSerializer.Deserialize<Sidequest.Application.Notifications.Implementation.DeliveryPayload>(delivery.PayloadJson)!.Mandatory);
        Assert.Empty(await db.CalendarDeliveryStates.ToArrayAsync());
    }

    /// <summary>The Event update/range lock is the first statement in each recovery transaction, before actor and ownership reads.</summary>
    [Fact]
    public async Task RecoveryLocksEventBeforeTransactionalAuthorizationAndFacts()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        var observer = new SqlCommandOrderObserver();
        var service = new AdministrationService(new ObservedContextFactory(s.Database, observer), s.Access,
            new ChangeWriter(), s.Clock, s.Gate);
        var preview = await service.PreviewRecoveryAsync(ResourceKind.Quest, s.Seed.Quest.Id);
        await service.RecoverAsync(preview, s.Replacement.Id, s.Replacement.Version, "Verified departure procedure executed.");
        var transactions = observer.Commands.GroupBy(x => x.TransactionId).ToArray();
        Assert.Equal(2, transactions.Length);
        Assert.All(transactions, statements =>
        {
            Assert.Contains("UPDLOCK, HOLDLOCK", statements.First().Sql, StringComparison.Ordinal);
            Assert.Contains(statements.Skip(1), x => x.Sql.Contains("[Administrators]", StringComparison.Ordinal));
            Assert.Contains(statements.Skip(1), x => x.Sql.Contains("[QuestOwners]", StringComparison.Ordinal));
        });
    }

    /// <summary>Actor revocation immediately before the Event lock is acquired is rechecked inside the transaction and cannot recover ownership.</summary>
    [Fact]
    public async Task ActorRevokedBetweenPreflightAndEventLockCannotRecover()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        var preview = await s.PreviewAsync();
        var interceptor = new BeforeEventLockInterceptor(async cancellationToken =>
        {
            await using var db = s.Database.CreateContext();
            db.Administrators.Remove(await db.Administrators.SingleAsync(cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
        });
        var service = new AdministrationService(new ObservedContextFactory(s.Database, interceptor), s.Access,
            new ChangeWriter(), s.Clock, s.Gate);
        Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<DomainException>(() =>
            service.RecoverAsync(preview, s.Replacement.Id, s.Replacement.Version, "Verified operational departure."))).Code);
        await using var verify = s.Database.CreateContext();
        Assert.Equal(s.Seed.Other.Id, (await verify.QuestOwners.SingleAsync()).UserId);
        Assert.Empty(await verify.AuditEntries.ToArrayAsync());
        Assert.Empty(await verify.OutboxMessages.ToArrayAsync());
    }

    /// <summary>Competing recovery confirmations appoint exactly one replacement and stage one logical notification without duplicate memberships.</summary>
    [Fact]
    public async Task CompetingRecoveryConfirmationsHaveOneAtomicWinner()
    {
        await using var s = await AdministrationScenario.CreateAsync();
        var preview = await s.PreviewAsync();
        var results = await Task.WhenAll(
            Record.ExceptionAsync(() => s.Service.RecoverAsync(preview, s.Replacement.Id, s.Replacement.Version, "First verified recovery confirmation.")),
            Record.ExceptionAsync(() => s.Service.RecoverAsync(preview, s.Replacement.Id, s.Replacement.Version, "Second verified recovery confirmation.")));
        Assert.Single(results, x => x is null);
        Assert.Equal(ErrorCode.Conflict, Assert.IsType<DomainException>(Assert.Single(results, x => x is not null)).Code);
        await using var db = s.Database.CreateContext();
        Assert.Equal(s.Replacement.Id, (await db.QuestOwners.SingleAsync()).UserId);
        Assert.Single(await db.EventMemberships.Where(x => x.UserId == s.Replacement.Id).ToArrayAsync());
        Assert.Equal(2, await db.AuditEntries.CountAsync());
        Assert.Single(await db.OutboxMessages.ToArrayAsync());
    }

    private sealed class ThrowingChangeWriter : IChangeWriter
    {
        /// <inheritdoc />
        public void Append(ISidequestDbContext db, ChangeEnvelope change) => throw new InvalidOperationException("Synthetic staging failure.");
    }
}
