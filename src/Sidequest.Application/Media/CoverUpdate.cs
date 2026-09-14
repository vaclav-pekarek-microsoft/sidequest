namespace Sidequest.Application.Media;

/// <summary>Reports the committed cover assignment without exposing storage internals.</summary>
/// <param name="AssetId">Ready assigned cover identifier, or null after removal.</param>
/// <param name="Version">Current opaque Quest rowversion to retain in the editor for its next mutation.</param>
public sealed record CoverUpdate(Guid? AssetId, byte[] Version);
