using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.IntegrationTests.CoreComposition;

/// <summary>Exercises suspended editing, authorized reinstatement and completion through real producers, SQL queues and calendar rendering.</summary>
public sealed class SuspendedQuestDeliveryTests
{
    /// <summary>Restores only the latest edited details with the same calendar UID, then completes without withdrawing the historical appointment.</summary>
    /// <param name="visibility">Public or Private visibility retained throughout the complete lifecycle.</param>
    /// <returns>Completion after exact transport content, authorization, recipient, sequence, stale-deadline and repeat-safe history assertions.</returns>
    [Theory]
    [InlineData(QuestVisibility.Public)]
    [InlineData(QuestVisibility.Private)]
    public async Task SuspendEditReinstateComplete_RestoresLatestCalendarWithoutFinalWithdrawal(QuestVisibility visibility)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var owner = await s.AddUserAsync("owner");
        var attendee = await s.AddUserAsync("attendee");
        var id = await s.CreateQuestAsync(owner, visibility);
        if (visibility == QuestVisibility.Private)
            await s.Quests.InviteAsync(id, attendee.Id);
        await s.FlushAsync();
        var original = await s.QuestAsync(id);
        await s.ParticipateAsync(id, attendee, ParticipationCommand.Join);
        await s.FlushAsync();
        var invitation = Assert.Single(s.Gateway.Messages, x => x.CalendarContent is not null);
        EventCancellationCompositionTests.AssertCalendar(invitation, id, original.CalendarRevision + 1,
            "REQUEST", attendee.Email, s.Clock.Now, original.StartUtc, original.EndUtc);

        s.ActAs(s.Manager);
        await s.Quests.ChangeStatusAsync(id, await s.QuestVersionAsync(id), QuestStatus.Suspended, "Please revise the meeting details.");
        await s.FlushAsync();
        var withdrawal = Assert.Single(s.Gateway.Messages, x => x.CalendarMethod == "CANCEL");
        EventCancellationCompositionTests.AssertCalendar(withdrawal, id, original.CalendarRevision + 2,
            "CANCEL", attendee.Email, s.Clock.Now, original.StartUtc, original.EndUtc);
        var deliveredBeforeEdit = s.Gateway.Messages.ToArray();
        var editedInput = CoreCompositionScenario.QuestInput(visibility, startHour: 14, endHour: 16,
            location: "Latest approved room") with
        {
            Title = "Latest approved title",
            Description = "Latest approved description"
        };
        s.ActAs(owner);
        await s.Quests.EditAsync(id, await s.QuestVersionAsync(id), editedInput);
        var edited = await s.QuestAsync(id);
        Assert.Equal(QuestStatus.Suspended, edited.Status);
        Assert.Equal(visibility, edited.Visibility);
        Assert.Equal(original.CalendarRevision + 3, edited.CalendarRevision);
        Assert.Equal(original.StartRevision + 1, edited.StartRevision);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero), edited.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.Zero), edited.EndUtc);
        await s.FlushAsync();
        Assert.Equal(deliveredBeforeEdit, s.Gateway.Messages);
        var editRow = Assert.Single(await s.OutboxAsync(),
            x => CoreCompositionScenario.Payload<ChangeEnvelope>(x.PayloadJson).Kind == NotificationKind.SuspendedQuestEdited);
        var edit = CoreCompositionScenario.Payload<ChangeEnvelope>(editRow.PayloadJson);
        Assert.Equal(new[] { owner.Id, s.Manager.Id }.Order(), edit.RecipientIds.Order());
        Assert.False(edit.CalendarChanged);
        Notification[] notices;
        await using (var read = s.Read())
        {
            notices = await read.Notifications.Where(x => x.SourceChangeId == edit.ChangeId).ToArrayAsync();
            Assert.Equal(new[] { owner.Id, s.Manager.Id }.Order(), notices.Select(x => x.UserId).Order());
            var noticeIds = notices.Select(x => x.Id).ToArray();
            Assert.Empty(await read.NotificationDeliveries.Where(x => noticeIds.Contains(x.NotificationId)).ToArrayAsync());
        }
        foreach (var recipient in new[] { owner, s.Manager })
        {
            s.ActAs(recipient);
            var noticeId = Assert.Single(notices, x => x.UserId == recipient.Id).Id;
            var notice = Assert.Single((await s.Notifications.ListAsync(new())).Items, x => x.Id == noticeId);
            Assert.Null(notice.EventId);
            Assert.Null(notice.QuestId);
            Assert.Equal("Suspended Quest details changed and require moderator review.", notice.Summary);
        }
        s.ActAs(owner);
        var editedVersion = await s.QuestVersionAsync(id);
        var denied = await Assert.ThrowsAsync<DomainException>(() =>
            s.Quests.ChangeStatusAsync(id, editedVersion, QuestStatus.Active, "The owner requests reinstatement."));
        Assert.Equal(ErrorCode.NotFound, denied.Code);
        Assert.Equal(editedVersion, await s.QuestVersionAsync(id));
        Assert.Equal(deliveredBeforeEdit, s.Gateway.Messages);

        s.ActAs(s.Manager);
        await s.Quests.ChangeStatusAsync(id, editedVersion, QuestStatus.Active, "The latest details are approved.");
        await s.FlushAsync();
        var calendars = s.Gateway.Messages.Where(x => x.CalendarContent is not null).ToArray();
        Assert.Equal(new[] { "REQUEST", "CANCEL", "REQUEST" }, calendars.Select(x => x.CalendarMethod));
        var restored = calendars[2];
        EventCancellationCompositionTests.AssertCalendar(restored, id, original.CalendarRevision + 4,
            "REQUEST", attendee.Email, s.Clock.Now, edited.StartUtc, edited.EndUtc, editedInput.Title);
        var restoredContent = Assert.IsType<string>(restored.CalendarContent).Replace("\r\n ", "", StringComparison.Ordinal);
        Assert.Contains($"DESCRIPTION:{editedInput.Description}\r\n", restoredContent, StringComparison.Ordinal);
        Assert.Contains($"LOCATION:{editedInput.Location}\r\n", restoredContent, StringComparison.Ordinal);
        Assert.DoesNotContain(original.Title, restoredContent, StringComparison.Ordinal);
        Assert.DoesNotContain(original.Description, restoredContent, StringComparison.Ordinal);
        Assert.DoesNotContain(original.Location, restoredContent, StringComparison.Ordinal);
        byte[] calendarVersion;
        await using (var read = s.Read())
        {
            var state = await read.CalendarDeliveryStates.SingleAsync();
            Assert.Equal(original.CalendarRevision + 4, state.SentSequence);
            calendarVersion = state.Version;
        }
        var messagesBeforeCompletion = s.Gateway.Messages.ToArray();
        var outboxBeforeCompletion = (await s.OutboxAsync()).Select(x => x.Id).ToArray();
        s.Clock.Now = original.EndUtc;
        await s.DrainAsync("scheduled");
        await s.FlushAsync();
        Assert.Equal(QuestStatus.Active, (await s.QuestAsync(id)).Status);
        Assert.Equal(messagesBeforeCompletion, s.Gateway.Messages);

        s.Clock.Now = edited.EndUtc;
        await s.DrainAsync("scheduled");
        var completion = Assert.Single(await s.ScheduledAsync(WorkTypes.QuestCompletion),
            x => CoreCompositionScenario.Payload<QuestCompletionPayload>(x.PayloadJson).EndUtc == edited.EndUtc);
        Assert.Equal(WorkStatus.Completed, completion.Status);
        await Assert.Single(s.Handlers, x => x.WorkType == WorkTypes.QuestCompletion).ExecuteAsync(completion.Id, default);
        await s.FlushAsync();
        Assert.Equal(messagesBeforeCompletion, s.Gateway.Messages);
        Assert.Equal(outboxBeforeCompletion, (await s.OutboxAsync()).Select(x => x.Id));
        await using var final = s.Read();
        var completed = await final.Quests.SingleAsync(x => x.Id == id);
        Assert.Equal(QuestStatus.Completed, completed.Status);
        Assert.Equal(editedInput.Title, completed.Title);
        Assert.Equal(editedInput.Description, completed.Description);
        Assert.Equal(editedInput.Location, completed.Location);
        Assert.Equal(original.CalendarRevision + 4, completed.CalendarRevision);
        Assert.Equal(ParticipationStatus.Joined, (await final.Participations.SingleAsync(x => x.QuestId == id)).Status);
        var history = await final.QuestStatusHistory.Where(x => x.QuestId == id).ToArrayAsync();
        Assert.Equal(4, history.Length);
        var terminal = Assert.Single(history, x => x.Next == QuestStatus.Completed);
        Assert.Equal(QuestStatus.Active, terminal.Previous);
        Assert.Equal(edited.EndUtc, terminal.OccurredUtc);
        Assert.Null(terminal.ActorId);
        Assert.Single(await final.AuditEntries.Where(x => x.ResourceId == id && x.Action == "Status:Active->Completed").ToArrayAsync());
        Assert.Equal(calendarVersion, (await final.CalendarDeliveryStates.SingleAsync()).Version);
    }
}
