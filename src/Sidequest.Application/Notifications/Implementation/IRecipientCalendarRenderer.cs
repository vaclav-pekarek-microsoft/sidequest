namespace Sidequest.Application.Notifications.Implementation;

/// <summary>Renders one recipient's immutable iTIP payload without fetching content or granting resource access.</summary>
public interface IRecipientCalendarRenderer
{
    /// <summary>Serializes an already-authorized snapshot using the configured stable service organizer.</summary>
    /// <param name="snapshot">Calendar facts captured for one logical sequence.</param>
    /// <returns>RFC 5545 text, including CRLF and octet-safe folding.</returns>
    public string Render(CalendarSnapshot snapshot);
}
