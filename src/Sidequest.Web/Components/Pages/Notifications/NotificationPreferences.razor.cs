using Microsoft.AspNetCore.Components;
using Sidequest.Application.Notifications;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Notifications;

namespace Sidequest.Web.Components.Pages.Notifications;

/// <summary>Per-page interactive optional settings; every save calls the authorized application boundary.</summary>
public partial class NotificationPreferences : IAsyncDisposable
{
    [Inject] private INotificationService Service { get; set; } = default!;
    /// <summary>Optional Event context for explicitly setting its publication-email override; does not grant membership.</summary>
    [Parameter] public Guid? EventId { get; set; }
    private readonly CancellationTokenSource lifetime = new();
    private NotificationPreferenceForm? model;
    private bool busy;
    private bool eventEnabled;
    private string? error;
    private string status = "";

    /// <inheritdoc/>
    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        busy = true;
        model = null;
        error = null;
        try
        {
            var p = await Service.GetPreferencesAsync(lifetime.Token);
            model = new() { NewQuestEmail = p.NewQuestEmail, ActivityEmail = p.ActivityEmail,
                RemindersEnabled = p.RemindersEnabled, ReminderHours = p.ReminderHours, TimeZoneId = p.TimeZoneId };
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { error = "Preferences are unavailable. Check your session and retry."; }
        finally { busy = false; }
    }

    private async Task SaveAsync()
    {
        if (busy || model is null)
            return;
        busy = true;
        error = null;
        status = "";
        try
        {
            await Service.SavePreferencesAsync(new(model.NewQuestEmail, model.ActivityEmail, model.RemindersEnabled,
                model.ReminderHours, string.IsNullOrWhiteSpace(model.TimeZoneId) ? null : model.TimeZoneId.Trim()), lifetime.Token);
            status = "Preferences saved. Obsolete reminders were replaced; required service delivery remains enabled.";
        }
        catch (DomainException e) when (e.Code == ErrorCode.Validation) { error = e.Message; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { model = null; error = "Preferences were not saved. Reload to check current access."; }
        finally { busy = false; }
    }

    private async Task SaveEventAsync()
    {
        if (busy || EventId is not Guid eventId)
            return;
        busy = true;
        error = null;
        try
        {
            await Service.SetEventNewQuestEmailAsync(eventId, eventEnabled, lifetime.Token);
            status = "Event override saved.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { model = null; error = "Event override was not saved. Current Event access is required."; }
        finally { busy = false; }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        lifetime.Dispose();
    }
}
