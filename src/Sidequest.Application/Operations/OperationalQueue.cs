namespace Sidequest.Application.Operations;

/// <summary>Closed set of durable queues permitted as metric dimensions; no resource identifiers are accepted.</summary>
public enum OperationalQueue
{
    /// <summary>Durable domain changes awaiting dispatch.</summary>
    Outbox,
    /// <summary>Timed lifecycle, reminder and other scheduled work.</summary>
    Scheduled,
    /// <summary>Per-recipient durable delivery attempts.</summary>
    Delivery
}
