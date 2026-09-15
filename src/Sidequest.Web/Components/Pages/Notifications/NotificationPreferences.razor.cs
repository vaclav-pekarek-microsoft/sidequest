using Microsoft.AspNetCore.Components;
using Sidequest.Application.Events;
using Sidequest.Application.Notifications;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Notifications;

namespace Sidequest.Web.Components.Pages.Notifications;

/// <summary>Optional settings whose current access is rechecked on reconnect without overwriting valid unsaved input.</summary>
public partial class NotificationPreferences
{
    [Inject] private INotificationService Service { get; set; } = default!;
    [Inject] private IEventService Events { get; set; } = default!;

    /// <summary>Optional Event context for an explicit publication-email override; current membership is required.</summary>
    [Parameter] public Guid? EventId { get; set; }

    private NotificationPreferenceForm? model;
    private bool eventEnabled;

    /// <inheritdoc />
    protected override Guid? ContextId => EventId;

    /// <inheritdoc />
    protected override async Task QueryAsync(CancellationToken token)
    {
        var requestedEvent = EventId;
        var preferences = await Service.GetPreferencesAsync(token);
        token.ThrowIfCancellationRequested();
        if (requestedEvent != EventId)
            return;
        if (requestedEvent is { } eventId)
        {
            var current = await Events.GetAsync(eventId, token);
            token.ThrowIfCancellationRequested();
            if (requestedEvent != EventId)
                return;
            if (!current.Summary.IsMember)
                throw new DomainException(ErrorCode.NotFound, "This Event is unavailable.");
        }
        model ??= new()
        {
            NewQuestEmail = preferences.NewQuestEmail,
            ActivityEmail = preferences.ActivityEmail,
            RemindersEnabled = preferences.RemindersEnabled,
            ReminderHours = preferences.ReminderHours,
            TimeZoneId = preferences.TimeZoneId
        };
    }

    private Task SaveAsync()
    {
        if (model is not { } edited)
            return Task.CompletedTask;
        return MutateAsync(async token =>
        {
            Status = "";
            await Service.SavePreferencesAsync(new(edited.NewQuestEmail, edited.ActivityEmail, edited.RemindersEnabled,
                edited.ReminderHours, string.IsNullOrWhiteSpace(edited.TimeZoneId) ? null : edited.TimeZoneId.Trim()), token);
            token.ThrowIfCancellationRequested();
            Status = "Preferences saved. Obsolete reminders were replaced; required service delivery remains enabled.";
        });
    }

    private Task SaveEventAsync()
    {
        if (EventId is not { } eventId)
            return Task.CompletedTask;
        return MutateAsync(async token =>
        {
            await Service.SetEventNewQuestEmailAsync(eventId, eventEnabled, token);
            token.ThrowIfCancellationRequested();
            Status = "Event override saved.";
        });
    }

    /// <inheritdoc />
    protected override void ClearProtectedState()
    {
        model = null;
        eventEnabled = false;
    }
}
