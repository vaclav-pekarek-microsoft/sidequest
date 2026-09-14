using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Infrastructure.Delivery;

namespace Sidequest.IntegrationTests.CoreDelivery;

/// <summary>Real SQL reminder production, replacement, current participation and lateness boundaries.</summary>
public sealed class ReminderPipelineTests
{
    /// <summary>One reminder survives retries and preference toggles/rejoin without creating another logical notification for the same start revision.</summary>
    [Fact]
    public async Task ReminderCompletesOnceAcrossPreferenceToggleAndRejoin()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.Service.SavePreferencesAsync(new(false, true, true, 1m, null));
        var lease = (await s.Queue.ClaimAsync("scheduled"))!;
        s.Execution.Lease = lease;
        await s.Reminders.ExecuteAsync(lease.Id, CancellationToken.None);
        await s.Reminders.ExecuteAsync(lease.Id, CancellationToken.None);
        await s.Queue.CompleteAsync(lease);
        await s.Service.SavePreferencesAsync(new(false, true, false, 1m, null));
        await s.Service.SavePreferencesAsync(new(false, true, true, 2m, null));
        await using var read = s.Database.CreateContext();
        Assert.Single(await read.Notifications.ToListAsync());
        Assert.Single(await read.NotificationDeliveries.ToListAsync());
        Assert.Equal(WorkStatus.Completed, (await read.ScheduledWork.SingleAsync()).Status);
        Assert.Null(await s.Queue.ClaimAsync("scheduled"));
    }

    /// <summary>Followers never get reminder work; departure after scheduling suppresses both channels.</summary>
    /// <param name="participation">Non-attendee state applied before due work is handled.</param>
    [Theory]
    [InlineData(ParticipationStatus.Following)]
    [InlineData(ParticipationStatus.None)]
    public async Task NonJoinedParticipationSuppressesReminder(ParticipationStatus participation)
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.Service.SavePreferencesAsync(new(false, true, true, 1m, null));
        await using (var update = s.Database.CreateContext())
        {
            (await update.Participations.SingleAsync()).Status = participation;
            await update.SaveChangesAsync();
        }
        var lease = (await s.Queue.ClaimAsync("scheduled"))!;
        s.Execution.Lease = lease;
        await s.Reminders.ExecuteAsync(lease.Id, CancellationToken.None);
        await s.Queue.CompleteAsync(lease);
        await using var read = s.Database.CreateContext();
        Assert.Empty(await read.Notifications.ToListAsync());
        Assert.Empty(await read.NotificationDeliveries.ToListAsync());
    }

    /// <summary>At the configured maximum lateness reminders are permitted, but an adjacent later instant and start itself suppress them.</summary>
    /// <param name="seconds">Clock advance from the due instant.</param>
    /// <param name="expected">Expected logical reminder count.</param>
    [Theory]
    [InlineData(120, 1)]
    [InlineData(121, 0)]
    [InlineData(3600, 0)]
    public async Task LatenessAndStartBoundariesAreEnforced(int seconds, int expected)
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.Service.SavePreferencesAsync(new(false, true, true, 1m, null));
        s.Clock.Now += TimeSpan.FromSeconds(seconds);
        var lease = (await s.Queue.ClaimAsync("scheduled"))!;
        s.Execution.Lease = lease;
        await s.Reminders.ExecuteAsync(lease.Id, CancellationToken.None);
        await s.Queue.CompleteAsync(lease);
        await using var read = s.Database.CreateContext();
        Assert.Equal(expected, await read.Notifications.CountAsync());
        Assert.Equal(expected, await read.NotificationDeliveries.CountAsync());
    }

    /// <summary>Rescheduling supersedes the old revision and generates one new revision schedule rather than sending the obsolete reminder.</summary>
    [Fact]
    public async Task RescheduleReplacesStaleRevisionAndPreferenceDoesNotResetLateness()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.Service.SavePreferencesAsync(new(false, true, true, 1m, null));
        s.Clock.Now += TimeSpan.FromMinutes(3);
        await s.Service.SavePreferencesAsync(new(true, false, true, 1m, null));
        await using (var read = s.Database.CreateContext())
            Assert.Equal(s.Clock.Now.AddMinutes(-3), (await read.ScheduledWork.SingleAsync()).DueUtc);
        await using (var update = s.Database.CreateContext())
        {
            var quest = await update.Quests.SingleAsync();
            quest.StartUtc += TimeSpan.FromMinutes(30);
            quest.StartRevision++;
            await update.SaveChangesAsync();
        }
        await s.Service.SavePreferencesAsync(new(false, true, true, .5m, null));
        await using var final = s.Database.CreateContext();
        Assert.Equal(2, await final.ScheduledWork.CountAsync());
        Assert.Equal(1, await final.ScheduledWork.CountAsync(x => x.Status == WorkStatus.Superseded));
        Assert.Equal(1, await final.ScheduledWork.CountAsync(x => x.Status == WorkStatus.Pending));
        Assert.Empty(await final.Notifications.ToListAsync());
    }

    /// <summary>Disabling reminders between in-app production and transport suppresses optional email submission.</summary>
    [Fact]
    public async Task DisableAfterReminderProductionSuppressesQueuedEmail()
    {
        await using var s = await DeliveryScenario.CreateAsync();
        await s.Service.SavePreferencesAsync(new(false, true, true, 1m, null));
        var work = (await s.Queue.ClaimAsync("scheduled"))!;
        s.Execution.Lease = work;
        await s.Reminders.ExecuteAsync(work.Id, CancellationToken.None);
        await s.Queue.CompleteAsync(work);
        await s.Service.SavePreferencesAsync(new(false, true, false, 1m, null));
        var gateway = new RecordingEmailGateway();
        var delivery = (await s.Queue.ClaimAsync("delivery"))!;
        await new DeliveryDispatcher(s.Factory, s.Policy, s.Renderer, gateway, s.Clock, s.Options)
            .ExecuteAsync(delivery, CancellationToken.None);
        Assert.Empty(gateway.Messages);
    }
}
