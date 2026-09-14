namespace Sidequest.IntegrationTests.CoreQuests;

internal sealed class QuestTestClock(DateTimeOffset now) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = now;
    internal Func<DateTimeOffset>? ReadUtc { get; set; }
    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => ReadUtc?.Invoke() ?? Now;
}
