namespace Sidequest.Application.Abstractions;

/// <summary>External email transport boundary invoked outside SQL transactions after durable delivery intent is committed.</summary>
public interface IEmailGateway
{
    /// <summary>Submits one recipient's message and exposes failure rather than reporting placeholder success.</summary>
    /// <param name="message">Authorized, rendered message with a stable logical delivery key.</param>
    /// <param name="cancellationToken">Cancels waiting/transport work; cancellation cannot prove the provider received nothing.</param>
    /// <returns>Provider acceptance receipt; mailbox delivery and exactly-once submission are not guaranteed.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed; the caller must retain any uncertain delivery outcome.</exception>
    public Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
