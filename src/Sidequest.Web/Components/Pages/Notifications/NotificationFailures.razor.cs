using Microsoft.AspNetCore.Components;
using Sidequest.Application.Notifications;

namespace Sidequest.Web.Components.Pages.Notifications;

/// <summary>Redacted delivery recovery that rechecks persisted administrator access and discards stale replay confirmations.</summary>
public partial class NotificationFailures
{
    [Inject] private INotificationService Service { get; set; } = default!;
    private IReadOnlyList<DeliveryFailure>? failures;
    private DeliveryFailure? pending;

    /// <inheritdoc />
    protected override async Task QueryAsync(CancellationToken token)
    {
        failures = null;
        pending = null;
        var loaded = await Service.FailedDeliveriesAsync(token);
        token.ThrowIfCancellationRequested();
        failures = loaded;
    }

    private void Review(DeliveryFailure failure)
    {
        if (!ControlsDisabled && failures?.Contains(failure) == true)
            pending = failure;
    }

    private void CancelReview()
    {
        if (!ControlsDisabled)
            pending = null;
    }

    private Task ReplayAsync()
    {
        if (pending is not { } selected)
            return Task.CompletedTask;
        return MutateAsync(async token =>
        {
            pending = null;
            failures = null;
            await Service.ReplayAsync(selected.Id, selected.Kind, token);
            token.ThrowIfCancellationRequested();
            Status = "Replay was queued, not delivered. Current eligibility and ordering will be checked.";
            await QueryAsync(token);
        });
    }

    /// <inheritdoc />
    protected override void ClearProtectedState()
    {
        failures = null;
        pending = null;
    }
}
