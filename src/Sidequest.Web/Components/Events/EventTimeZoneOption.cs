namespace Sidequest.Web.Components.Events;

/// <summary>A readable regional choice backed by a real TZDB zone, never a fixed-offset substitute.</summary>
/// <param name="Id">IANA identifier submitted unchanged by the editor.</param>
/// <param name="Label">UTC offset followed by grouped city names.</param>
/// <param name="OffsetSeconds">Signed offset at local noon on the Event start date, used for numeric ordering.</param>
public sealed record EventTimeZoneOption(string Id, string Label, int OffsetSeconds);
