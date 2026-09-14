namespace Sidequest.Domain.Model;

/// <summary>Namespace of a resource referenced by audit records.</summary>
public enum ResourceKind
{
    /// <summary>An Event aggregate or Event-scoped action.</summary>
    Event,
    /// <summary>A Quest aggregate or Quest-scoped action.</summary>
    Quest,
    /// <summary>An internal user account or account-scoped action.</summary>
    User,
    /// <summary>A global system operation not scoped to Event or Quest content.</summary>
    System
}
