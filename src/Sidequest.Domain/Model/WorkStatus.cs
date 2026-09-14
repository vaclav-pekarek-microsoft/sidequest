namespace Sidequest.Domain.Model;

/// <summary>Durable leased-work lifecycle; successful processing does not guarantee mailbox arrival.</summary>
public enum WorkStatus
{
    /// <summary>Awaiting a due initial or retry attempt.</summary>
    Pending,
    /// <summary>Claimed for processing under an expiring lease.</summary>
    Processing,
    /// <summary>Handler completed the logical work.</summary>
    Completed,
    /// <summary>Visible terminal failure awaiting investigation or authorized replay.</summary>
    DeadLetter,
    /// <summary>Obsolete work replaced by a newer intended state.</summary>
    Superseded
}
