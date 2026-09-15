using Microsoft.AspNetCore.Components;
using NodaTime;
using Sidequest.Domain.Rules;
using Sidequest.Web.Experience;

namespace Sidequest.Web.Components.Experience;

/// <summary>Displays inherited Event time first and the actual browser's local equivalent only when known and different.</summary>
public partial class QuestTimeDisplay : IAsyncDisposable
{
    private string? deviceZone;
    [Inject] private ExperienceCoordinator Coordinator { get; set; } = default!;
    /// <summary>IANA time zone inherited from the parent Event, never independently editable.</summary>
    [Parameter, EditorRequired] public string ZoneId { get; set; } = "Etc/UTC";
    /// <summary>UTC inclusive activity start instant.</summary>
    [Parameter] public DateTimeOffset StartUtc { get; set; }
    /// <summary>UTC exclusive activity end instant.</summary>
    [Parameter] public DateTimeOffset EndUtc { get; set; }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        ReadZone();
        Coordinator.Changed += UpdateAsync;
    }

    private void ReadZone() => deviceZone = Coordinator.BrowserTimeZone is { Length: <= 100 } zone &&
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone) is not null ? zone : null;
    private Task UpdateAsync() => InvokeAsync(() => { ReadZone(); StateHasChanged(); });
    private static string Local(DateTimeOffset instant, string zone) =>
        Instant.FromDateTimeOffset(instant).InZone(TimeRules.Zone(zone)).ToString("yyyy-MM-dd HH:mm o<G>", null);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Coordinator.Changed -= UpdateAsync;
        return ValueTask.CompletedTask;
    }
}
