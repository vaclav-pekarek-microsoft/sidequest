namespace Sidequest.Application.Operations;

/// <summary>Reports an unavailable operational observation without carrying raw provider errors, content, connection details or healthy fallback data.</summary>
/// <remarks>Caller-requested cancellation is propagated separately as OperationCanceledException, never translated to this failure.</remarks>
public sealed class OperationalObservationException : Exception
{
    /// <summary>Creates a safe typed failure with a fixed generic message and no inner exception.</summary>
    /// <param name="kind">One of the defined, content-free failure classifications.</param>
    /// <exception cref="ArgumentOutOfRangeException">The classification is undefined.</exception>
    public OperationalObservationException(OperationalFailureKind kind)
        : base("Operational observation is unavailable.")
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
    }

    /// <summary>The reason observation is unavailable; does not identify a database, resource, recipient or provider exception.</summary>
    public OperationalFailureKind Kind { get; }
}
