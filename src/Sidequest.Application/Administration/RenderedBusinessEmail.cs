namespace Sidequest.Application.Administration;

/// <summary>Frozen nonsecret rendered business wording, reused verbatim after the first attempted submission.</summary>
/// <param name="Key">Allowlisted template used to render the message.</param>
/// <param name="Revision">Exact override revision, or zero for compiled wording.</param>
/// <param name="Subject">Validated single-line subject.</param>
/// <param name="HtmlBody">Strict inert HTML with encoded substitutions.</param>
/// <param name="TextBody">Plain-text alternative.</param>
/// <param name="ReplyTo">Validated optional response mailbox; does not alter recipient, verified sender or calendar organizer.</param>
public sealed record RenderedBusinessEmail(string Key, int Revision, string Subject, string HtmlBody,
    string TextBody, string? ReplyTo);
