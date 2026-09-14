using System.Text;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Sidequest.Application.Notifications.Implementation;

namespace Sidequest.Infrastructure.Delivery;

/// <summary>Maintained Ical.Net serialization with recipient-only iTIP metadata and UTF-8 octet-safe RFC 5545 folding.</summary>
/// <param name="options">Stable configured service organizer; missing configuration fails explicitly.</param>
public sealed class RecipientCalendarRenderer(EmailDeliveryOptions options) : IRecipientCalendarRenderer
{
    /// <inheritdoc/>
    public string Render(CalendarSnapshot snapshot)
    {
        if (!AcsEmailGateway.UsableAddress(options.SenderAddress))
            throw new DeliveryTransportException(TransportOutcome.Permanent, "Calendar organizer configuration is missing.");
        if (!AcsEmailGateway.UsableAddress(snapshot.Recipient) || snapshot.QuestId == Guid.Empty ||
            snapshot.Sequence < 0 || snapshot.Sequence > int.MaxValue || snapshot.EndUtc <= snapshot.StartUtc ||
            snapshot.Method is not ("REQUEST" or "CANCEL"))
            throw new DeliveryTransportException(TransportOutcome.Permanent, "Invalid calendar snapshot.");
        var cancelled = snapshot.Method == "CANCEL";
        var calendar = new Calendar { Method = snapshot.Method, ProductId = "-//Sidequest//Recipient Calendar//EN" };
        var item = new CalendarEvent
        {
            Uid = $"{snapshot.QuestId:N}@sidequest.calendar",
            Sequence = (int)snapshot.Sequence,
            DtStamp = new CalDateTime(snapshot.StampUtc.UtcDateTime),
            DtStart = new CalDateTime(snapshot.StartUtc.UtcDateTime),
            DtEnd = new CalDateTime(snapshot.EndUtc.UtcDateTime),
            Organizer = new Organizer($"mailto:{options.SenderAddress}"),
            Summary = cancelled ? "Sidequest appointment withdrawn" : NormalizeText(snapshot.Title),
            Description = cancelled ? "" : NormalizeText(snapshot.Description),
            Location = cancelled ? "" : NormalizeText(snapshot.Location),
            Status = cancelled ? "CANCELLED" : "CONFIRMED"
        };
        item.Attendees.Add(new Attendee($"mailto:{snapshot.Recipient}") { Rsvp = !cancelled, Role = "REQ-PARTICIPANT" });
        calendar.Events.Add(item);
        var serialized = new CalendarSerializer().SerializeToString(calendar)
            ?? throw new DeliveryTransportException(TransportOutcome.Permanent, "Calendar serialization failed.");
        return FoldUtf8(serialized);
    }

    private static string NormalizeText(string value)
    {
        if (value is null || value.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new DeliveryTransportException(TransportOutcome.Permanent, "Calendar text contains unsupported control characters.");
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static string FoldUtf8(string serialized)
    {
        var normalized = serialized.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n ", "", StringComparison.Ordinal).Replace("\n\t", "", StringComparison.Ordinal);
        var result = new StringBuilder();
        foreach (var line in normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var octets = 0;
            foreach (var rune in line.EnumerateRunes())
            {
                if (octets + rune.Utf8SequenceLength > 75)
                {
                    result.Append("\r\n ");
                    octets = 1;
                }
                result.Append(rune.ToString());
                octets += rune.Utf8SequenceLength;
            }
            result.Append("\r\n");
        }
        return result.ToString();
    }
}
