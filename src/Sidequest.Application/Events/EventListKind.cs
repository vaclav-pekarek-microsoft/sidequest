namespace Sidequest.Application.Events;

/// <summary>Authorized Event list views separating current membership, discovery, and retained history.</summary>
public enum EventListKind
{
    /// <summary>Current Events associated with the actor's individual membership or ownership.</summary>
    Mine,
    /// <summary>Active Event discovery summaries visible to eligible users without granting full content access.</summary>
    Available,
    /// <summary>Historical Events still readable through the actor's retained access.</summary>
    History
}
