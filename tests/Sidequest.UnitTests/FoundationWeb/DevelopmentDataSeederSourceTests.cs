using System.Text.RegularExpressions;
using Sidequest.Web.Authentication;

namespace Sidequest.UnitTests.FoundationWeb;

/// <summary>Protects the deterministic development Event catalog without widening the internal seeder's production visibility.</summary>
public sealed class DevelopmentDataSeederSourceTests
{
    private static readonly Regex EventIdPattern = new(
        """Guid\.Parse\("(?<id>31000000-0000-4000-8000-00000000000[1-8])"\)""",
        RegexOptions.CultureInvariant);

    private static readonly Regex EventSeedPattern = new(
        """new\("(?<name>[^"]+)",\s*"[^"]+",\s*-?\d+,\s*(?:true|false),\s*\[(?<members>[^\]]+)\],\s*\[(?<quests>[^\]]+)\]\)""",
        RegexOptions.CultureInvariant);

    /// <summary>The catalog retains exactly eight unique fixed identifiers and eight uniquely named Event definitions.</summary>
    [Fact]
    public void SeederDeclaresEightUniqueDeterministicEvents()
    {
        var source = ReadSeeder();
        var ids = EventIdPattern.Matches(source).Select(match => match.Groups["id"].Value).ToArray();
        var seeds = EventSeedPattern.Matches(source).Select(ToSeed).ToArray();

        Assert.Equal(8, ids.Length);
        Assert.Equal(8, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            Enumerable.Range(1, 8).Select(index => $"31000000-0000-4000-8000-00000000000{index}"),
            ids);
        Assert.Equal(8, seeds.Length);
        Assert.Equal(8, seeds.Select(seed => seed.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            ["Prague Engineering Summit", "Seattle Cloud Week", "Barcelona Product Forum",
             "London AI Exchange", "Toronto Developer Days", "Redmond Maker Meetup",
             "Brno Innovation Days", "Dublin Security Lab"],
            seeds.Select(seed => seed.Name));
    }

    /// <summary>The three added Events each retain six Quests and restricted synthetic membership sets rather than joining every persona.</summary>
    [Fact]
    public void AddedEventsDeclareQuestsAndRestrictedMembershipSets()
    {
        var seeds = EventSeedPattern.Matches(ReadSeeder()).Select(ToSeed)
            .ToDictionary(seed => seed.Name, StringComparer.Ordinal);

        Assert.Equal([0, 1], seeds["Redmond Maker Meetup"].Members);
        Assert.Equal([2, 3], seeds["Brno Innovation Days"].Members);
        Assert.Equal([0, 3], seeds["Dublin Security Lab"].Members);
        Assert.All(
            ["Redmond Maker Meetup", "Brno Innovation Days", "Dublin Security Lab"],
            name => Assert.Equal(6, seeds[name].Quests.Count));
        Assert.All(
            ["Redmond Maker Meetup", "Brno Innovation Days", "Dublin Security Lab"],
            name => Assert.Equal(6, seeds[name].Quests.Distinct(StringComparer.Ordinal).Count()));
    }

    /// <summary>Bob is excluded from Redmond and Dublin while deterministic index selection makes him both Brno owner and member.</summary>
    [Fact]
    public void BobMembershipAndBrnoOwnershipRemainDeterministic()
    {
        var source = ReadSeeder();
        var seeds = EventSeedPattern.Matches(source).Select(ToSeed).ToArray();
        var bobIndex = DevelopmentPersonas.All.ToList().FindIndex(persona => persona.Name == "Bob");
        var redmond = seeds.Single(seed => seed.Name == "Redmond Maker Meetup");
        var brnoIndex = Array.FindIndex(seeds, seed => seed.Name == "Brno Innovation Days");
        var brno = seeds[brnoIndex];
        var dublin = seeds.Single(seed => seed.Name == "Dublin Security Lab");

        Assert.Equal(2, bobIndex);
        Assert.DoesNotContain(bobIndex, redmond.Members);
        Assert.DoesNotContain(bobIndex, dublin.Members);
        Assert.Contains(bobIndex, brno.Members);
        Assert.Equal(bobIndex, brnoIndex % DevelopmentPersonas.All.Count);
        Assert.Contains("var owner = orderedUsers[eventIndex % orderedUsers.Length];", source);
        Assert.Contains("foreach (var memberIndex in seed.MemberIndexes)", source);
        Assert.Contains("db.EventMemberships.Add(new EventMembership", source);
    }

    private static SeedContract ToSeed(Match match) => new(
        match.Groups["name"].Value,
        ParseMembers(match.Groups["members"].Value),
        Regex.Matches(match.Groups["quests"].Value, "\"[^\"]+\"", RegexOptions.CultureInvariant)
            .Select(item => item.Value[1..^1])
            .ToArray());

    private static int[] ParseMembers(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToArray();

    private static string ReadSeeder()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(
            root, "src", "Sidequest.Web", "Authentication", "DevelopmentDataSeeder.cs"));
    }

    private sealed record SeedContract(string Name, IReadOnlyList<int> Members, IReadOnlyList<string> Quests);
}
