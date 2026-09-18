using Sidequest.Application.Events;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

/// <summary>Exercises discovery similarity and stable authorized pagination through the public Event service.</summary>
/// <param name="database">Migrated database owned and cleaned by the existing SQL fixture.</param>
public sealed class EventQueryTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Returns stored contact labels only inside the existing recipient, member and owner authorization boundaries.</summary>
    /// <returns>Completion after exact email projections and denied management and roster reads.</returns>
    [Fact]
    public async Task ContactEmailsFollowExistingEventAuthorization()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var owner = context.Service(seed.User);
        var applicant = context.Service(seed.Other);
        await applicant.RequestMembershipAsync(seed.Event.Id);
        await owner.InviteAsync(seed.Event.Id, seed.Other.ObjectId);

        var request = Assert.Single((await owner.ListRequestsAsync(seed.Event.Id, new())).Items);
        var invitation = Assert.Single((await owner.ListInvitationsAsync(seed.Event.Id, new())).Items);
        Assert.Equal(new PersonSummary(seed.Other.Id, seed.Other.DisplayName, seed.Other.Email), request.User);
        Assert.Equal(request.User, invitation.User);
        Assert.Equal(request.User, Assert.Single((await applicant.ListRequestsAsync(null, new())).Items).User);
        Assert.Equal(request.User, Assert.Single((await applicant.ListInvitationsAsync(null, new())).Items).User);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            applicant.ListRequestsAsync(seed.Event.Id, new()))).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            applicant.ListInvitationsAsync(seed.Event.Id, new()))).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            applicant.ListMembersAsync(seed.Event.Id, new()))).Code);

        await owner.AddMemberAsync(seed.Event.Id, seed.Other.ObjectId, false);
        var members = (await applicant.ListMembersAsync(seed.Event.Id, new())).Items;
        Assert.Contains(members, x => x.User == request.User);
        Assert.Contains(members, x => x.User == new PersonSummary(seed.User.Id, seed.User.DisplayName, seed.User.Email));
    }

    /// <summary>Normalizes case/punctuation and uses Jaccard at the inclusive threshold while requiring inclusive date overlap and active discovery.</summary>
    /// <returns>A task completing after concrete similarity, order and privacy assertions.</returns>
    [Fact]
    public async Task DuplicateSearchNormalizesAndHonorsJaccardOverlapAndPrivacy()
    {
        var context = new EventTestContext(database);
        var user = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, user);
        var token = Guid.NewGuid().ToString("N");
        var identical = Candidate(user.Id, $"{token} ALPHA---beta gamma delta");
        var threshold = Candidate(user.Id, $"{token} alpha beta");
        threshold.StartDate = new(2026, 7, 16);
        var below = Candidate(user.Id, $"{token} alpha");
        var outside = Candidate(user.Id, identical.Name);
        outside.StartDate = new(2026, 7, 17);
        outside.EndDate = new(2026, 7, 18);
        var draft = Candidate(user.Id, identical.Name);
        draft.Status = EventStatus.Draft;
        var completed = Candidate(user.Id, identical.Name);
        completed.Status = EventStatus.Completed;
        await FoundationSeed.PersistAsync(database, identical, threshold, below, outside, draft, completed);
        var result = await context.Service(user).FindDuplicatesAsync($" {token.ToUpperInvariant()} alpha beta gamma delta ",
            new(2026, 7, 15), new(2026, 7, 16));
        Assert.Equal(new[] { identical.Id, threshold.Id }, result.Select(x => x.Event.Id));
        Assert.Equal(1d, result[0].Similarity);
        Assert.Equal(0.6d, result[1].Similarity);
        Assert.All(result, x => { Assert.False(x.Event.IsMember); Assert.Equal("Public candidate", x.Event.DiscoverySummary); });
        Assert.DoesNotContain(result, x => new[] { below.Id, outside.Id, draft.Id, completed.Id }.Contains(x.Event.Id));
    }

    /// <summary>Caps identical warnings at five and resolves equal-score ordering by identifier independently of insertion order.</summary>
    /// <returns>A task completing after exact top-five order and repeat-query stability checks.</returns>
    [Fact]
    public async Task DuplicateSearchReturnsStableTopFive()
    {
        var context = new EventTestContext(database);
        var user = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, user);
        var name = $"{Guid.NewGuid():N} shared name";
        var candidates = Enumerable.Range(1, 7).Select(_ => Candidate(user.Id, name)).ToArray();
        await FoundationSeed.PersistAsync(database, candidates.Reverse().ToArray());
        var sut = context.Service(user);
        var result = await sut.FindDuplicatesAsync(name, new(2026, 7, 15), new(2026, 7, 16));
        var repeated = await sut.FindDuplicatesAsync(name, new(2026, 7, 15), new(2026, 7, 16));
        Assert.Equal(candidates.OrderBy(x => x.Id).Take(5).Select(x => x.Id), result.Select(x => x.Event.Id));
        Assert.Equal(result.Select(x => x.Event.Id), repeated.Select(x => x.Event.Id));
        Assert.Equal(5, result.Count);
        Assert.All(result, x => Assert.Equal(1d, x.Similarity));
    }

    /// <summary>Paginates only the actor's membership set with stable date/identifier ordering, an exact total and empty trailing pages.</summary>
    /// <returns>A task completing after singleton, full-capacity and beyond-end pages are compared.</returns>
    [Fact]
    public async Task MinePagingUsesStableDateAndSqlIdentifierOrder()
    {
        var context = new EventTestContext(database);
        var user = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, user);
        var items = Enumerable.Range(1, 3).Select(_ => Candidate(user.Id, "Paged Event")).ToArray();
        items[0].StartDate = new(2026, 7, 14);
        await FoundationSeed.PersistAsync(database, items.Reverse().ToArray());
        await FoundationSeed.PersistAsync(database, items.Select(x => new EventMembership
        {
            EventId = x.Id, UserId = user.Id, ChangedById = user.Id, ChangedUtc = FoundationSeed.Now
        }).ToArray());
        var expected = items.OrderBy(x => x.StartDate).ThenBy(x => new System.Data.SqlTypes.SqlGuid(x.Id)).Select(x => x.Id).ToArray();
        var sut = context.Service(user);
        var first = await sut.ListAsync(EventListKind.Mine, new(1, 1));
        var second = await sut.ListAsync(EventListKind.Mine, new(2, 1));
        var third = await sut.ListAsync(EventListKind.Mine, new(3, 1));
        var beyond = await sut.ListAsync(EventListKind.Mine, new(4, 1));
        Assert.Equal(expected, first.Items.Concat(second.Items).Concat(third.Items).Select(x => x.Id));
        Assert.Equal(3, first.TotalCount);
        Assert.Equal(3, beyond.TotalCount);
        Assert.Equal(4, beyond.Page);
        Assert.Equal(1, beyond.PageSize);
        Assert.Empty(beyond.Items);
        Assert.Equal(expected, (await sut.ListAsync(EventListKind.Mine, new(1, 100))).Items.Select(x => x.Id));
    }

    private static Event Candidate(Guid creator, string name)
    {
        var item = FoundationSeed.NewEvent(creator);
        item.Name = name;
        item.Description = "Private duplicate sentinel";
        item.DiscoverySummary = "Public candidate";
        return item;
    }
}
