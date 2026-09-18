using Microsoft.AspNetCore.Components;
using Sidequest.Application.Quests;

namespace Sidequest.Web.Components.Quests;

/// <summary>Collects draft or published configuration, keeping parent-owned inputs immutable.</summary>
public partial class QuestEditor
{
    private QuestEditorModel model = new();
    private QuestInput? previous;

    /// <summary>Initial configuration copied locally when the parent supplies a new snapshot.</summary>
    [Parameter, EditorRequired] public QuestInput Initial { get; set; } = default!;
    /// <summary>Read-only inherited Event zone label.</summary>
    [Parameter, EditorRequired] public string ZoneId { get; set; } = "Etc/UTC";
    /// <summary>Prevents changing visibility after publication.</summary>
    [Parameter] public bool Published { get; set; }
    /// <summary>Disables duplicate submission while a service operation is pending.</summary>
    [Parameter] public bool Busy { get; set; }
    /// <summary>Submits copied form data upward; only the application service may authorize or persist it.</summary>
    [Parameter] public EventCallback<QuestEditorModel> Save { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (ReferenceEquals(previous, Initial))
            return;
        previous = Initial;
        model = new()
        {
            Title = Initial.Title, Description = Initial.Description, Location = Initial.Location,
            Capacity = Initial.SuggestedCapacity, Start = Initial.StartLocal, End = Initial.EndLocal,
            Visibility = Initial.Visibility,
            StartOffset = Initial.StartOffset,
            EndOffset = Initial.EndOffset
        };
    }

    private Task SaveAsync() => Save.InvokeAsync(model);
}
