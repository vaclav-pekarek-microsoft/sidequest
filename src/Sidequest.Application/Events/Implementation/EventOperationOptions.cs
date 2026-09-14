namespace Sidequest.Application.Events.Implementation;

/// <summary>Conservative configurable resource limits for individual requests, invitations, and one-time bulk actions.</summary>
public sealed class EventOperationOptions
{
    /// <summary>Maximum deduplicated recipients in a complete group snapshot; exceeding this adds nobody.</summary>
    public int MaximumBulkRecipients { get; init; } = 5000;

    /// <summary>Maximum bulk starts by one actor in one hour.</summary>
    public int BulkStartsPerHour { get; init; } = 5;

    /// <summary>Maximum requests per actor and Event in one hour, including retained withdrawn/rejected requests.</summary>
    public int RequestsPerHour { get; init; } = 5;

    /// <summary>Maximum individual invitations issued by one actor in one hour, excluding idempotent repeats.</summary>
    public int InvitationsPerHour { get; init; } = 1000;

    /// <summary>Maximum directory searches per actor per minute in this application instance.</summary>
    public int DirectorySearchesPerMinute { get; init; } = 30;

    internal void Validate()
    {
        if (MaximumBulkRecipients is < 1 or > 100000 || BulkStartsPerHour < 1 ||
            RequestsPerHour < 1 || InvitationsPerHour < 1 || DirectorySearchesPerMinute < 1)
            throw new ArgumentException("Event operation limits must be positive and the recipient limit at most 100000.");
    }
}
