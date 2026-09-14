namespace Sidequest.Application.Abstractions;

/// <summary>Provider acceptance evidence, not a guarantee of recipient mailbox arrival.</summary>
/// <param name="ProviderMessageId">Provider-issued identifier to retain with durable delivery state.</param>
public sealed record EmailReceipt(string ProviderMessageId);
