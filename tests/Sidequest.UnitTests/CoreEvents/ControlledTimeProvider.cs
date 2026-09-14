namespace Sidequest.UnitTests.CoreEvents;

internal sealed class ControlledTimeProvider : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
    internal TaskCompletionSource<TimeSpan> TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? elapsed;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        elapsed = () => callback(state);
        TimerCreated.TrySetResult(dueTime);
        return new TimerHandle();
    }

    internal void Elapse(TimeSpan amount)
    {
        Now += amount;
        elapsed!();
    }

    private sealed class TimerHandle : ITimer
    {
        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        /// <inheritdoc />
        public void Dispose() { }
        /// <inheritdoc />
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
