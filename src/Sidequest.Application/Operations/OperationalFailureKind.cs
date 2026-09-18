namespace Sidequest.Application.Operations;

/// <summary>Closed, content-free failure classifications for operational observations, independent of provider exception types.</summary>
public enum OperationalFailureKind
{
    /// <summary>The persisted schema version is incompatible with the running application.</summary>
    SchemaMismatch,
    /// <summary>Required schema objects cannot be read because they are absent or incompatible.</summary>
    SchemaUnavailable,
    /// <summary>The persistence dependency rejected or could not complete the observation.</summary>
    StoreUnavailable,
    /// <summary>The observation exceeded a dependency deadline without caller-requested cancellation.</summary>
    Timeout,
    /// <summary>The operational dependency is misconfigured or observation would join an application transaction.</summary>
    Configuration
}
