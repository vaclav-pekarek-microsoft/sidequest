namespace Sidequest.Application.Administration;

/// <summary>One immutable email template revision or revision zero for the compiled default.</summary>
/// <param name="Key">Allowlisted stable message category.</param>
/// <param name="Revision">Zero for compiled defaults, positive for append-only database revisions.</param>
/// <param name="Subject">Single-line header template.</param>
/// <param name="HtmlBody">Strict attribute-free XHTML fragment using allowlisted inert elements.</param>
/// <param name="TextBody">Plain-text alternative with the same non-executable placeholder language.</param>
/// <param name="ChangedUtc">UTC override creation instant, null for compiled defaults.</param>
public sealed record EmailTemplate(string Key, int Revision, string Subject, string HtmlBody, string TextBody,
    DateTimeOffset? ChangedUtc = null);
