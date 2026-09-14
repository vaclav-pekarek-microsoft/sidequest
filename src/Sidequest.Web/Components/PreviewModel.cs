using System.ComponentModel.DataAnnotations;

namespace Sidequest.Web.Components;

/// <summary>Holds the transient label used to exercise Fluent binding and EditForm validation.</summary>
/// <remarks>Each component owns one mutable model; access it only through that component's renderer context.</remarks>
public sealed class PreviewModel
{
    /// <summary>Gets or sets the required preview label, limited to 80 characters and never persisted.</summary>
    [Required, StringLength(80)]
    public string Label { get; set; } = "";
}
