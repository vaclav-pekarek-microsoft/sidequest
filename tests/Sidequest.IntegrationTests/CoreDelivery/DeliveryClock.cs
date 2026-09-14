namespace Sidequest.IntegrationTests.CoreDelivery;

internal sealed class DeliveryClock(DateTimeOffset now) : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = now;

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => Now;
}
