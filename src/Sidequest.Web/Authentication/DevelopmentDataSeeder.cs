using System.Data;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.Web.Authentication;

/// <summary>Creates an idempotent local catalog for the documented synthetic personas after the development database is available.</summary>
internal sealed class DevelopmentDataSeeder(
    ISidequestDbContextFactory factory,
    FoundationAuthenticationSettings settings,
    IConfiguration configuration,
    TimeProvider clock)
{
    private static readonly Guid[] EventIds =
    [
        Guid.Parse("31000000-0000-4000-8000-000000000001"),
        Guid.Parse("31000000-0000-4000-8000-000000000002"),
        Guid.Parse("31000000-0000-4000-8000-000000000003"),
        Guid.Parse("31000000-0000-4000-8000-000000000004"),
        Guid.Parse("31000000-0000-4000-8000-000000000005"),
        Guid.Parse("31000000-0000-4000-8000-000000000006"),
        Guid.Parse("31000000-0000-4000-8000-000000000007"),
        Guid.Parse("31000000-0000-4000-8000-000000000008")
    ];

    private static readonly EventSeed[] EventSeeds =
    [
        new("Prague Engineering Summit", "Three days of engineering talks and social activities in Prague.", -35, true,
            [0, 1, 2, 3], ["Old Town Photo Walk", "Retro Gaming Night", "Czech Dinner", "Morning Run", "Architecture Stories", "Farewell Coffee"]),
        new("Seattle Cloud Week", "Meet colleagues around cloud engineering sessions in Seattle.", 7, false,
            [0, 1, 2, 3], ["Pike Place Breakfast", "Lake Union Kayaking", "Cloud Trivia", "Coffee Roastery Tour", "Board Games Social", "Sunset Photo Walk"]),
        new("Barcelona Product Forum", "Product, design, and research meetups by the Mediterranean.", 30, false,
            [0, 1, 2, 3], ["Tapas Trail", "Beach Volleyball", "Gothic Quarter Walk", "Design Critique Cafe", "Sunrise Run", "Paella Workshop"]),
        new("London AI Exchange", "An applied AI gathering with room for practical side adventures.", 60, false,
            [0, 1, 2, 3], ["Museum Late Visit", "AI Paper Club", "Thames Evening Walk", "Escape Room", "Sunday Roast", "Street Art Tour"]),
        new("Toronto Developer Days", "Developer community sessions and friendly activities across Toronto.", 90, false,
            [0, 1, 2, 3], ["Harbourfront Cycle", "Ramen Dinner", "Indie Games Meetup", "Distillery District Walk", "Karaoke Night", "Morning Yoga"]),
        new("Redmond Maker Meetup", "Hands-on maker sessions with relaxed team activities around Redmond.", 14, false,
            [0, 1], ["Hardware Hack Night", "Marymoor Cycle", "Robotics Demo", "Food Truck Lunch", "Woodworking Basics", "Retro Console Lab"]),
        new("Brno Innovation Days", "A focused innovation gathering with local culture and collaborative experiments.", 45, false,
            [2, 3], ["Villa Tugendhat Walk", "Prototype Cafe", "Moravian Dinner", "Escape Challenge", "Morning Ride", "Demo Storytelling"]),
        new("Dublin Security Lab", "Security workshops paired with welcoming social Quests across Dublin.", 75, false,
            [0, 3], ["Coastal Train Walk", "Threat Modeling Games", "Trad Music Evening", "Phoenix Park Run", "Security Trivia", "Coffee Cupping"])
    ];

    /// <summary>Seeds the complete catalog once, including memberships, ownership, and mixed follow/join participation for every persona.</summary>
    /// <param name="cancellationToken">Cancels database reads, writes, or transaction completion.</param>
    /// <returns>A task that completes when the existing seed is detected or the catalog transaction commits.</returns>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("DevelopmentData:Seed", true))
            return;

        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var existingEventIds = (await db.Events.Where(item => EventIds.Contains(item.Id))
            .Select(item => item.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        var objectIds = DevelopmentPersonas.All.Select(persona => persona.ObjectId).ToArray();
        var users = await db.Users
            .Where(user => user.TenantId == DevelopmentPersonas.TenantId && objectIds.Contains(user.ObjectId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var persona in DevelopmentPersonas.All)
        {
            if (users.Any(user => user.ObjectId == persona.ObjectId))
                continue;

            var user = new UserAccount
            {
                TenantId = DevelopmentPersonas.TenantId,
                ObjectId = persona.ObjectId,
                DisplayName = persona.Name,
                Email = persona.Email,
                IsEligible = true
            };
            users.Add(user);
            db.Users.Add(user);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var orderedUsers = DevelopmentPersonas.All
            .Select(persona => users.Single(user => user.ObjectId == persona.ObjectId))
            .ToArray();

        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var administrator = orderedUsers[0];
        if (settings.BootstrapAdministratorObjectId == administrator.ObjectId &&
            !await db.Administrators.AnyAsync(item => item.UserId == administrator.Id, cancellationToken).ConfigureAwait(false))
        {
            db.Administrators.Add(new Administrator { UserId = administrator.Id });
        }
        for (var eventIndex = 0; eventIndex < EventSeeds.Length; eventIndex++)
        {
            if (existingEventIds.Contains(EventIds[eventIndex]))
                continue;
            var seed = EventSeeds[eventIndex];
            var startDate = today.AddDays(seed.StartOffsetDays);
            var endDate = startDate.AddDays(3);
            var owner = orderedUsers[eventIndex % orderedUsers.Length];
            var parent = new Event
            {
                Id = EventIds[eventIndex],
                CreatorId = owner.Id,
                Name = seed.Name,
                Description = seed.Description,
                DiscoverySummary = seed.Description,
                StartDate = startDate,
                EndDate = endDate,
                TimeZoneId = "Etc/UTC",
                Status = seed.IsPast ? EventStatus.Completed : EventStatus.Active,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            db.Events.Add(parent);
            db.EventOwners.Add(new EventOwner { EventId = parent.Id, UserId = owner.Id });
            foreach (var memberIndex in seed.MemberIndexes)
            {
                var user = orderedUsers[memberIndex];
                db.EventMemberships.Add(new EventMembership
                {
                    EventId = parent.Id,
                    UserId = user.Id,
                    Status = MembershipStatus.Active,
                    ChangedUtc = now,
                    ChangedById = administrator.Id
                });
            }

            for (var questIndex = 0; questIndex < seed.QuestNames.Count; questIndex++)
            {
                var creator = orderedUsers[seed.MemberIndexes[questIndex % seed.MemberIndexes.Count]];
                var startUtc = new DateTimeOffset(
                    startDate.AddDays(questIndex % 3).ToDateTime(new TimeOnly(9 + questIndex, 0)),
                    TimeSpan.Zero);
                var quest = new Quest
                {
                    EventId = parent.Id,
                    CreatorId = creator.Id,
                    Title = seed.QuestNames[questIndex],
                    Description = $"Meet colleagues for {seed.QuestNames[questIndex].ToLowerInvariant()} during {seed.Name}.",
                    Location = $"{seed.Name} meeting point {questIndex + 1}",
                    SuggestedCapacity = 8 + (questIndex * 4),
                    StartUtc = startUtc,
                    EndUtc = startUtc.AddHours(2),
                    Visibility = QuestVisibility.Public,
                    Status = seed.IsPast ? QuestStatus.Completed : QuestStatus.Active,
                    CalendarRevision = 1,
                    StartRevision = 1,
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                db.Quests.Add(quest);
                db.QuestOwners.Add(new QuestOwner { QuestId = quest.Id, UserId = creator.Id });
                for (var memberIndex = 0; memberIndex < seed.MemberIndexes.Count; memberIndex++)
                {
                    var selector = (questIndex + memberIndex) % 4;
                    if (selector > 1)
                        continue;

                    db.Participations.Add(new QuestParticipation
                    {
                        QuestId = quest.Id,
                        UserId = orderedUsers[seed.MemberIndexes[memberIndex]].Id,
                        Status = selector == 0 ? ParticipationStatus.Joined : ParticipationStatus.Following,
                        ChangedUtc = now
                    });
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record EventSeed(
        string Name,
        string Description,
        int StartOffsetDays,
        bool IsPast,
        IReadOnlyList<int> MemberIndexes,
        IReadOnlyList<string> QuestNames);
}
