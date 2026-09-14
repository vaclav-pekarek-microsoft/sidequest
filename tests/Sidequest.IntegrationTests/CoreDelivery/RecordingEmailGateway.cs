using Sidequest.Application.Abstractions;

namespace Sidequest.IntegrationTests.CoreDelivery;

internal sealed class RecordingEmailGateway : IEmailGateway
{
    internal List<EmailMessage> Messages { get; } = [];
    internal Exception? Failure { get; set; }
    internal Func<Task>? BeforeReturn { get; set; }

    /// <inheritdoc/>
    public async Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Messages.Add(message);
        if (BeforeReturn is not null)
            await BeforeReturn();
        if (Failure is not null)
            throw Failure;
        return new("synthetic-provider-receipt");
    }
}
