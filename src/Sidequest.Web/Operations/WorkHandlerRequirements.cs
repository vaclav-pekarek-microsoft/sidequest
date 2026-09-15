namespace Sidequest.Web.Operations;

/// <summary>Declares optional host features whose durable handlers must exist before polling starts.</summary>
/// <param name="RequireMediaCleanup">Whether the composed host includes Media and must have exactly one media-cleanup handler.
/// When false, an accidental media handler registration is rejected rather than silently enabling the feature.</param>
public sealed record WorkHandlerRequirements(bool RequireMediaCleanup = false);
