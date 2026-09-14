using Microsoft.Extensions.Logging.Abstractions;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Infrastructure.Delivery;

namespace Sidequest.UnitTests.CoreDelivery;

/// <summary>Safe ACS configuration failure checks; deliberately never supplies a live endpoint or credential.</summary>
public sealed class AcsEmailGatewayTests
{
    /// <summary>The default synthetic host can construct the adapter, but affected sends fail permanently without claiming acceptance.</summary>
    [Fact]
    public async Task MissingConfiguration_ConstructsButSendFailsPermanently()
    {
        var gateway = new AcsEmailGateway(new(), NullLogger<AcsEmailGateway>.Instance);
        var error = await Assert.ThrowsAsync<DeliveryTransportException>(() =>
            gateway.SendAsync(new EmailMessage("recipient@example.invalid", "Subject", "safe", "safe", "stable")));
        Assert.Equal(TransportOutcome.Permanent, error.Outcome);
        Assert.Contains("configuration", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
