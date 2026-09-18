namespace Sidequest.Web.Components.Administration;

/// <summary>Formats authorized account contacts without falling back to internal user identifiers.</summary>
public static class AccountLabel
{
    /// <summary>Shows the contact email first, with a distinct display name when available.</summary>
    /// <param name="email">Trusted projected email, or blank when no contact is recorded.</param>
    /// <param name="displayName">Trusted projected name, or blank when no name is recorded.</param>
    /// <returns>A readable contact label; absent email is explicitly identified as unavailable.</returns>
    public static string Format(string? email, string? displayName)
    {
        var contact = string.IsNullOrWhiteSpace(email) ? "Contact unavailable" : email.Trim();
        return string.IsNullOrWhiteSpace(displayName) || string.Equals(contact, displayName.Trim(), StringComparison.OrdinalIgnoreCase)
            ? contact
            : $"{contact} ({displayName.Trim()})";
    }
}
