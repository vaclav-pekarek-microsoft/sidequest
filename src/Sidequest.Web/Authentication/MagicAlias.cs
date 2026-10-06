using System.Net.Mail;

namespace Sidequest.Web.Authentication;

/// <summary>Normalizes user-entered Microsoft aliases into the only permitted authentication mailbox domain.</summary>
public static class MagicAlias
{
    /// <summary>The fixed mailbox domain accepted by deployed magic-code authentication.</summary>
    public const string Domain = "microsoft.com";

    /// <summary>Normalizes an alias or rejects values that do not form one exact Microsoft mailbox.</summary>
    /// <param name="alias">User-entered mailbox local part without a domain.</param>
    /// <returns>The lower-case <c>alias@microsoft.com</c> address.</returns>
    /// <exception cref="ArgumentException">The value is empty, includes a domain, contains unsupported characters, or exceeds email limits.</exception>
    public static string Normalize(string? alias)
    {
        var localPart = alias?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(localPart) || localPart.Length > 64 ||
            !char.IsAsciiLetterOrDigit(localPart[0]) ||
            !char.IsAsciiLetterOrDigit(localPart[^1]) ||
            localPart.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '-' or '_')))
            throw new ArgumentException("Enter a valid Microsoft alias without @microsoft.com.", nameof(alias));

        var address = $"{localPart}@{Domain}";
        if (!MailAddress.TryCreate(address, out var parsed) ||
            !string.Equals(parsed.Address, address, StringComparison.Ordinal))
            throw new ArgumentException("Enter a valid Microsoft alias without @microsoft.com.", nameof(alias));
        return address;
    }

    /// <summary>Returns the alias portion of a previously normalized Microsoft mailbox.</summary>
    /// <param name="email">Normalized Microsoft mailbox.</param>
    /// <returns>The local alias portion.</returns>
    /// <exception cref="ArgumentException">The address is not an exact lower-case Microsoft mailbox.</exception>
    public static string LocalPart(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var suffix = $"@{Domain}";
        if (!email.EndsWith(suffix, StringComparison.Ordinal) || email.Length <= suffix.Length)
            throw new ArgumentException("A normalized Microsoft mailbox is required.", nameof(email));
        return email[..^suffix.Length];
    }
}
