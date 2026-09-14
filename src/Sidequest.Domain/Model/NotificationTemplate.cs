namespace Sidequest.Domain.Model;

/// <summary>Versioned notification template using allowlisted non-executable placeholders.</summary>
public sealed class NotificationTemplate : Entity
{
    /// <summary>Allowlisted template lookup key.</summary>
    public string Key { get; set; } = "";
    /// <summary>Template content revision retained for controlled updates.</summary>
    public int Revision { get; set; }
    /// <summary>Email subject template.</summary>
    public string Subject { get; set; } = "";
    /// <summary>HTML body template whose substituted user data must be HTML-encoded.</summary>
    public string HtmlBody { get; set; } = "";
    /// <summary>Plain-text alternative body template.</summary>
    public string TextBody { get; set; } = "";
    /// <summary>UTC instant of the latest template change.</summary>
    public DateTimeOffset ChangedUtc { get; set; }
    /// <summary>Internal administrator account identifier responsible for the change.</summary>
    public Guid ChangedById { get; set; }
}
