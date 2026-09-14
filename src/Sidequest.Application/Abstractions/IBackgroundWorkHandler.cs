namespace Sidequest.Application.Abstractions;

/// <summary>Handler for durable leased work; implementations must tolerate replay and recheck applicable access and lifecycle rules.</summary>
public interface IBackgroundWorkHandler
{
    /// <summary>Versioned discriminator identifying the durable work this handler can process.</summary>
    public string WorkType { get; }
    /// <summary>Processes the identified persisted work item without assuming exactly-once execution.</summary>
    /// <param name="workId">Internal identifier of the claimed durable work record.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation, including host shutdown; unfinished work must remain recoverable.</param>
    /// <returns>A task completing when handling finishes; faults must remain visible to retry/dead-letter orchestration.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed before handling completes.</exception>
    public Task ExecuteAsync(Guid workId, CancellationToken cancellationToken);
}
