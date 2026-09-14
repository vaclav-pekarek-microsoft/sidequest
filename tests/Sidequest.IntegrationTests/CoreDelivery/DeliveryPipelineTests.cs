using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Delivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreDelivery;

/// <summary>Real durable pipeline checks with only external email transport replaced by deterministic recording behavior.</summary>
public sealed class DeliveryPipelineTests
{
    /// <summary>Repeating a committed outbox after a crash neither recreates notifications nor changes the retained calendar intent.</summary>
    [Fact]
    public async Task OutboxRecoveryDeduplicatesNotificationCalendarAndReminder()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        var lease = (await s.Queue.ClaimAsync("outbox"))!;
        s.Execution.Lease = lease;
        await s.Changes.ExecuteAsync(lease.Id, CancellationToken.None);
        string payload;
        await using (var read = s.Database.CreateContext())
        {
            payload = (await read.NotificationDeliveries.SingleAsync()).PayloadJson;
            Assert.Single(await read.Notifications.ToListAsync());
            Assert.Single(await read.ScheduledWork.ToListAsync());
        }
        s.Clock.Now += s.Options.LeaseDuration;
        Assert.Null(await s.Queue.ClaimAsync("outbox"));
        await s.Changes.ExecuteAsync(lease.Id, CancellationToken.None);
        await using var final = s.Database.CreateContext();
        Assert.Equal(payload, (await final.NotificationDeliveries.SingleAsync()).PayloadJson);
        Assert.Single(await final.Notifications.ToListAsync());
        Assert.Single(await final.CalendarDeliveryStates.ToListAsync());
        Assert.Single(await final.ScheduledWork.ToListAsync());
    }

    /// <summary>Calendar failure retries reuse identical sequence, timestamp, bytes and logical key; acceptance is recorded only from the test receipt.</summary>
    [Fact]
    public async Task CalendarRetryRetainsExactPayloadAndRecordsActualReceipt()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        await s.ProcessChangeAsync();
        var gateway = new RecordingEmailGateway { Failure = new DeliveryTransportException(TransportOutcome.Uncertain, "timeout") };
        var dispatcher = Dispatcher(s, gateway);
        var first = (await s.Queue.ClaimAsync("delivery"))!;
        await Assert.ThrowsAsync<DeliveryTransportException>(() => dispatcher.ExecuteAsync(first, CancellationToken.None));
        await s.Queue.FailAsync(first, gateway.Failure);
        await using (var read = s.Database.CreateContext())
        {
            var row = await read.NotificationDeliveries.SingleAsync();
            Assert.Null(row.ProviderMessageId);
            Assert.True((await read.CalendarDeliveryStates.SingleAsync()).MayHaveBeenDelivered);
            s.Clock.Now = row.DueUtc;
        }
        gateway.Failure = null;
        var retry = (await s.Queue.ClaimAsync("delivery"))!;
        await dispatcher.ExecuteAsync(retry, CancellationToken.None);
        Assert.True(await s.Queue.CompleteAsync(retry));
        Assert.Equal(2, gateway.Messages.Count);
        Assert.Equal(gateway.Messages[0], gateway.Messages[1]);
        Assert.Equal("REQUEST", gateway.Messages[1].CalendarMethod);
        await using var final = s.Database.CreateContext();
        Assert.Equal("synthetic-provider-receipt", (await final.NotificationDeliveries.SingleAsync()).ProviderMessageId);
        Assert.Equal(7, (await final.CalendarDeliveryStates.SingleAsync()).SentSequence);
    }

    /// <summary>An uncertain request still requires a newer withdrawal; the delayed original request cannot be submitted afterward.</summary>
    [Fact]
    public async Task UncertainRequestThenSuspensionSendsWithdrawalAndSuppressesOldRetry()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        await s.ProcessChangeAsync();
        var gateway = new RecordingEmailGateway { Failure = new OperationCanceledException("transport cancelled after possible acceptance") };
        var dispatcher = Dispatcher(s, gateway);
        var first = (await s.Queue.ClaimAsync("delivery"))!;
        await Assert.ThrowsAsync<OperationCanceledException>(() => dispatcher.ExecuteAsync(first, CancellationToken.None));
        await s.Queue.FailAsync(first, gateway.Failure);
        await using (var update = s.Database.CreateContext())
        {
            var q = await update.Quests.SingleAsync();
            q.Status = QuestStatus.Suspended;
            q.CalendarRevision = 8;
            await update.SaveChangesAsync();
        }
        await s.AddChangeAsync(NotificationKind.QuestSuspended, 8);
        await s.ProcessChangeAsync();
        gateway.Failure = null;
        var cancellation = (await s.Queue.ClaimAsync("delivery"))!;
        await dispatcher.ExecuteAsync(cancellation, CancellationToken.None);
        await s.Queue.CompleteAsync(cancellation);
        Assert.Equal("CANCEL", gateway.Messages[1].CalendarMethod);
        Assert.DoesNotContain(s.Seed.Quest.Title, gateway.Messages[1].CalendarContent);
        Assert.Contains("SEQUENCE:8", gateway.Messages[1].CalendarContent);
        s.Clock.Now += TimeSpan.FromMinutes(1);
        var obsolete = (await s.Queue.ClaimAsync("delivery"))!;
        await dispatcher.ExecuteAsync(obsolete, CancellationToken.None);
        Assert.Equal(2, gateway.Messages.Count);
        await using var final = s.Database.CreateContext();
        Assert.Equal(WorkStatus.Superseded, (await final.NotificationDeliveries.SingleAsync(x => x.Id == first.Id)).Status);
        var state = await final.CalendarDeliveryStates.SingleAsync();
        Assert.Equal(8, state.SentSequence);
        Assert.False(state.MayHaveBeenDelivered);
    }

    /// <summary>Revoked membership before first transport prevents stale protected content; the in-app item is hidden on the next read.</summary>
    [Fact]
    public async Task AccessRevokedBeforeSendSuppressesCalendarAndRead()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        await s.ProcessChangeAsync();
        await using (var update = s.Database.CreateContext())
        {
            (await update.EventMemberships.SingleAsync()).Status = MembershipStatus.Removed;
            (await update.Participations.SingleAsync()).Status = ParticipationStatus.None;
            await update.SaveChangesAsync();
        }
        var gateway = new RecordingEmailGateway();
        var lease = (await s.Queue.ClaimAsync("delivery"))!;
        await Dispatcher(s, gateway).ExecuteAsync(lease, CancellationToken.None);
        Assert.Empty(gateway.Messages);
        Assert.Equal(0, await s.Service.UnreadCountAsync());
        await using var read = s.Database.CreateContext();
        Assert.False((await read.CalendarDeliveryStates.SingleAsync()).MayHaveBeenDelivered);
        Assert.Equal(WorkStatus.Superseded, (await read.NotificationDeliveries.SingleAsync()).Status);
    }

    /// <summary>If access disappears without a processed withdrawal event, an uncertain request schedules a minimal compensation cancellation.</summary>
    [Fact]
    public async Task UncertainRequestAccessLossCreatesDurableCompensation()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        await s.ProcessChangeAsync();
        var gateway = new RecordingEmailGateway { Failure = new DeliveryTransportException(TransportOutcome.Uncertain, "timeout") };
        var dispatcher = Dispatcher(s, gateway);
        var first = (await s.Queue.ClaimAsync("delivery"))!;
        await Assert.ThrowsAsync<DeliveryTransportException>(() => dispatcher.ExecuteAsync(first, CancellationToken.None));
        await s.Queue.FailAsync(first, gateway.Failure);
        await using (var update = s.Database.CreateContext())
        {
            (await update.EventMemberships.SingleAsync()).Status = MembershipStatus.Removed;
            (await update.Participations.SingleAsync()).Status = ParticipationStatus.None;
            s.Clock.Now = (await update.NotificationDeliveries.SingleAsync()).DueUtc;
            await update.SaveChangesAsync();
        }
        var retry = (await s.Queue.ClaimAsync("delivery"))!;
        await dispatcher.ExecuteAsync(retry, CancellationToken.None);
        Assert.Single(gateway.Messages);
        gateway.Failure = null;
        var compensation = (await s.Queue.ClaimAsync("delivery"))!;
        await dispatcher.ExecuteAsync(compensation, CancellationToken.None);
        await s.Queue.CompleteAsync(compensation);
        Assert.Equal("CANCEL", gateway.Messages[1].CalendarMethod);
        Assert.DoesNotContain("Private room", gateway.Messages[1].CalendarContent);
        Assert.Contains("SEQUENCE:8", gateway.Messages[1].CalendarContent);
    }

    /// <summary>Optional activity honors changed send-time settings without deleting the mandatory in-app record.</summary>
    [Fact]
    public async Task OptionalPreferenceChangeSuppressesEmailButRetainsInApp()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var change = new ChangeEnvelope(Guid.NewGuid(), NotificationKind.QuestUpdated, s.Seed.Event.Id, s.Seed.Quest.Id,
            s.Seed.Other.Id, [s.Seed.User.Id], s.Clock.Now, CalendarChanged: false);
        await s.AddChangeAsync(change);
        await s.ProcessChangeAsync();
        await s.Service.SavePreferencesAsync(new(false, false, true, 1m, null));
        var gateway = new RecordingEmailGateway();
        var lease = (await s.Queue.ClaimAsync("delivery"))!;
        await Dispatcher(s, gateway).ExecuteAsync(lease, CancellationToken.None);
        Assert.Empty(gateway.Messages);
        Assert.Equal(1, await s.Service.UnreadCountAsync());
    }

    /// <summary>Unregistered/unknown work versions and malformed payloads are visible permanent failures rather than fake successes.</summary>
    [Fact]
    public async Task UnknownAndMalformedWorkDeadLetterThroughRealRunner()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await FoundationSeed.PersistAsync(s.Database,
            new OutboxMessage { Type = WorkTypes.Change, PayloadJson = "{invalid", DueUtc = s.Clock.Now },
            new ScheduledWork { Type = "quest.reminder.v99", DeduplicationKey = "unknown", DueUtc = s.Clock.Now });
        var gateway = new RecordingEmailGateway();
        var runner = new DurableWorkRunner(s.Queue, [s.Changes, s.Reminders], Dispatcher(s, gateway),
            s.Execution, s.Clock, s.Options, NullLogger<DurableWorkRunner>.Instance);
        Assert.True(await runner.RunOnceAsync("outbox"));
        Assert.True(await runner.RunOnceAsync("scheduled"));
        await using var read = s.Database.CreateContext();
        Assert.Equal(WorkStatus.DeadLetter, (await read.OutboxMessages.SingleAsync()).Status);
        Assert.Equal(WorkStatus.DeadLetter, (await read.ScheduledWork.SingleAsync()).Status);
        Assert.Empty(gateway.Messages);
        Assert.Empty(await read.Notifications.ToListAsync());
    }

    private static DeliveryDispatcher Dispatcher(DeliveryScenario s, RecordingEmailGateway gateway) =>
        new(s.Factory, s.Policy, s.Renderer, gateway, s.Clock, s.Options);

    /// <summary>A durable receipt written before process death prevents ordinary email resubmission during lease recovery.</summary>
    [Fact]
    public async Task ReceiptSurvivesCrashBeforeQueueCompletionWithoutResend()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var change = new ChangeEnvelope(Guid.NewGuid(), NotificationKind.MembershipAdded, s.Seed.Event.Id, null,
            s.Seed.Other.Id, [s.Seed.User.Id], s.Clock.Now, AffectedUserIds: [s.Seed.User.Id]);
        await s.AddChangeAsync(change);
        await s.ProcessChangeAsync();
        var gateway = new RecordingEmailGateway();
        var dispatcher = Dispatcher(s, gateway);
        var first = (await s.Queue.ClaimAsync("delivery"))!;
        await dispatcher.ExecuteAsync(first, CancellationToken.None);
        s.Clock.Now += s.Options.LeaseDuration;
        var recovered = (await s.Queue.ClaimAsync("delivery"))!;
        await dispatcher.ExecuteAsync(recovered, CancellationToken.None);
        Assert.True(await s.Queue.CompleteAsync(recovered));
        Assert.Single(gateway.Messages);
    }

    /// <summary>Provider I/O occurs outside a transaction; a new intent can commit, but another recipient submission cannot overlap the old call.</summary>
    [Fact]
    public async Task CalendarSessionLockSerializesSubmissionsAndReceiptCannotOverwriteNewIntent()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        await s.ProcessChangeAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new RecordingEmailGateway { BeforeReturn = async () => { entered.SetResult(); await release.Task; } };
        var first = (await s.Queue.ClaimAsync("delivery"))!;
        var running = Dispatcher(s, gateway).ExecuteAsync(first, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using (var update = s.Database.CreateContext())
            {
                var quest = await update.Quests.SingleAsync();
                quest.Status = QuestStatus.Suspended;
                quest.CalendarRevision = 8;
                await update.SaveChangesAsync();
            }
            await s.AddChangeAsync(NotificationKind.QuestSuspended, 8);
            await s.ProcessChangeAsync();
            var second = (await s.Queue.ClaimAsync("delivery"))!;
            var otherGateway = new RecordingEmailGateway();
            var locked = await Assert.ThrowsAsync<DeliveryTransportException>(() =>
                Dispatcher(s, otherGateway).ExecuteAsync(second, CancellationToken.None));
            Assert.Equal(TransportOutcome.Retryable, locked.Outcome);
            Assert.Empty(otherGateway.Messages);
        }
        finally
        {
            release.TrySetResult();
            await running;
        }
        await using var read = s.Database.CreateContext();
        var state = await read.CalendarDeliveryStates.SingleAsync();
        Assert.Equal(8, state.IntendedSequence);
        Assert.Equal("CANCEL", state.IntendedMethod);
        Assert.Equal(7, state.SentSequence);
        Assert.True(state.MayHaveBeenDelivered);
    }

    /// <summary>An ordinary calendar join remains mandatory even after all optional email and reminder preferences are disabled.</summary>
    [Fact]
    public async Task MandatoryCalendarIgnoresOptionalPreferences()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.Service.SavePreferencesAsync(new(false, false, false, 1, null));
        await s.AddChangeAsync();
        await s.ProcessChangeAsync();
        var gateway = new RecordingEmailGateway();
        var lease = (await s.Queue.ClaimAsync("delivery"))!;
        await Dispatcher(s, gateway).ExecuteAsync(lease, CancellationToken.None);
        Assert.Equal("REQUEST", Assert.Single(gateway.Messages).CalendarMethod);
    }

    /// <summary>Publication resolves registered members once; restarting cannot add a newly signed-in account to that old change.</summary>
    [Fact]
    public async Task PublicationFanoutFreezesRegisteredRecipientsAcrossRecovery()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await using (var update = s.Database.CreateContext())
        {
            (await update.Users.SingleAsync(x => x.Id == s.Seed.User.Id)).LastSignedInUtc = s.Clock.Now;
            update.EventMemberships.Add(s.Seed.Membership(s.Seed.Other.Id));
            await update.SaveChangesAsync();
        }
        var change = new ChangeEnvelope(Guid.NewGuid(), NotificationKind.QuestPublished, s.Seed.Event.Id,
            s.Seed.Quest.Id, null, [], s.Clock.Now);
        await s.AddChangeAsync(change);
        var lease = (await s.Queue.ClaimAsync("outbox"))!;
        s.Execution.Lease = lease;
        await s.Changes.ExecuteAsync(lease.Id, CancellationToken.None);
        await using (var update = s.Database.CreateContext())
        {
            (await update.Users.SingleAsync(x => x.Id == s.Seed.Other.Id)).LastSignedInUtc = s.Clock.Now;
            await update.SaveChangesAsync();
        }
        s.Clock.Now += s.Options.LeaseDuration;
        Assert.Null(await s.Queue.ClaimAsync("outbox"));
        await s.Changes.ExecuteAsync(lease.Id, CancellationToken.None);
        await using var read = s.Database.CreateContext();
        Assert.Equal(s.Seed.User.Id, (await read.Notifications.SingleAsync()).UserId);
        var captured = JsonSerializer.Deserialize<ChangeEnvelope>((await read.OutboxMessages.SingleAsync()).PayloadJson)!;
        Assert.Equal(s.Seed.User.Id, Assert.Single(captured.RecipientIds));
    }

    /// <summary>An empty committed publication audience remains empty after another account registers; no sentinel identity is invented.</summary>
    [Fact]
    public async Task EmptyPublicationSnapshotCannotExpandAfterCommit()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var change = new ChangeEnvelope(Guid.NewGuid(), NotificationKind.QuestPublished, s.Seed.Event.Id,
            s.Seed.Quest.Id, null, [], s.Clock.Now);
        await s.AddChangeAsync(change);
        await s.ProcessChangeAsync();
        await using (var update = s.Database.CreateContext())
        {
            (await update.Users.SingleAsync(x => x.Id == s.Seed.User.Id)).LastSignedInUtc = s.Clock.Now;
            await update.SaveChangesAsync();
        }
        s.Clock.Now += s.Options.LeaseDuration;
        Assert.Null(await s.Queue.ClaimAsync("outbox"));
        await using var read = s.Database.CreateContext();
        Assert.Empty(await read.Notifications.ToListAsync());
        Assert.Equal(WorkStatus.Completed, (await read.OutboxMessages.SingleAsync()).Status);
    }

    /// <summary>Join, leave and explicit rejoin use one UID and strictly increasing sequences without restoring prior participation implicitly.</summary>
    [Fact]
    public async Task JoinLeaveRejoinRetainsUidAndMonotonicSequence()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        var gateway = new RecordingEmailGateway();
        var dispatcher = Dispatcher(s, gateway);
        for (var step = 0; step < 3; step++)
        {
            await using (var update = s.Database.CreateContext())
            {
                (await update.Participations.SingleAsync()).Status = step == 1 ? ParticipationStatus.None : ParticipationStatus.Joined;
                (await update.Quests.SingleAsync()).CalendarRevision = 7 + step;
                await update.SaveChangesAsync();
            }
            await s.AddChangeAsync(step == 1 ? NotificationKind.Left : NotificationKind.Joined, 7 + step);
            await s.ProcessChangeAsync();
            var lease = (await s.Queue.ClaimAsync("delivery"))!;
            await dispatcher.ExecuteAsync(lease, CancellationToken.None);
            await s.Queue.CompleteAsync(lease);
        }
        Assert.Equal(new[] { "REQUEST", "CANCEL", "REQUEST" }, gateway.Messages.Select(x => x.CalendarMethod));
        for (var step = 0; step < 3; step++)
        {
            Assert.Contains($"UID:{s.Seed.Quest.Id:N}@sidequest.calendar", gateway.Messages[step].CalendarContent);
            Assert.Contains($"SEQUENCE:{7 + step}", gateway.Messages[step].CalendarContent);
        }
    }

    /// <summary>Event-specific publication email overrides take precedence over the opposite global setting at send time.</summary>
    /// <param name="eventEnabled">Explicit Event override.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EventEmailOverrideWinsAtSendTime(bool eventEnabled)
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await using (var update = s.Database.CreateContext())
        {
            (await update.Users.SingleAsync(x => x.Id == s.Seed.User.Id)).LastSignedInUtc = s.Clock.Now;
            await update.SaveChangesAsync();
        }
        await s.Service.SavePreferencesAsync(new(!eventEnabled, true, false, 1, null));
        await s.Service.SetEventNewQuestEmailAsync(s.Seed.Event.Id, eventEnabled);
        var change = new ChangeEnvelope(Guid.NewGuid(), NotificationKind.QuestPublished, s.Seed.Event.Id,
            s.Seed.Quest.Id, null, [], s.Clock.Now);
        await s.AddChangeAsync(change);
        await s.ProcessChangeAsync();
        var gateway = new RecordingEmailGateway();
        var lease = (await s.Queue.ClaimAsync("delivery"))!;
        await Dispatcher(s, gateway).ExecuteAsync(lease, CancellationToken.None);
        Assert.Equal(eventEnabled ? 1 : 0, gateway.Messages.Count);
        Assert.Equal(1, await s.Service.UnreadCountAsync());
    }

    /// <summary>A late provider receipt from a reclaimed delivery cannot overwrite new ownership or record a false sent sequence.</summary>
    [Fact]
    public async Task StaleProviderCompletionCannotOverwriteReclaimedDelivery()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        await s.ProcessChangeAsync();
        WorkLease? replacement = null;
        var gateway = new RecordingEmailGateway
        {
            BeforeReturn = async () =>
            {
                s.Clock.Now += s.Options.LeaseDuration;
                replacement = await s.Queue.ClaimAsync("delivery");
            }
        };
        var first = (await s.Queue.ClaimAsync("delivery"))!;
        await Dispatcher(s, gateway).ExecuteAsync(first, CancellationToken.None);
        Assert.NotNull(replacement);
        Assert.False(await s.Queue.CompleteAsync(first));
        await using var read = s.Database.CreateContext();
        var delivery = await read.NotificationDeliveries.SingleAsync();
        Assert.Equal(replacement.Token, delivery.LeaseId);
        Assert.Null(delivery.ProviderMessageId);
        var calendar = await read.CalendarDeliveryStates.SingleAsync();
        Assert.Null(calendar.SentSequence);
        Assert.True(calendar.MayHaveBeenDelivered);
    }

    /// <summary>Completion never invents a withdrawal from an uncertain request, including when the parent is subsequently cancelled.</summary>
    /// <param name="cancelParent">Whether the Event is cancelled after the child has already ended.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainCalendarCompletionDoesNotInventWithdrawal(bool cancelParent)
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        await s.ProcessChangeAsync();
        var gateway = new RecordingEmailGateway { Failure = new DeliveryTransportException(TransportOutcome.Uncertain, "timeout") };
        var dispatcher = Dispatcher(s, gateway);
        var first = (await s.Queue.ClaimAsync("delivery"))!;
        await Assert.ThrowsAsync<DeliveryTransportException>(() => dispatcher.ExecuteAsync(first, CancellationToken.None));
        await s.Queue.FailAsync(first, gateway.Failure);
        await using (var update = s.Database.CreateContext())
        {
            var quest = await update.Quests.SingleAsync();
            quest.Status = QuestStatus.Completed;
            s.Clock.Now = quest.EndUtc;
            if (cancelParent)
                (await update.Events.SingleAsync()).Status = EventStatus.Cancelled;
            await update.SaveChangesAsync();
        }
        var retry = (await s.Queue.ClaimAsync("delivery"))!;
        await dispatcher.ExecuteAsync(retry, CancellationToken.None);
        Assert.Single(gateway.Messages);
        await using var read = s.Database.CreateContext();
        Assert.Single(await read.NotificationDeliveries.ToListAsync());
        Assert.Equal("REQUEST", (await read.CalendarDeliveryStates.SingleAsync()).IntendedMethod);
    }

    /// <summary>Joined managers receive observer in-app visibility, not a second attendee email/calendar caused by someone else's join.</summary>
    [Fact]
    public async Task JoinedManagerObserverDoesNotReceiveAnotherUsersCalendar()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await FoundationSeed.PersistAsync(s.Database, s.Seed.Membership(s.Seed.Other.Id),
            new QuestOwner { QuestId = s.Seed.Quest.Id, UserId = s.Seed.Other.Id },
            new QuestParticipation
            {
                QuestId = s.Seed.Quest.Id,
                UserId = s.Seed.Other.Id,
                Status = ParticipationStatus.Joined,
                ChangedUtc = s.Clock.Now.AddMinutes(-1)
            });
        var change = new ChangeEnvelope(Guid.NewGuid(), NotificationKind.Joined, s.Seed.Event.Id, s.Seed.Quest.Id,
            s.Seed.User.Id, [s.Seed.User.Id, s.Seed.Other.Id], s.Clock.Now, 7,
            PreviousAttendeeIds: [s.Seed.Other.Id], CalendarChanged: true, AffectedUserIds: [s.Seed.User.Id]);
        await s.AddChangeAsync(change);
        await s.ProcessChangeAsync();
        await using var read = s.Database.CreateContext();
        Assert.Equal(2, await read.Notifications.CountAsync());
        Assert.Equal(s.Seed.User.Id, (await read.NotificationDeliveries.SingleAsync()).UserId);
        Assert.Equal(s.Seed.User.Id, (await read.CalendarDeliveryStates.SingleAsync()).UserId);
    }

    /// <summary>Captured membership decision targets win over coincident timestamps on other recipients' persisted decisions.</summary>
    [Fact]
    public async Task MembershipDecisionSeparatesAffectedRequesterFromManagerObservers()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await FoundationSeed.PersistAsync(s.Database, s.Seed.Membership(s.Seed.Other.Id),
            new EventOwner { EventId = s.Seed.Event.Id, UserId = s.Seed.Other.Id },
            new EventMembershipRequest
            {
                EventId = s.Seed.Event.Id,
                UserId = s.Seed.User.Id,
                Status = MembershipRequestStatus.Approved,
                CreatedUtc = s.Clock.Now.AddMinutes(-1),
                DecidedUtc = s.Clock.Now.AddSeconds(-1),
                DecidedById = s.Seed.Other.Id
            },
            new EventMembershipRequest
            {
                EventId = s.Seed.Event.Id,
                UserId = s.Seed.Other.Id,
                Status = MembershipRequestStatus.Approved,
                CreatedUtc = s.Clock.Now.AddMinutes(-2),
                DecidedUtc = s.Clock.Now,
                DecidedById = s.Seed.Other.Id
            });
        var change = new ChangeEnvelope(Guid.NewGuid(), NotificationKind.MembershipDecided, s.Seed.Event.Id, null,
            s.Seed.Other.Id, [s.Seed.User.Id, s.Seed.Other.Id], s.Clock.Now, AffectedUserIds: [s.Seed.User.Id]);
        await s.AddChangeAsync(change);
        await s.ProcessChangeAsync();
        await using var read = s.Database.CreateContext();
        Assert.Equal(2, await read.Notifications.CountAsync());
        Assert.Equal(s.Seed.User.Id, (await read.NotificationDeliveries.SingleAsync()).UserId);
    }

    /// <summary>Coincident action timestamps and later membership/participation mutations cannot redirect captured mandatory mail or calendar withdrawal.</summary>
    /// <param name="kind">Membership decision or attendee access loss requiring a recipient-only withdrawal.</param>
    [Theory]
    [InlineData(NotificationKind.MembershipDecided)]
    [InlineData(NotificationKind.AccessRemoved)]
    public async Task CapturedTargetSurvivesCoincidentChangesAndLaterStateMutation(NotificationKind kind)
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.Service.SavePreferencesAsync(new(false, false, false, 1m, null));
        var occurredUtc = s.Clock.Now;
        s.Seed.Other.Email = "observer@example.invalid";
        await using (var addresses = s.Database.CreateContext())
        {
            (await addresses.Users.SingleAsync(x => x.Id == s.Seed.Other.Id)).Email = s.Seed.Other.Email;
            await addresses.SaveChangesAsync();
        }
        Assert.NotEqual(s.Seed.User.Email, s.Seed.Other.Email);
        await FoundationSeed.PersistAsync(s.Database, s.Seed.Membership(s.Seed.Other.Id),
            new QuestParticipation
            {
                QuestId = s.Seed.Quest.Id,
                UserId = s.Seed.Other.Id,
                Status = ParticipationStatus.Joined,
                ChangedUtc = occurredUtc
            },
            new EventMembershipRequest
            {
                EventId = s.Seed.Event.Id,
                UserId = s.Seed.User.Id,
                Status = MembershipRequestStatus.Approved,
                CreatedUtc = occurredUtc.AddMinutes(-1),
                DecidedUtc = occurredUtc,
                DecidedById = s.Seed.Other.Id
            },
            new EventMembershipRequest
            {
                EventId = s.Seed.Event.Id,
                UserId = s.Seed.Other.Id,
                Status = MembershipRequestStatus.Approved,
                CreatedUtc = occurredUtc.AddMinutes(-1),
                DecidedUtc = occurredUtc,
                DecidedById = s.Seed.Other.Id
            });
        var withdrawal = kind == NotificationKind.AccessRemoved;
        if (withdrawal)
        {
            await using var setup = s.Database.CreateContext();
            var quest = await setup.Quests.SingleAsync();
            quest.CalendarRevision = 8;
            quest.Visibility = QuestVisibility.Private;
            foreach (var user in new[] { s.Seed.User, s.Seed.Other })
            {
                var snapshot = new CalendarSnapshot(quest.Id, 7, occurredUtc, quest.StartUtc, quest.EndUtc,
                    user.Email, "REQUEST", quest.Title, quest.Description, quest.Location);
                setup.CalendarDeliveryStates.Add(new CalendarDeliveryState
                {
                    QuestId = quest.Id,
                    UserId = user.Id,
                    IntendedSequence = 7,
                    SentSequence = 7,
                    IntendedMethod = "REQUEST",
                    Payload = JsonSerializer.Serialize(snapshot),
                    MayHaveBeenDelivered = true,
                    ChangedUtc = occurredUtc
                });
            }
            await setup.SaveChangesAsync();
        }
        var change = new ChangeEnvelope(Guid.NewGuid(), kind, s.Seed.Event.Id, withdrawal ? s.Seed.Quest.Id : null,
            s.Seed.Other.Id, [s.Seed.User.Id, s.Seed.Other.Id], occurredUtc, withdrawal ? 8 : 0,
            PreviousAttendeeIds: withdrawal ? [s.Seed.User.Id, s.Seed.Other.Id] : null, AffectedUserIds: [s.Seed.User.Id]);
        await s.AddChangeAsync(change);
        s.Clock.Now += TimeSpan.FromMinutes(1);
        await using (var later = s.Database.CreateContext())
        {
            foreach (var membership in await later.EventMemberships.ToListAsync())
            {
                membership.Status = MembershipStatus.Removed;
                membership.ChangedUtc = s.Clock.Now;
            }
            foreach (var participation in await later.Participations.ToListAsync())
            {
                participation.Status = ParticipationStatus.None;
                participation.ChangedUtc = s.Clock.Now;
            }
            (await later.MembershipRequests.SingleAsync(x => x.UserId == s.Seed.User.Id)).DecidedUtc = s.Clock.Now;
            await later.SaveChangesAsync();
        }
        await s.ProcessChangeAsync();
        var gateway = new RecordingEmailGateway();
        var lease = (await s.Queue.ClaimAsync("delivery"))!;
        await Dispatcher(s, gateway).ExecuteAsync(lease, CancellationToken.None);
        Assert.True(await s.Queue.CompleteAsync(lease));
        var message = Assert.Single(gateway.Messages);
        Assert.Equal(s.Seed.User.Email, message.Recipient);
        Assert.DoesNotContain(s.Seed.Other.Email, message.TextBody);
        await using var read = s.Database.CreateContext();
        var delivery = await read.NotificationDeliveries.SingleAsync();
        var retained = JsonSerializer.Deserialize<DeliveryPayload>(delivery.PayloadJson)!;
        Assert.Equal(s.Seed.User.Id, delivery.UserId);
        Assert.Equal(occurredUtc, retained.Change.OccurredUtc);
        Assert.Equal(new[] { s.Seed.User.Id }, retained.Change.AffectedUserIds);
        Assert.Equal(WorkStatus.Completed, delivery.Status);
        if (withdrawal)
        {
            Assert.Equal("CANCEL", message.CalendarMethod);
            Assert.Contains(s.Seed.User.Email, message.CalendarContent!);
            Assert.DoesNotContain(s.Seed.Other.Email, message.CalendarContent!);
            Assert.Equal(8, (await read.CalendarDeliveryStates.SingleAsync(x => x.UserId == s.Seed.User.Id)).SentSequence);
            var observer = await read.CalendarDeliveryStates.SingleAsync(x => x.UserId == s.Seed.Other.Id);
            Assert.Equal(7, observer.IntendedSequence);
            Assert.Equal(7, observer.SentSequence);
            Assert.Equal("REQUEST", observer.IntendedMethod);
        }
        else
            Assert.Null(message.CalendarContent);
    }

    /// <summary>Private suspended-edit moderator notifications are generic, roster-free and disappear when that ownership permission is removed.</summary>
    [Fact]
    public async Task SuspendedEditModeratorNoticeIsGenericAndOwnershipRechecked()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await using (var update = s.Database.CreateContext())
        {
            var quest = await update.Quests.SingleAsync();
            quest.Visibility = QuestVisibility.Private;
            quest.Status = QuestStatus.Suspended;
            update.EventOwners.Add(new EventOwner { EventId = s.Seed.Event.Id, UserId = s.Seed.User.Id });
            await update.SaveChangesAsync();
        }
        await s.AddChangeAsync(NotificationKind.SuspendedQuestEdited);
        await s.ProcessChangeAsync();
        var notification = Assert.Single((await s.Service.ListAsync(new())).Items);
        Assert.Null(notification.QuestId);
        Assert.Null(notification.EventId);
        Assert.DoesNotContain(s.Seed.Quest.Title, notification.Summary);
        await using (var update = s.Database.CreateContext())
        {
            Assert.Empty(await update.NotificationDeliveries.ToListAsync());
            update.EventOwners.Remove(await update.EventOwners.SingleAsync());
            await update.SaveChangesAsync();
        }
        Assert.Equal(0, await s.Service.UnreadCountAsync());
    }

    /// <summary>Missing live-provider configuration is a durable, visible permanent failure while recipient inbox access remains usable.</summary>
    [Fact]
    public async Task MissingProviderConfigurationDeadLettersWithoutDisablingInbox()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        await s.ProcessChangeAsync();
        var gateway = new AcsEmailGateway(new(), NullLogger<AcsEmailGateway>.Instance);
        var dispatcher = new DeliveryDispatcher(s.Factory, s.Policy, s.Renderer, gateway, s.Clock, s.Options);
        var runner = new DurableWorkRunner(s.Queue, [s.Changes, s.Reminders], dispatcher,
            s.Execution, s.Clock, s.Options, NullLogger<DurableWorkRunner>.Instance);
        Assert.True(await runner.RunOnceAsync("delivery"));
        Assert.Equal(1, await s.Service.UnreadCountAsync());
        await using var read = s.Database.CreateContext();
        var failure = await read.NotificationDeliveries.SingleAsync();
        Assert.Equal(WorkStatus.DeadLetter, failure.Status);
        Assert.Null(failure.ProviderMessageId);
        Assert.Equal(1, failure.Attempts);
    }

    /// <summary>A missing trusted directory address never falls back to an identity display name or synthetic email success.</summary>
    [Fact]
    public async Task MissingRecipientAddressCreatesVisibleFailureWithoutTransportCall()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await using (var update = s.Database.CreateContext())
        {
            (await update.Users.SingleAsync(x => x.Id == s.Seed.User.Id)).Email = "";
            await update.SaveChangesAsync();
        }
        await s.AddChangeAsync();
        await s.ProcessChangeAsync();
        var gateway = new RecordingEmailGateway();
        var runner = new DurableWorkRunner(s.Queue, [s.Changes, s.Reminders], Dispatcher(s, gateway),
            s.Execution, s.Clock, s.Options, NullLogger<DurableWorkRunner>.Instance);
        await runner.RunOnceAsync("delivery");
        Assert.Empty(gateway.Messages);
        Assert.Equal(1, await s.Service.UnreadCountAsync());
        await using var read = s.Database.CreateContext();
        Assert.Equal(WorkStatus.DeadLetter, (await read.NotificationDeliveries.SingleAsync()).Status);
        Assert.False((await read.CalendarDeliveryStates.SingleAsync()).MayHaveBeenDelivered);
    }

    /// <summary>Invite/follower-only access loss uses explicit affected-target metadata and sends mandatory minimal mail without prior-attendance inference.</summary>
    [Fact]
    public async Task AccessLossWithoutAttendanceUsesExplicitAffectedTarget()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await using (var update = s.Database.CreateContext())
        {
            (await update.Quests.SingleAsync()).Visibility = QuestVisibility.Private;
            (await update.Participations.SingleAsync()).Status = ParticipationStatus.None;
            update.QuestInvitations.Add(s.Seed.Invitation(QuestInvitationStatus.Revoked));
            await update.SaveChangesAsync();
        }
        await s.Service.SavePreferencesAsync(new(false, false, false, 1, null));
        var change = new ChangeEnvelope(Guid.NewGuid(), NotificationKind.AccessRemoved, s.Seed.Event.Id,
            s.Seed.Quest.Id, s.Seed.Other.Id, [s.Seed.User.Id], s.Clock.Now, 7, PreviousAttendeeIds: [],
            AffectedUserIds: [s.Seed.User.Id]);
        await s.AddChangeAsync(change);
        await s.ProcessChangeAsync();
        var gateway = new RecordingEmailGateway();
        var lease = (await s.Queue.ClaimAsync("delivery"))!;
        await Dispatcher(s, gateway).ExecuteAsync(lease, CancellationToken.None);
        var message = Assert.Single(gateway.Messages);
        Assert.Equal(s.Seed.User.Email, message.Recipient);
        Assert.Null(message.CalendarContent);
        Assert.DoesNotContain(s.Seed.Quest.Title, message.TextBody);
        var notification = Assert.Single((await s.Service.ListAsync(new())).Items);
        Assert.Null(notification.QuestId);
        Assert.Null(notification.EventId);
        await using var read = s.Database.CreateContext();
        var retained = JsonSerializer.Deserialize<DeliveryPayload>((await read.NotificationDeliveries.SingleAsync()).PayloadJson)!;
        Assert.Equal(new[] { s.Seed.User.Id }, retained.Change.AffectedUserIds);
        Assert.NotEqual(retained.Change.ActorId, Assert.Single(retained.Change.AffectedUserIds!));
        Assert.Empty(retained.Change.PreviousAttendeeIds!);
        Assert.True(retained.Mandatory);
    }

    /// <summary>Explicit affected targets survive an observer's ownership and membership removal after capture without redirecting mail or calendar intent.</summary>
    [Fact]
    public async Task AccessLossAffectedTargetWinsOverLaterOwnerChanges()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await using (var update = s.Database.CreateContext())
        {
            var quest = await update.Quests.SingleAsync();
            quest.Visibility = QuestVisibility.Private;
            quest.CalendarRevision = 8;
            (await update.Participations.SingleAsync()).Status = ParticipationStatus.None;
            update.EventMemberships.Add(s.Seed.Membership(s.Seed.Other.Id));
            update.QuestOwners.Add(new QuestOwner { QuestId = quest.Id, UserId = s.Seed.Other.Id });
            foreach (var user in new[] { s.Seed.User, s.Seed.Other })
            {
                var snapshot = new CalendarSnapshot(quest.Id, 7, s.Clock.Now.AddMinutes(-1), quest.StartUtc, quest.EndUtc,
                    user.Email, "REQUEST", quest.Title, quest.Description, quest.Location);
                update.CalendarDeliveryStates.Add(new CalendarDeliveryState
                {
                    QuestId = quest.Id,
                    UserId = user.Id,
                    IntendedSequence = 7,
                    IntendedMethod = "REQUEST",
                    Payload = JsonSerializer.Serialize(snapshot),
                    MayHaveBeenDelivered = true,
                    ChangedUtc = s.Clock.Now.AddMinutes(-1)
                });
            }
            await update.SaveChangesAsync();
        }
        var change = new ChangeEnvelope(Guid.NewGuid(), NotificationKind.AccessRemoved, s.Seed.Event.Id,
            s.Seed.Quest.Id, s.Seed.Other.Id, [s.Seed.User.Id, s.Seed.Other.Id], s.Clock.Now, 8,
            PreviousAttendeeIds: [s.Seed.User.Id], AffectedUserIds: [s.Seed.User.Id]);
        await s.AddChangeAsync(change);
        s.Clock.Now += TimeSpan.FromMinutes(1);
        await using (var later = s.Database.CreateContext())
        {
            later.QuestOwners.Remove(await later.QuestOwners.SingleAsync(x => x.UserId == s.Seed.Other.Id));
            var membership = await later.EventMemberships.SingleAsync(x => x.UserId == s.Seed.Other.Id);
            membership.Status = MembershipStatus.Removed;
            membership.ChangedUtc = s.Clock.Now;
            await later.SaveChangesAsync();
        }
        await s.ProcessChangeAsync();
        await using var read = s.Database.CreateContext();
        Assert.Equal(2, await read.Notifications.CountAsync());
        var delivery = await read.NotificationDeliveries.SingleAsync();
        var retained = JsonSerializer.Deserialize<DeliveryPayload>(delivery.PayloadJson)!;
        Assert.Equal(s.Seed.User.Id, delivery.UserId);
        Assert.Equal(new[] { s.Seed.User.Id }, retained.Change.AffectedUserIds);
        Assert.Equal(change.OccurredUtc, retained.Change.OccurredUtc);
        Assert.True(retained.Mandatory);
        Assert.False(await read.QuestOwners.AnyAsync(x => x.UserId == s.Seed.Other.Id));
        Assert.Equal(MembershipStatus.Removed, (await read.EventMemberships.SingleAsync(x => x.UserId == s.Seed.Other.Id)).Status);
        var observer = await read.CalendarDeliveryStates.SingleAsync(x => x.UserId == s.Seed.Other.Id);
        Assert.Equal(7, observer.IntendedSequence);
        Assert.Equal("REQUEST", observer.IntendedMethod);
        Assert.Equal("CANCEL", (await read.CalendarDeliveryStates.SingleAsync(x => x.UserId == s.Seed.User.Id)).IntendedMethod);
    }

    /// <summary>Every targeted kind rejects missing metadata, including sole-recipient payloads; empty or foreign targets also fail without effects.</summary>
    /// <param name="kind">Targeted source discriminator.</param>
    /// <param name="shape">Missing, ambiguous, empty, foreign or empty-identifier target shape.</param>
    [Theory]
    [InlineData(NotificationKind.MembershipAdded, 0)]
    [InlineData(NotificationKind.MembershipDecided, 0)]
    [InlineData(NotificationKind.Joined, 0)]
    [InlineData(NotificationKind.Left, 0)]
    [InlineData(NotificationKind.AttendeeRemoved, 0)]
    [InlineData(NotificationKind.AccessRemoved, 0)]
    [InlineData(NotificationKind.AccessRemoved, 1)]
    [InlineData(NotificationKind.AccessRemoved, 2)]
    [InlineData(NotificationKind.AccessRemoved, 3)]
    [InlineData(NotificationKind.AccessRemoved, 4)]
    public async Task InvalidAffectedTargetsFailExplicitly(NotificationKind kind, int shape)
    {
        await using var s = await DeliveryScenario.CreateAsync();
        Guid[]? targets = shape switch { 2 => [], 3 => [s.Seed.Other.Id], 4 => [Guid.Empty], _ => null };
        var change = new ChangeEnvelope(Guid.NewGuid(), kind, s.Seed.Event.Id,
            s.Seed.Quest.Id, s.Seed.Other.Id, shape == 1 ? [s.Seed.User.Id, s.Seed.Other.Id] : [s.Seed.User.Id],
            s.Clock.Now, 7, PreviousAttendeeIds: [], AffectedUserIds: targets);
        await s.AddChangeAsync(change);
        var gateway = new RecordingEmailGateway();
        var runner = new DurableWorkRunner(s.Queue, [s.Changes, s.Reminders], Dispatcher(s, gateway),
            s.Execution, s.Clock, s.Options, NullLogger<DurableWorkRunner>.Instance);
        await runner.RunOnceAsync("outbox");
        await using var read = s.Database.CreateContext();
        Assert.Equal(WorkStatus.DeadLetter, (await read.OutboxMessages.SingleAsync()).Status);
        Assert.Empty(await read.Notifications.ToListAsync());
        Assert.Empty(await read.NotificationDeliveries.ToListAsync());
        Assert.Empty(gateway.Messages);
    }

    /// <summary>A persisted delivery addressed to an observing recipient cannot bypass captured affected-target checks at submission time.</summary>
    [Fact]
    public async Task DeliveryRejectsRecipientOutsideAffectedTargets()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        await s.ProcessChangeAsync();
        await using (var update = s.Database.CreateContext())
        {
            var row = await update.NotificationDeliveries.SingleAsync();
            var payload = JsonSerializer.Deserialize<DeliveryPayload>(row.PayloadJson)!;
            row.PayloadJson = JsonSerializer.Serialize(payload with
            {
                Change = payload.Change with { RecipientIds = [s.Seed.User.Id, s.Seed.Other.Id], AffectedUserIds = [s.Seed.Other.Id] }
            });
            await update.SaveChangesAsync();
        }
        var gateway = new RecordingEmailGateway();
        var runner = new DurableWorkRunner(s.Queue, [s.Changes, s.Reminders], Dispatcher(s, gateway),
            s.Execution, s.Clock, s.Options, NullLogger<DurableWorkRunner>.Instance);
        await runner.RunOnceAsync("delivery");
        await using var read = s.Database.CreateContext();
        Assert.Equal(WorkStatus.DeadLetter, (await read.NotificationDeliveries.SingleAsync()).Status);
        Assert.Null((await read.CalendarDeliveryStates.SingleAsync()).SentSequence);
        Assert.Empty(gateway.Messages);
    }

    /// <summary>Malformed reminder storage rolls preference edits back and dead-letters due work rather than silently rebuilding a success-shaped schedule.</summary>
    [Fact]
    public async Task MalformedReminderRequiresExplicitRepairWithoutPreferenceMutation()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.Service.SavePreferencesAsync(new(false, true, true, 1m, null));
        await using (var update = s.Database.CreateContext())
        {
            (await update.ScheduledWork.SingleAsync()).PayloadJson = "{";
            await update.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<DomainException>(() => s.Service.SavePreferencesAsync(new(true, false, true, 2m, null)));
        Assert.Equal(ErrorCode.Validation, error.Code);
        var gateway = new RecordingEmailGateway();
        var runner = new DurableWorkRunner(s.Queue, [s.Changes, s.Reminders], Dispatcher(s, gateway),
            s.Execution, s.Clock, s.Options, NullLogger<DurableWorkRunner>.Instance);
        await runner.RunOnceAsync("scheduled");
        await using var read = s.Database.CreateContext();
        var schedule = await read.ScheduledWork.SingleAsync();
        Assert.Equal(WorkStatus.DeadLetter, schedule.Status);
        Assert.Equal("{", schedule.PayloadJson);
        Assert.Equal(1m, (await read.NotificationPreferences.SingleAsync()).ReminderHours);
        Assert.Single(await read.AuditEntries.ToListAsync());
        Assert.Empty(await read.Notifications.ToListAsync());
        Assert.Empty(gateway.Messages);
    }

    /// <summary>Account ineligibility blocks transport but retains an uncertain withdrawal as a visible dead letter until authorized eligible replay.</summary>
    [Fact]
    public async Task IneligibleRecipientRetainsUncertainWithdrawalForAuthorizedRecovery()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.AddChangeAsync();
        await s.ProcessChangeAsync();
        var gateway = new RecordingEmailGateway { Failure = new DeliveryTransportException(TransportOutcome.Uncertain, "timeout") };
        var runner = new DurableWorkRunner(s.Queue, [s.Changes, s.Reminders], Dispatcher(s, gateway),
            s.Execution, s.Clock, s.Options, NullLogger<DurableWorkRunner>.Instance);
        await runner.RunOnceAsync("delivery");
        await using (var update = s.Database.CreateContext())
        {
            (await update.Users.SingleAsync(x => x.Id == s.Seed.User.Id)).IsEligible = false;
            await update.SaveChangesAsync();
        }
        s.Clock.Now += TimeSpan.FromMinutes(1);
        gateway.Failure = null;
        await runner.RunOnceAsync("delivery");
        await runner.RunOnceAsync("delivery");
        Guid withdrawalId;
        await using (var read = s.Database.CreateContext())
        {
            var withdrawal = await read.NotificationDeliveries.SingleAsync(x => x.Status == WorkStatus.DeadLetter);
            withdrawalId = withdrawal.Id;
            Assert.Equal("CANCEL", JsonSerializer.Deserialize<DeliveryPayload>(withdrawal.PayloadJson)!.Calendar!.Method);
            Assert.True((await read.CalendarDeliveryStates.SingleAsync()).MayHaveBeenDelivered);
            Assert.Single(gateway.Messages);
        }
        await using (var update = s.Database.CreateContext())
        {
            (await update.Users.SingleAsync(x => x.Id == s.Seed.User.Id)).IsEligible = true;
            update.Administrators.Add(new Administrator { UserId = s.Seed.User.Id });
            await update.SaveChangesAsync();
        }
        await s.Service.ReplayAsync(withdrawalId, "delivery");
        await runner.RunOnceAsync("delivery");
        await using var final = s.Database.CreateContext();
        Assert.Equal(WorkStatus.Completed, (await final.NotificationDeliveries.SingleAsync(x => x.Id == withdrawalId)).Status);
        Assert.False((await final.CalendarDeliveryStates.SingleAsync()).MayHaveBeenDelivered);
        Assert.Equal(2, gateway.Messages.Count);
        Assert.Equal("CANCEL", gateway.Messages.Last().CalendarMethod);
    }
}
