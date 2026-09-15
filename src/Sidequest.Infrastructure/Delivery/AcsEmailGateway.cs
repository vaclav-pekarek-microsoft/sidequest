using System.Net.Mail;
using Azure;
using Azure.Communication.Email;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using ApplicationEmailMessage = Sidequest.Application.Abstractions.EmailMessage;

namespace Sidequest.Infrastructure.Delivery;

/// <summary>Real ACS Email transport with explicit missing-configuration failure and conservative uncertain-outcome handling.</summary>
/// <remarks>ACS acceptance does not guarantee mailbox arrival or exactly-once submission. Calendar interoperability requires approved real Outlook evidence.</remarks>
public sealed class AcsEmailGateway : IEmailGateway
{
    private readonly EmailDeliveryOptions options;
    private readonly EmailClient? client;
    private readonly TimeProvider clock;

    /// <summary>Constructs a lazily authenticated provider client; does not contact Azure or require secrets merely to start the host.</summary>
    /// <param name="options">Secret-store or managed identity deployment configuration.</param>
    /// <param name="logger">Safe configuration diagnostics without settings or secrets.</param>
    /// <param name="clock">UTC time for HTTP-date Retry-After; null selects the system clock.</param>
    public AcsEmailGateway(EmailDeliveryOptions options, ILogger<AcsEmailGateway> logger, TimeProvider? clock = null)
    {
        this.options = options;
        this.clock = clock ?? TimeProvider.System;
        if (!UsableAddress(options.SenderAddress))
        {
            logger.LogError("Email delivery is unconfigured: a verified sender/organizer address is required. Affected deliveries will dead-letter.");
            return;
        }
        var clientOptions = new EmailClientOptions();
        clientOptions.Retry.MaxRetries = 0;
        clientOptions.Diagnostics.IsLoggingContentEnabled = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(options.ConnectionString))
                client = new EmailClient(options.ConnectionString, clientOptions);
            else if (Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps)
            {
                var credential = string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
                    ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
                    : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId));
                client = new EmailClient(endpoint, credential, clientOptions);
            }
        }
        catch (ArgumentException)
        {
            logger.LogError("ACS Email configuration is invalid. Correct deployment settings before replaying failed deliveries.");
        }
        if (client is null)
            logger.LogError("ACS Email is unavailable: configure a secret-store connection string or managed identity endpoint. No messages will be reported as sent.");
    }

    /// <inheritdoc/>
    public async Task<EmailReceipt> SendAsync(ApplicationEmailMessage message, CancellationToken cancellationToken = default)
    {
        if (client is null)
            throw new DeliveryTransportException(TransportOutcome.Permanent, "ACS Email configuration is missing or invalid.");
        if (options.SubmissionTimeout <= TimeSpan.Zero || options.SubmissionTimeout > TimeSpan.FromSeconds(90))
            throw new DeliveryTransportException(TransportOutcome.Permanent, "ACS submission timeout must be positive and at most ninety seconds.");
        if (!UsableAddress(message.Recipient))
            throw new DeliveryTransportException(TransportOutcome.Permanent, "Trusted recipient address is unavailable.");
        if (string.IsNullOrWhiteSpace(message.Subject) || message.Subject.Length > 200 ||
            message.Subject.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029') ||
            (message.ReplyTo is not null && !UsableAddress(message.ReplyTo)))
            throw new DeliveryTransportException(TransportOutcome.Permanent, "Email subject or reply-to header is invalid.");
        if (string.IsNullOrWhiteSpace(message.IdempotencyKey) ||
            (message.CalendarContent is not null && message.CalendarMethod is not ("REQUEST" or "CANCEL")))
            throw new DeliveryTransportException(TransportOutcome.Permanent, "Invalid transport message contract.");
        var content = new EmailContent(message.Subject) { Html = message.HtmlBody, PlainText = message.TextBody };
        var transport = new Azure.Communication.Email.EmailMessage(options.SenderAddress, message.Recipient, content);
        if (message.ReplyTo is not null)
            transport.ReplyTo.Add(new EmailAddress(message.ReplyTo));
        if (message.CalendarContent is not null)
            transport.Attachments.Add(new EmailAttachment("sidequest.ics",
                $"text/calendar; charset=utf-8; method={message.CalendarMethod}", BinaryData.FromString(message.CalendarContent)));
        var submitted = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.SubmissionTimeout);
        try
        {
            var operation = await client.SendAsync(WaitUntil.Started, transport, timeout.Token).ConfigureAwait(false);
            submitted = true;
            await operation.WaitForCompletionAsync(timeout.Token).ConfigureAwait(false);
            if (!operation.HasValue || operation.Value.Status != EmailSendStatus.Succeeded || string.IsNullOrWhiteSpace(operation.Id))
                throw new DeliveryTransportException(TransportOutcome.Uncertain, "ACS did not provide completed acceptance evidence.");
            return new(operation.Id);
        }
        catch (RequestFailedException failure)
        {
            var outcome = submitted ? TransportOutcome.Uncertain : failure.Status switch
            {
                400 or 401 or 403 or 404 or 422 => TransportOutcome.Permanent,
                429 => TransportOutcome.Retryable,
                _ => TransportOutcome.Uncertain
            };
            TimeSpan? retryAfter = null;
            if (failure.GetRawResponse()?.Headers.TryGetValue("Retry-After", out var header) == true &&
                int.TryParse(header, out var seconds) && seconds >= 0)
                retryAfter = TimeSpan.FromSeconds(seconds);
            else if (failure.GetRawResponse()?.Headers.TryGetValue("Retry-After", out var dateHeader) == true &&
                DateTimeOffset.TryParse(dateHeader, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
                retryAfter = date > clock.GetUtcNow() ? date - clock.GetUtcNow() : TimeSpan.Zero;
            else if (failure.GetRawResponse()?.Headers.TryGetValue("x-ms-retry-after-ms", out var millisecondsHeader) == true &&
                int.TryParse(millisecondsHeader, out var milliseconds) && milliseconds >= 0)
                retryAfter = TimeSpan.FromMilliseconds(milliseconds);
            throw new DeliveryTransportException(outcome, "ACS submission failed; inspect approved provider diagnostics.", retryAfter);
        }
        catch (AuthenticationFailedException)
        {
            throw new DeliveryTransportException(submitted ? TransportOutcome.Uncertain : TransportOutcome.Permanent,
                "ACS managed identity authentication failed.");
        }
        catch (HttpRequestException)
        {
            throw new DeliveryTransportException(TransportOutcome.Uncertain, "ACS network submission outcome is uncertain.");
        }
        // OperationCanceledException intentionally propagates: the durable dispatcher has already recorded uncertainty.
    }

    internal static bool UsableAddress(string address) =>
        !string.IsNullOrWhiteSpace(address) &&
        !address.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029') &&
        MailAddress.TryCreate(address, out var parsed) &&
        string.Equals(parsed.Address, address, StringComparison.OrdinalIgnoreCase) && address.Contains('@');
}
