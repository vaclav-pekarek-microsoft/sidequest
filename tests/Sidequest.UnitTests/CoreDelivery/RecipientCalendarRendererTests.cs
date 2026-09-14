using System.Text;
using Ical.Net;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Infrastructure.Delivery;

namespace Sidequest.UnitTests.CoreDelivery;

/// <summary>Protocol-level checks against the maintained library parser, with no Outlook or provider calls.</summary>
public sealed class RecipientCalendarRendererTests
{
    private readonly RecipientCalendarRenderer renderer = new(new EmailDeliveryOptions { SenderAddress = "organizer@example.invalid" });

    /// <summary>Stable IDs, UTC boundaries, exact retries, escaping and single attendee survive serialization and parse.</summary>
    [Fact]
    public void Render_RoundTripsEscapesUtcAndOnlyRecipientWithExactRetries()
    {
        var snapshot = Snapshot() with { Title = "Private, title; \\ line\nnext", Description = "text\r\nATTENDEE:mailto:intruder@example.invalid", Location = "Room; 1, east" };
        var text = renderer.Render(snapshot);
        Assert.Equal(text, renderer.Render(snapshot));
        var parsed = Calendar.Load(text)!;
        var item = Assert.Single(parsed.Events);
        Assert.Equal("REQUEST", parsed.Method);
        Assert.Equal($"{snapshot.QuestId:N}@sidequest.calendar", item.Uid);
        Assert.Equal(42, item.Sequence);
        Assert.Equal(snapshot.Title, item.Summary);
        Assert.Equal(snapshot.Location, item.Location);
        Assert.Equal(snapshot.StartUtc.UtcDateTime, item.DtStart!.AsUtc);
        Assert.Equal(snapshot.EndUtc.UtcDateTime, item.DtEnd!.AsUtc);
        Assert.Equal("mailto:recipient@example.invalid", Assert.Single(item.Attendees).Value!.ToString());
        Assert.Contains("DTSTART:20260715T100000Z\r\n", text);
        Assert.Contains("DTSTAMP:20260714T100000Z\r\n", text);
        Assert.DoesNotContain("\r\nATTENDEE:mailto:intruder", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal));
    }

    /// <summary>Unicode folding respects the seventy-five octet physical-line bound without splitting UTF-8 characters.</summary>
    [Fact]
    public void Render_FoldsMultibyteTextAtSeventyFiveOctets()
    {
        var title = string.Concat(Enumerable.Repeat("🙂ě漢字", 100));
        var text = renderer.Render(Snapshot() with { Title = title });
        Assert.All(text.Split("\r\n"), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line));
        Assert.Equal(title, Assert.Single(Calendar.Load(text)!.Events).Summary);
        Assert.Contains("\r\n ", text);
    }

    /// <summary>Withdrawal uses the same UID and higher sequence while removing protected descriptions and location.</summary>
    [Fact]
    public void Render_CancellationRetainsIdentityAndRedactsProtectedContent()
    {
        var initial = Snapshot();
        var request = Calendar.Load(renderer.Render(initial))!;
        var cancellation = renderer.Render(initial with { Method = "CANCEL", Sequence = 43 });
        var parsed = Calendar.Load(cancellation)!;
        var cancelled = Assert.Single(parsed.Events);
        Assert.Equal("CANCEL", parsed.Method);
        Assert.Equal(Assert.Single(request.Events).Uid, cancelled.Uid);
        Assert.Equal(43, cancelled.Sequence);
        Assert.Equal("CANCELLED", cancelled.Status);
        Assert.DoesNotContain(initial.Title, cancellation);
        Assert.DoesNotContain(initial.Description, cancellation);
        Assert.DoesNotContain(initial.Location, cancellation);
        Assert.Single(cancelled.Attendees);
    }

    /// <summary>Invalid addresses, unsupported methods and overflowing sequences fail rather than producing malformed calendar data.</summary>
    [Fact]
    public void Render_RejectsMissingOrganizerInjectionAndInvalidSequence()
    {
        Assert.Throws<DeliveryTransportException>(() => new RecipientCalendarRenderer(new()).Render(Snapshot()));
        Assert.Throws<DeliveryTransportException>(() => renderer.Render(Snapshot() with { Recipient = "x@example.invalid\r\nATTENDEE:evil" }));
        Assert.Throws<DeliveryTransportException>(() => renderer.Render(Snapshot() with { Sequence = (long)int.MaxValue + 1 }));
        Assert.Throws<DeliveryTransportException>(() => renderer.Render(Snapshot() with { Method = "PUBLISH" }));
        Assert.Throws<DeliveryTransportException>(() => renderer.Render(Snapshot() with { Title = "bad\0title" }));
    }

    /// <summary>Bare carriage returns in trusted plain-text fields are escaped as text, never emitted as injected calendar properties.</summary>
    [Fact]
    public void Render_NormalizesBareCarriageReturnBeforeEscaping()
    {
        var text = renderer.Render(Snapshot() with { Title = "Title\rATTENDEE:mailto:extra@example.invalid" });
        var parsed = Calendar.Load(text)!;
        Assert.Single(Assert.Single(parsed.Events).Attendees);
        Assert.Equal("Title\nATTENDEE:mailto:extra@example.invalid", Assert.Single(parsed.Events).Summary);
        Assert.DoesNotContain("\rATTENDEE", text);
    }

    private static CalendarSnapshot Snapshot() => new(Guid.Parse("12345678-1234-1234-1234-123456789abc"), 42,
        new(2026, 7, 14, 10, 0, 0, TimeSpan.Zero), new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero),
        new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero), "recipient@example.invalid", "REQUEST",
        "Private title", "Private details", "Private room");
}
