using System.Globalization;
using NodaTime;
using NodaTime.TimeZones;

namespace Sidequest.Web.Components.Events;

/// <summary>Builds compact regional choices from the bundled CLDR mapping while retaining existing Event zone identifiers.</summary>
public static class EventTimeZones
{
    /// <summary>Groups related cities by CLDR regional zone and orders their date-specific UTC offsets from west to east.</summary>
    /// <param name="startDate">Event-local date used for the displayed offset; actual scheduling still uses the full TZDB rules.</param>
    /// <param name="selectedId">Existing identifier to preserve, including supported aliases and zones outside the regional mapping.</param>
    /// <returns>Distinct choices ordered by numeric offset and then display label.</returns>
    public static IReadOnlyList<EventTimeZoneOption> Create(DateOnly startDate, string? selectedId)
    {
        var source = TzdbDateTimeZoneSource.Default;
        var provider = DateTimeZoneProviders.Tzdb;
        var selected = selectedId is null ? null : provider.GetZoneOrNull(selectedId);
        var canonicalSelected = selected is null ? null : source.CanonicalIdMap[selected.Id];
        var localNoon = new LocalDateTime(startDate.Year, startDate.Month, startDate.Day, 12, 0);
        var options = new List<EventTimeZoneOption>();
        var retained = false;
        foreach (var group in source.WindowsMapping.MapZones.GroupBy(mapping => mapping.WindowsId))
        {
            var ids = group.SelectMany(mapping => mapping.TzdbIds).Distinct(StringComparer.Ordinal).ToArray();
            var id = source.WindowsMapping.PrimaryMapping[group.Key];
            if (!retained && selectedId is not null && canonicalSelected is not null &&
                ids.Any(candidate => source.CanonicalIdMap[candidate] == canonicalSelected))
            {
                id = selectedId;
                retained = true;
            }
            var cities = ids.Where(candidate => !candidate.StartsWith("Etc/", StringComparison.Ordinal))
                .Select(candidate => candidate[(candidate.LastIndexOf('/') + 1)..].Replace('_', ' '))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var label = string.Join(", ", cities);
            Add(id, label.Length == 0 ? group.Key : label);
        }
        if (selected is not null && selectedId is not null && !retained)
            Add(selectedId, selected.MinOffset == selected.MaxOffset ? "Fixed UTC offset" :
                selected.Id[(selected.Id.LastIndexOf('/') + 1)..].Replace('_', ' '));
        return options.OrderBy(option => option.OffsetSeconds).ThenBy(option => option.Label, StringComparer.Ordinal).ToArray();

        void Add(string id, string cities)
        {
            var offset = provider[id].AtLeniently(localNoon).Offset;
            options.Add(new(id, $"(UTC{offset.ToString("+HH:mm", CultureInfo.InvariantCulture)}) {cities}", offset.Seconds));
        }
    }
}
