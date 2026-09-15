namespace Sidequest.Web.Operations;

/// <summary>Immutable, opt-in sampling policy; configuration changes require a host restart.</summary>
public sealed class OperationalMonitoringOptions
{
    /// <summary>Creates a policy with a validated interval even when sampling is disabled.</summary>
    /// <param name="enabled">Whether the host should sample SQL; defaults to false.</param>
    /// <param name="sampleInterval">Interval between completed attempts, from five seconds through five minutes; null selects thirty seconds.</param>
    /// <exception cref="ArgumentOutOfRangeException">The interval is outside the inclusive supported bounds.</exception>
    public OperationalMonitoringOptions(bool enabled = false, TimeSpan? sampleInterval = null)
    {
        SampleInterval = sampleInterval ?? TimeSpan.FromSeconds(30);
        if (SampleInterval < TimeSpan.FromSeconds(5) || SampleInterval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(sampleInterval), "Sampling interval must be between five seconds and five minutes.");
        Enabled = enabled;
    }

    /// <summary>Whether SQL observation is explicitly enabled; this does not configure an exporter.</summary>
    public bool Enabled { get; }

    /// <summary>Delay after each completed attempt, avoiding overlapping samples even when SQL is slow.</summary>
    public TimeSpan SampleInterval { get; }

    /// <summary>Maximum age of a usable observation, in elapsed time; twice the configured sampling interval.</summary>
    public TimeSpan StaleAfter => SampleInterval * 2;
}
