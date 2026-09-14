namespace Sidequest.Domain.Model;

/// <summary>One-time individual operation applied to a frozen group expansion snapshot.</summary>
public enum BulkMode
{
    /// <summary>Add eligible individuals directly, without silently restoring removed membership.</summary>
    Add,
    /// <summary>Create individual Event invitations requiring consent.</summary>
    Invite
}
