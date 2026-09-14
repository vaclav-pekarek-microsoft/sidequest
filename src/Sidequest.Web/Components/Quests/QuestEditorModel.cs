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
    /// <summary>Optional signed offset in hours for explicit overlap disambiguation.</summary>
    public decimal? StartOffsetHours { get; set; }
    /// <summary>Optional signed offset in hours for explicit overlap disambiguation.</summary>
    public decimal? EndOffsetHours { get; set; }
    /// <summary>Visibility can only be changed while drafting.</summary>
    public QuestVisibility Visibility { get; set; }

    /// <summary>Creates immutable input without inferring an offset for a DST overlap.</summary>
    /// <returns>Application configuration input; ambiguous/gap/containment checks remain server-side.</returns>
    /// <exception cref="DomainException">An offset is outside the supported civil range.</exception>
    public QuestInput ToInput()
    {
        if (StartOffsetHours is < -14 or > 14 || EndOffsetHours is < -14 or > 14)
            throw new DomainException(ErrorCode.Validation, "UTC offsets must be between -14 and 14 hours.", "Offset");
        return new(Title, Description, Location, Capacity, Start, End,
            StartOffsetHours is null ? null : TimeSpan.FromHours((double)StartOffsetHours.Value),
            EndOffsetHours is null ? null : TimeSpan.FromHours((double)EndOffsetHours.Value), Visibility);
    }
}
