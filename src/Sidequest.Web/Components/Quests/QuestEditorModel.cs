using System.ComponentModel.DataAnnotations;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Web.Components.Quests;

/// <summary>Editable wall-clock form data; authoritative scheduling validation remains in the application service.</summary>
public sealed class QuestEditorModel
{
    /// <summary>Plain-text title, trimmed and validated again on the server.</summary>
    [Required, StringLength(120, MinimumLength = 3)] public string Title { get; set; } = "";
    /// <summary>Optional plain-text details, never rendered as HTML.</summary>
    [StringLength(10000)] public string Description { get; set; } = "";
    /// <summary>Draft location may be empty; publication requires a location.</summary>
    [StringLength(500)] public string Location { get; set; } = "";
    /// <summary>Advisory capacity only; exceeding it never prevents joining.</summary>
    [Range(1, 10000)] public int? Capacity { get; set; }
    /// <summary>Local start interpreted in the parent Event zone.</summary>
    public DateTime Start { get; set; }
    /// <summary>Local exclusive end interpreted in the parent Event zone.</summary>
    public DateTime End { get; set; }
    /// <summary>Offset mapped from the start occurrence choice, or preserved from the existing instant.</summary>
    public TimeSpan? StartOffset { get; set; }
    /// <summary>Offset mapped from the end occurrence choice, or preserved from the existing instant.</summary>
    public TimeSpan? EndOffset { get; set; }
    /// <summary>Visibility can only be changed while drafting.</summary>
    public QuestVisibility Visibility { get; set; }

    /// <summary>Creates immutable input without inferring an offset for a DST overlap.</summary>
    /// <param name="zoneId">Read-only Event zone used to reject nonexistent or unresolved repeated local times.</param>
    /// <returns>Application configuration input with validated offsets; containment remains server-side.</returns>
    /// <exception cref="DomainException">A local time does not exist, needs an occurrence choice, or has an invalid offset.</exception>
    public QuestInput ToInput(string zoneId)
    {
        ValidateLocal(Start, StartOffset, zoneId, "start");
        ValidateLocal(End, EndOffset, zoneId, "end");
        return new(Title, Description, Location, Capacity, Start, End,
            StartOffset, EndOffset, Visibility);
    }

    private static void ValidateLocal(DateTime local, TimeSpan? offset, string zoneId, string boundary)
    {
        if (QuestLocalTime.Candidates(local, zoneId).Count == 2 && offset is null)
            throw new DomainException(ErrorCode.Validation,
                $"Choose First occurrence or Second occurrence for the repeated {boundary} time.", "LocalTime");
        TimeRules.ToUtc(local, zoneId, offset);
    }
}
