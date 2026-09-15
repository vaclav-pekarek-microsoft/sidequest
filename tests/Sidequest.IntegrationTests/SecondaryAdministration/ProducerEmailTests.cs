using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Administration;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Application.Security;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.CoreComposition;
using Sidequest.IntegrationTests.FoundationPersistence;
using Xunit.Abstractions;

namespace Sidequest.IntegrationTests.SecondaryAdministration;

/// <summary>Connects real Quest/Event commands, durable handlers, business configuration and recording transport, without live email.</summary>
/// <param name="output">Reports bounded synthetic diagnostic measurements, never message bodies or resource content.</param>
public sealed class ProducerEmailTests(ITestOutputHelper output)
{
    /// <summary>Real private invitation uses saved branding, reply-to and exact override revision without altering the trusted recipient.</summary>
    [Fact]
    public async Task RealInvitationProducerUsesBusinessSettingsAndVersionedTemplate()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var attendee = await s.AddUserAsync("recipient", registered: true);
        var questId = await s.CreateQuestAsync(s.Manager, QuestVisibility.Private);
        var email = await AdministrationAsync(s);
        await SaveSettingsAsync(email, "Engineering & Games", "responses@example.invalid");
        var template = BusinessEmailRules.Default("quest.invitation") with
        {
            Subject = "{{Brand}} — invitation",
            HtmlBody = "<h2>{{Brand}}</h2><p>{{Summary}}</p><p>{{CalendarGuidance}}</p>",
            TextBody = "{{Brand}}\n{{Summary}}\n{{CalendarGuidance}}"
        };
        Assert.Equal(1, await email.SaveTemplateAsync(template));
        s.ActAs(s.Manager);
        await s.Quests.InviteAsync(questId, attendee.Id);
        await s.FlushAsync();
        var message = Assert.Single(s.Gateway.Messages);
        Assert.Equal(attendee.Email, message.Recipient);
        Assert.Equal("responses@example.invalid", message.ReplyTo);
        Assert.Equal("Engineering & Games — invitation", message.Subject);
        Assert.StartsWith("<h2>Engineering &amp; Games</h2><p>You have a Quest invitation.", message.HtmlBody, StringComparison.Ordinal);
        Assert.StartsWith("Engineering & Games\nYou have a Quest invitation.", message.TextBody, StringComparison.Ordinal);
        Assert.Null(message.CalendarContent);
        await using var db = s.Read();
        var saved = CoreCompositionScenario.Payload<DeliveryPayload>((await db.NotificationDeliveries.SingleAsync()).PayloadJson);
        Assert.Equal(1, saved.BusinessEmail!.Revision);
        Assert.Equal("quest.invitation", saved.BusinessEmail.Key);
        Assert.Equal(message.Subject, saved.BusinessEmail.Subject);
        Assert.Equal(message.HtmlBody, saved.BusinessEmail.HtmlBody);
        Assert.Equal(message.TextBody, saved.BusinessEmail.TextBody);
    }

    /// <summary>After an uncertain real join submission, changed settings/templates cannot rewrite the retry, calendar bytes or stable key.</summary>
    [Fact]
    public async Task UncertainJoinRetryFreezesTemplateReplyToAndCalendarIntent()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var attendee = await s.AddUserAsync("attendee", registered: true);
        var questId = await s.CreateQuestAsync(s.Manager, QuestVisibility.Private);
        s.ActAs(s.Manager);
        await s.Quests.InviteAsync(questId, attendee.Id);
        await s.FlushAsync();
        s.Gateway.Messages.Clear();
        var email = await AdministrationAsync(s);
        await SaveSettingsAsync(email, "Original brand", "original@example.invalid");
        var template = BusinessEmailRules.Default("quest.joined") with { Subject = "{{Brand}} joined" };
        await email.SaveTemplateAsync(template);
        await s.ParticipateAsync(questId, attendee, ParticipationCommand.Join);
        await s.DrainAsync("outbox");
        s.Gateway.Failure = new DeliveryTransportException(TransportOutcome.Uncertain, "Synthetic timeout");
        Assert.True(await s.Runner.RunOnceAsync("delivery"));
        var first = Assert.Single(s.Gateway.Messages);
        Assert.Equal("Original brand joined", first.Subject);
        Assert.Equal("original@example.invalid", first.ReplyTo);
        Assert.Equal("REQUEST", first.CalendarMethod);
        Assert.Contains("ORGANIZER:mailto:organizer@example.invalid", first.CalendarContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("original@example.invalid", first.CalendarContent, StringComparison.Ordinal);
        await SaveSettingsAsync(email, "Later brand", "later@example.invalid");
        await email.SaveTemplateAsync(template with { Revision = 1, Subject = "Changed {{Brand}} joined" });
        await using (var db = s.Read())
        {
            var delivery = await db.NotificationDeliveries.SingleAsync(x => x.Status == WorkStatus.Pending);
            s.Clock.Now = delivery.DueUtc;
            Assert.Equal(1, CoreCompositionScenario.Payload<DeliveryPayload>(delivery.PayloadJson).BusinessEmail!.Revision);
        }
        s.Gateway.Failure = null;
        Assert.True(await s.Runner.RunOnceAsync("delivery"));
        Assert.Equal(2, s.Gateway.Messages.Count);
        Assert.Equal(first, s.Gateway.Messages[1]);
        await using var verify = s.Read();
        var calendar = await verify.CalendarDeliveryStates.SingleAsync();
        Assert.Equal(calendar.IntendedSequence, calendar.SentSequence);
        Assert.True(calendar.MayHaveBeenDelivered);
    }

    /// <summary>Persists a real joined-email/calendar Unicode snapshot larger than 10,000 serialized characters and retries its exact SQL-reloaded content after configuration changes.</summary>
    [Fact]
    public async Task LargeUnicodeFrozenPayloadPersistsAndRetriesExactProducerIntent()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var attendee = await s.AddUserAsync("unicode-attendee", registered: true);
        var questInput = CoreCompositionScenario.QuestInput(QuestVisibility.Private) with
        {
            Title = "交流会 — café",
            Description = "会議の説明 — české setkání",
            Location = "会議室 — Praha"
        };
        s.ActAs(s.Manager);
        var questId = await s.Quests.CreateAsync(s.EventId, questInput);
        await s.Quests.ChangeStatusAsync(questId, await s.QuestVersionAsync(questId), QuestStatus.Active, "");
        await s.Quests.InviteAsync(questId, attendee.Id);
        await s.FlushAsync();
        s.Gateway.Messages.Clear();

        var email = await AdministrationAsync(s);
        const string brand = "交流 — Sidequest";
        const string replyTo = "unicode-replies@example.invalid";
        await SaveSettingsAsync(email, brand, replyTo);
        var unicodeWording = new string('界', 1000);
        var template = BusinessEmailRules.Default("quest.joined") with
        {
            Subject = "{{Brand}} — joined",
            HtmlBody = "<p>" + unicodeWording + "</p><p>{{Brand}}</p><p>{{Summary}}</p><p>{{CalendarGuidance}}</p>",
            TextBody = unicodeWording + "\n{{Brand}}\n{{Summary}}\n{{CalendarGuidance}}"
        };
        Assert.Equal(1, await email.SaveTemplateAsync(template));
        var expectedEmail = BusinessEmailRules.Render(template with { Revision = 1 }, NotificationKind.Joined, brand, replyTo);
        await s.ParticipateAsync(questId, attendee, ParticipationCommand.Join);
        await s.DrainAsync("outbox");

        Guid deliveryId;
        string logicalKey;
        string sourceIntent;
        CalendarSnapshot capturedCalendar;
        await using (var staged = s.Read())
        {
            var row = await staged.NotificationDeliveries.AsNoTracking().SingleAsync(x => x.Status == WorkStatus.Pending);
            var payload = CoreCompositionScenario.Payload<DeliveryPayload>(row.PayloadJson);
            Assert.Null(payload.BusinessEmail);
            Assert.True(row.PayloadJson.Length < 10000, $"Pre-render intent was unexpectedly {row.PayloadJson.Length} characters.");
            deliveryId = row.Id;
            logicalKey = row.DeduplicationKey;
            sourceIntent = JsonSerializer.Serialize(payload.Change);
            capturedCalendar = Assert.IsType<CalendarSnapshot>(payload.Calendar);
            Assert.Equal(questInput.Title, capturedCalendar.Title);
            Assert.Equal(questInput.Description, capturedCalendar.Description);
            Assert.Equal(questInput.Location, capturedCalendar.Location);
        }

        s.Gateway.Failure = new DeliveryTransportException(TransportOutcome.Uncertain, "Synthetic Unicode submission timeout");
        Assert.True(await s.Runner.RunOnceAsync("delivery"));
        var first = Assert.Single(s.Gateway.Messages);
        Assert.Equal(expectedEmail.Subject, first.Subject);
        Assert.Equal(expectedEmail.HtmlBody, first.HtmlBody);
        Assert.Equal(expectedEmail.TextBody, first.TextBody);
        Assert.Equal(replyTo, first.ReplyTo);
        Assert.Equal(attendee.Email, first.Recipient);
        Assert.Equal(logicalKey, first.IdempotencyKey);
        Assert.Equal("REQUEST", first.CalendarMethod);
        Assert.Contains("ORGANIZER:mailto:organizer@example.invalid", first.CalendarContent, StringComparison.OrdinalIgnoreCase);

        string frozenJson;
        DateTimeOffset retryDue;
        await using (var reloaded = s.Read())
        {
            var row = await reloaded.NotificationDeliveries.AsNoTracking().SingleAsync(x => x.Id == deliveryId);
            frozenJson = row.PayloadJson;
            output.WriteLine("SQL-reloaded frozen payload length: {0} characters.", frozenJson.Length);
            Assert.True(frozenJson.Length > 10000, $"The persisted regression payload must exceed 10,000 characters; actual: {frozenJson.Length}.");
            var frozen = CoreCompositionScenario.Payload<DeliveryPayload>(frozenJson);
            Assert.Equal(frozenJson, JsonSerializer.Serialize(frozen));
            Assert.Equal(expectedEmail, frozen.BusinessEmail);
            Assert.Equal(capturedCalendar, frozen.Calendar);
            Assert.Equal(first.CalendarContent, frozen.CalendarContent);
            Assert.Equal(sourceIntent, JsonSerializer.Serialize(frozen.Change));
            Assert.Equal(WorkStatus.Pending, row.Status);
            Assert.Equal(1, row.Attempts);
            Assert.Null(row.ProviderMessageId);
            retryDue = row.DueUtc;
        }

        await SaveSettingsAsync(email, "Later valid brand", "later-replies@example.invalid");
        Assert.Equal(2, await email.SaveTemplateAsync(BusinessEmailRules.Default("quest.joined") with { Revision = 1 }));
        await using (var changed = s.Read())
        {
            Assert.Equal(frozenJson, (await changed.NotificationDeliveries.AsNoTracking().SingleAsync(x => x.Id == deliveryId)).PayloadJson);
            var original = await changed.NotificationTemplates.AsNoTracking().SingleAsync(x => x.Key == template.Key && x.Revision == 1);
            Assert.Equal(template.Subject, original.Subject);
            Assert.Equal(template.HtmlBody, original.HtmlBody);
            Assert.Equal(template.TextBody, original.TextBody);
            Assert.Equal(2, await changed.NotificationTemplates.CountAsync(x => x.Key == template.Key));
        }

        s.Clock.Now = retryDue;
        s.Gateway.Failure = null;
        Assert.True(await s.Runner.RunOnceAsync("delivery"));
        Assert.Equal(2, s.Gateway.Messages.Count);
        Assert.Equal(first, s.Gateway.Messages[1]);
        await using var completed = s.Read();
        var accepted = await completed.NotificationDeliveries.AsNoTracking().SingleAsync(x => x.Id == deliveryId);
        Assert.Equal(logicalKey, accepted.DeduplicationKey);
        Assert.Equal(frozenJson, accepted.PayloadJson);
        Assert.Equal(WorkStatus.Completed, accepted.Status);
        Assert.Equal("synthetic-provider-receipt", accepted.ProviderMessageId);
        var calendar = await completed.CalendarDeliveryStates.AsNoTracking().SingleAsync(x => x.QuestId == questId && x.UserId == attendee.Id);
        Assert.Equal(capturedCalendar.Sequence, calendar.IntendedSequence);
        Assert.Equal(capturedCalendar.Sequence, calendar.SentSequence);
        Assert.Equal(capturedCalendar.Method, calendar.IntendedMethod);
        Assert.True(calendar.MayHaveBeenDelivered);
    }

    /// <summary>Real removal/leave producers use overridden service wording but cannot reintroduce protected details into withdrawal email or calendars.</summary>
    /// <param name="operation">Actual business command that ends access or attendance.</param>
    [Theory]
    [InlineData("membership")]
    [InlineData("invitation")]
    [InlineData("attendee")]
    [InlineData("leave")]
    public async Task RealRemovalContextsKeepOverrideAndCalendarWithdrawalRedacted(string operation)
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var attendee = await s.AddUserAsync("private-attendee", registered: true);
        var questId = await s.CreateQuestAsync(s.Manager, QuestVisibility.Private);
        s.ActAs(s.Manager);
        await s.Quests.InviteAsync(questId, attendee.Id);
        await s.ParticipateAsync(questId, attendee, ParticipationCommand.Join);
        await s.FlushAsync();
        var invitation = Assert.Single(s.Gateway.Messages, x => x.CalendarMethod == "REQUEST");
        var uid = CalendarLine(invitation.CalendarContent!, "UID:");
        var previousSequence = long.Parse(CalendarLine(invitation.CalendarContent!, "SEQUENCE:")["SEQUENCE:".Length..],
            System.Globalization.CultureInfo.InvariantCulture);
        s.Gateway.Messages.Clear();
        var email = await AdministrationAsync(s);
        await SaveSettingsAsync(email, "Access desk", "desk@example.invalid");
        var kind = operation switch
        {
            "attendee" => NotificationKind.AttendeeRemoved,
            "leave" => NotificationKind.Left,
            _ => NotificationKind.AccessRemoved
        };
        var template = BusinessEmailRules.Default(BusinessEmailRules.KeyFor(kind)) with
        {
            Subject = "{{Brand}} — service notice",
            HtmlBody = "<p><strong>{{Brand}}</strong></p><p>{{Summary}}</p><p>{{CalendarGuidance}}</p>",
            TextBody = "{{Brand}}\n{{Summary}}\n{{CalendarGuidance}}"
        };
        await email.SaveTemplateAsync(template);
        s.ActAs(s.Manager);
        switch (operation)
        {
            case "membership": await s.Events.RemoveMemberAsync(s.EventId, attendee.Id, "Protected removal reason sentinel"); break;
            case "invitation": await s.Quests.RevokeInvitationAsync(questId, attendee.Id, "Protected removal reason sentinel"); break;
            case "attendee": await s.Quests.RemoveAttendeeAsync(questId, attendee.Id, "Protected removal reason sentinel"); break;
            default: await s.ParticipateAsync(questId, attendee, ParticipationCommand.Leave); break;
        }
        await s.FlushAsync();
        var withdrawal = Assert.Single(s.Gateway.Messages, x => x.CalendarMethod == "CANCEL");
        Assert.Equal(attendee.Email, withdrawal.Recipient);
        Assert.Equal("Access desk — service notice", withdrawal.Subject);
        Assert.Equal("desk@example.invalid", withdrawal.ReplyTo);
        Assert.Contains(NotificationRules.Summary(kind), withdrawal.TextBody, StringComparison.Ordinal);
        foreach (var secret in new[] { "Private title sentinel", "Private description sentinel", "Private room sentinel", "Protected removal reason sentinel" })
        {
            Assert.DoesNotContain(secret, withdrawal.Subject, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, withdrawal.TextBody, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, withdrawal.HtmlBody, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, withdrawal.CalendarContent, StringComparison.Ordinal);
        }
        Assert.Equal(uid, CalendarLine(withdrawal.CalendarContent!, "UID:"));
        Assert.True(long.Parse(CalendarLine(withdrawal.CalendarContent!, "SEQUENCE:")["SEQUENCE:".Length..],
            System.Globalization.CultureInfo.InvariantCulture) > previousSequence);
        Assert.Contains("ORGANIZER:mailto:organizer@example.invalid", withdrawal.CalendarContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("desk@example.invalid", withdrawal.CalendarContent, StringComparison.Ordinal);
        await using var db = s.Read();
        Assert.Equal(ParticipationStatus.None, (await db.Participations.SingleAsync(x => x.UserId == attendee.Id)).Status);
        var state = await db.CalendarDeliveryStates.SingleAsync(x => x.UserId == attendee.Id);
        Assert.Equal("CANCEL", state.IntendedMethod);
        Assert.Equal(state.IntendedSequence, state.SentSequence);
        Assert.False(state.MayHaveBeenDelivered);
    }

    /// <summary>Actual reminder scheduling/handler delivery uses business settings while preserving the joined-only, no-calendar reminder policy.</summary>
    [Fact]
    public async Task RealReminderHandlerUsesTemplateWithoutChangingCalendarPolicy()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var attendee = await s.AddUserAsync("reminder-attendee", registered: true);
        var questId = await s.CreateQuestAsync(s.Manager, QuestVisibility.Private);
        s.ActAs(s.Manager);
        await s.Quests.InviteAsync(questId, attendee.Id);
        await s.ParticipateAsync(questId, attendee, ParticipationCommand.Join);
        await s.FlushAsync();
        s.Gateway.Messages.Clear();
        var email = await AdministrationAsync(s);
        await SaveSettingsAsync(email, "Reminder brand", "reminders@example.invalid");
        await email.SaveTemplateAsync(BusinessEmailRules.Default("quest.reminder") with { Subject = "{{Brand}} starts soon" });
        Assert.True(await s.Runner.RunOnceAsync("scheduled"));
        await s.DrainAsync("delivery");
        var reminder = Assert.Single(s.Gateway.Messages);
        Assert.Equal("Reminder brand starts soon", reminder.Subject);
        Assert.Equal("reminders@example.invalid", reminder.ReplyTo);
        Assert.Equal(attendee.Email, reminder.Recipient);
        Assert.Null(reminder.CalendarContent);
        Assert.Null(reminder.CalendarMethod);
        Assert.Contains("Your joined Quest starts soon.", reminder.TextBody, StringComparison.Ordinal);
        await using var db = s.Read();
        Assert.Single(await db.Notifications.Where(x => x.Kind == NotificationKind.Reminder && x.UserId == attendee.Id).ToArrayAsync());
        Assert.Equal("REQUEST", (await db.CalendarDeliveryStates.SingleAsync()).IntendedMethod);
    }

    /// <summary>An invalid persisted override produces a real dead letter with no recording-provider call, while retaining the in-app service record.</summary>
    [Fact]
    public async Task InvalidStoredTemplateFailsActualProducerDeliveryExplicitly()
    {
        await using var s = await CoreCompositionScenario.CreateAsync();
        var attendee = await s.AddUserAsync("invalid-template-recipient", registered: true);
        var questId = await s.CreateQuestAsync(s.Manager, QuestVisibility.Private);
        await using (var db = s.Read())
        {
            var template = BusinessEmailRules.Default("quest.invitation");
            db.NotificationTemplates.Add(new NotificationTemplate
            {
                Key = template.Key,
                Revision = 1,
                Subject = "{{Quest.Title}}",
                HtmlBody = template.HtmlBody,
                TextBody = template.TextBody,
                ChangedById = s.Manager.Id,
                ChangedUtc = s.Clock.Now
            });
            await db.SaveChangesAsync();
        }
        s.ActAs(s.Manager);
        await s.Quests.InviteAsync(questId, attendee.Id);
        await s.DrainAsync("outbox");
        Assert.True(await s.Runner.RunOnceAsync("delivery"));
        Assert.Empty(s.Gateway.Messages);
        await using var verify = s.Read();
        var failed = Assert.Single(await verify.NotificationDeliveries.ToArrayAsync());
        Assert.Equal(WorkStatus.DeadLetter, failed.Status);
        Assert.Null(failed.ProviderMessageId);
        Assert.Equal("Permanent dependency/configuration or payload failure; correct before replay.", failed.LastError);
        Assert.Single(await verify.Notifications.ToArrayAsync());
        var email = await AdministrationAsync(s);
        var editor = await email.GetHistoryAsync("quest.invitation", allowInvalidForEditing: true);
        await email.SaveTemplateAsync(BusinessEmailRules.Default("quest.invitation") with { Revision = editor[0].Revision });
        s.ActAs(s.Manager);
        await s.Notifications.ReplayAsync(failed.Id, "delivery");
        await s.DrainAsync("delivery");
        var repaired = Assert.Single(s.Gateway.Messages);
        Assert.Equal(failed.DeduplicationKey, repaired.IdempotencyKey);
        Assert.Equal(attendee.Email, repaired.Recipient);
        await using var replayed = s.Read();
        var completed = await replayed.NotificationDeliveries.SingleAsync();
        Assert.Equal(failed.Id, completed.Id);
        Assert.Equal(WorkStatus.Completed, completed.Status);
        Assert.Equal(2, CoreCompositionScenario.Payload<DeliveryPayload>(completed.PayloadJson).BusinessEmail!.Revision);
    }

    private static string CalendarLine(string calendar, string prefix) =>
        Assert.Single(calendar.Split("\r\n"), x => x.StartsWith(prefix, StringComparison.Ordinal));

    private static async Task<BusinessEmailService> AdministrationAsync(CoreCompositionScenario scenario)
    {
        await using var db = scenario.Read();
        if (!await db.Administrators.AnyAsync(x => x.UserId == scenario.Manager.Id))
        {
            db.Administrators.Add(new Administrator { UserId = scenario.Manager.Id });
            await db.SaveChangesAsync();
        }
        return new(new CompositionContextFactory(scenario), new ResourceAccess(StubCurrentUser.For(scenario.Manager)), scenario.Clock);
    }

    private static async Task SaveSettingsAsync(BusinessEmailService email, string brand, string replyTo)
    {
        var settings = await email.GetSettingsAsync();
        await email.SaveSettingAsync(settings.Single(x => x.Key == BusinessEmailRules.BrandKey) with { Value = brand });
        await email.SaveSettingAsync(settings.Single(x => x.Key == BusinessEmailRules.ReplyToKey) with { Value = replyTo });
    }

    private sealed class CompositionContextFactory(CoreCompositionScenario scenario) : ISidequestDbContextFactory
    {
        /// <inheritdoc />
        public Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ISidequestDbContext>(scenario.Read());
        }
    }
}
