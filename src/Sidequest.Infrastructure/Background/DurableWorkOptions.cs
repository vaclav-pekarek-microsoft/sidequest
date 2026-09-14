namespace Sidequest.Infrastructure.Background;

/// <summary>Bounded polling and lease settings; invalid values fail worker construction conspicuously.</summary>
public sealed class DurableWorkOptions
{
    /// <summary>Healthy idle poll period, from one through thirty seconds.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Lease duration, from thirty seconds through two minutes.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(90);
    /// <summary>Maximum reminder lateness; overdue reminders are suppressed rather than sent stale.</summary>
    public TimeSpan ReminderLateness { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Independent bounded worker loops; defaults to four so a slow provider does not block all scheduled work.</summary>
    public int Concurrency { get; set; } = 4;

    /// <summary>Rejects settings that violate bounded recovery and reminder timing.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A duration is outside its supported bounds.</exception>
    public void Validate()
    {
        if (PollInterval < TimeSpan.FromSeconds(1) || PollInterval > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        if (LeaseDuration < TimeSpan.FromSeconds(30) || LeaseDuration > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
        if (ReminderLateness <= TimeSpan.Zero || ReminderLateness > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(ReminderLateness));
        if (Concurrency is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(Concurrency));
    }
}
