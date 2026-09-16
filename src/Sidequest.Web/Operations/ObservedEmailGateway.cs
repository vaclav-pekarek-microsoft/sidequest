using Sidequest.Application.Abstractions;

namespace Sidequest.Web.Operations;

internal sealed class ObservedEmailGateway(IEmailGateway inner, OperationalActivityMetrics metrics) : IEmailGateway
{
    /// <inheritdoc />
    public Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
        metrics.ObserveAsync(OperationalActivity.EmailSubmission, () => inner.SendAsync(message, cancellationToken), cancellationToken);
}
