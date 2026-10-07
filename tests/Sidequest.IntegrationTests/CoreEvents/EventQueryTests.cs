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

    /// <summary>Returns every effective-Active Event in the actor's tenant and only the actor's eligible legacy Draft with exact audience flags.</summary>
    /// <returns>A task completing after exact union, count, lifecycle, membership, and ownership projections are asserted.</returns>
    [Fact]
    public async Task ListAllReturnsSameTenantActiveUnionAndActorsOwnedLegacyDraftWithAudienceProjection()
    {
        var context = new EventTestContext(database);
        var actor = FoundationSeed.NewUser();
        var creator = FoundationSeed.NewUser();
        creator.TenantId = actor.TenantId;
        await FoundationSeed.PersistAsync(database, actor, creator);
        var joined = Candidate(creator.Id, "Joined active");
        var unjoined = Candidate(creator.Id, "Unjoined active");
        unjoined.StartDate = new(2026, 7, 16);
        unjoined.EndDate = new(2026, 7, 17);
        var ownedDraft = Candidate(creator.Id, "Owned legacy draft");
        ownedDraft.Status = EventStatus.Draft;
        ownedDraft.StartDate = new(2026, 7, 17);
        ownedDraft.EndDate = new(2026, 7, 18);
        await FoundationSeed.PersistAsync(database, joined, unjoined, ownedDraft);
        await FoundationSeed.PersistAsync(database,
            Membership(joined, actor),
            Membership(ownedDraft, actor),
            new EventOwner { EventId = ownedDraft.Id, UserId = actor.Id });

        var result = await context.Service(actor).ListAsync(EventListKind.All, new(1, 100));

        Assert.Equal(new[] { joined.Id, unjoined.Id, ownedDraft.Id }, result.Items.Select(x => x.Id));
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(1, result.Page);
        Assert.Equal(100, result.PageSize);
        var projections = result.Items.ToDictionary(x => x.Id);
        Assert.Equal((EventStatus.Active, true, false),
            (projections[joined.Id].Status, projections[joined.Id].IsMember, projections[joined.Id].IsOwner));
        Assert.Equal((EventStatus.Active, false, false),
            (projections[unjoined.Id].Status, projections[unjoined.Id].IsMember, projections[unjoined.Id].IsOwner));
        Assert.Equal((EventStatus.Draft, true, true),
            (projections[ownedDraft.Id].Status, projections[ownedDraft.Id].IsMember, projections[ownedDraft.Id].IsOwner));
        Assert.Equal(actor.Id, Assert.Single(projections[ownedDraft.Id].Owners).Id);
    }

    /// <summary>Rejects every foreign, removed, cross-tenant, terminal, and exactly-ended partition while retaining an Event ending after now.</summary>
    /// <returns>A task completing after the exact post-filter singleton and effective-completion boundary are asserted.</returns>
    [Fact]
    public async Task ListAllExcludesForeignDraftRemovedOwnedDraftCrossTenantAndTerminalRows()
    {
        var context = new EventTestContext(database);
        context.Clock.Now = new(2026, 7, 16, 22, 0, 0, TimeSpan.Zero);
        var actor = FoundationSeed.NewUser();
        var sameTenantCreator = FoundationSeed.NewUser();
        var otherActor = FoundationSeed.NewUser();
        sameTenantCreator.TenantId = actor.TenantId;
        otherActor.TenantId = actor.TenantId;
        var crossTenantCreator = FoundationSeed.NewUser();
        await FoundationSeed.PersistAsync(database, actor, sameTenantCreator, otherActor, crossTenantCreator);

        var endingAfterNow = Candidate(sameTenantCreator.Id, "Ends after now");
        endingAfterNow.TimeZoneId = "Etc/UTC";
        var endingExactlyNow = Candidate(sameTenantCreator.Id, "Ends exactly now");
        var foreignDraft = Candidate(sameTenantCreator.Id, "Another actor draft");
        foreignDraft.Status = EventStatus.Draft;
        var removedOwnedDraft = Candidate(sameTenantCreator.Id, "Removed owner draft");
        removedOwnedDraft.Status = EventStatus.Draft;
        var crossTenant = Candidate(crossTenantCreator.Id, "Cross tenant active");
        var completed = Candidate(sameTenantCreator.Id, "Completed");
        completed.Status = EventStatus.Completed;
        var cancelled = Candidate(sameTenantCreator.Id, "Cancelled");
        cancelled.Status = EventStatus.Cancelled;
        var archived = Candidate(sameTenantCreator.Id, "Archived");
        archived.Status = EventStatus.Archived;
        await FoundationSeed.PersistAsync(database, endingAfterNow, endingExactlyNow, foreignDraft,
            removedOwnedDraft, crossTenant, completed, cancelled, archived);
        await FoundationSeed.PersistAsync(database,
            Membership(endingExactlyNow, actor),
            Membership(foreignDraft, otherActor),
            new EventOwner { EventId = foreignDraft.Id, UserId = otherActor.Id },
            Membership(removedOwnedDraft, actor, MembershipStatus.Removed),
            new EventOwner { EventId = removedOwnedDraft.Id, UserId = actor.Id },
            Membership(completed, actor),
            Membership(cancelled, actor),
            Membership(archived, actor));

        var result = await context.Service(actor).ListAsync(EventListKind.All, new(1, 100));

        var visible = Assert.Single(result.Items);
        Assert.Equal(endingAfterNow.Id, visible.Id);
        Assert.Equal(EventStatus.Active, visible.Status);
        Assert.False(visible.IsMember);
        Assert.Equal(1, result.TotalCount);
        Assert.DoesNotContain(result.Items, x => new[]
        {
            endingExactlyNow.Id, foreignDraft.Id, removedOwnedDraft.Id, crossTenant.Id,
            completed.Id, cancelled.Id, archived.Id
        }.Contains(x.Id));
    }

    /// <summary>Orders the filtered union by start date and SQL GUID before returning full, interior singleton, and out-of-range pages.</summary>
    /// <returns>A task completing after every page reports the same exact post-filter total and stable item slice.</returns>
    [Fact]
    public async Task ListAllOrdersAndPagesPostFilterUnionByStartDateThenSqlGuid()
    {
        var context = new EventTestContext(database);
        var actor = FoundationSeed.NewUser();
        var creator = FoundationSeed.NewUser();
        creator.TenantId = actor.TenantId;
        await FoundationSeed.PersistAsync(database, actor, creator);
        var earlier = Candidate(creator.Id, "Earlier");
        earlier.Id = Guid.Parse("00000000-0000-0000-0000-000000000003");
        earlier.StartDate = new(2026, 7, 14);
        var tiedA = Candidate(creator.Id, "Tied A");
        tiedA.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var tiedB = Candidate(creator.Id, "Tied B");
        tiedB.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var filteredTerminal = Candidate(creator.Id, "Filtered before paging");
        filteredTerminal.Id = Guid.Parse("00000000-0000-0000-0000-000000000004");
        filteredTerminal.StartDate = new(2026, 7, 13);
        filteredTerminal.Status = EventStatus.Completed;
        await FoundationSeed.PersistAsync(database, tiedA, filteredTerminal, earlier, tiedB);
        await FoundationSeed.PersistAsync(database, Membership(filteredTerminal, actor));
        var expected = new[] { earlier, tiedA, tiedB }
            .OrderBy(x => x.StartDate)
            .ThenBy(x => new System.Data.SqlTypes.SqlGuid(x.Id))
            .Select(x => x.Id)
            .ToArray();
        var cases = new[]
        {
            (Request: new Sidequest.Application.Abstractions.PageRequest(1, 100), Expected: expected),
            (Request: new Sidequest.Application.Abstractions.PageRequest(1, 2), Expected: expected[..2]),
            (Request: new Sidequest.Application.Abstractions.PageRequest(2, 2), Expected: expected[2..]),
            (Request: new Sidequest.Application.Abstractions.PageRequest(3, 2), Expected: Array.Empty<Guid>())
        };
        var sut = context.Service(actor);

        foreach (var testCase in cases)
        {
            var result = await sut.ListAsync(EventListKind.All, testCase.Request);
            Assert.Equal(testCase.Expected, result.Items.Select(x => x.Id));
            Assert.Equal(3, result.TotalCount);
            Assert.Equal(testCase.Request.Page, result.Page);
            Assert.Equal(testCase.Request.PageSize, result.PageSize);
            Assert.DoesNotContain(result.Items, x => x.Id == filteredTerminal.Id);
        }

        Assert.Equal(expected, (await sut.ListAsync(EventListKind.All, new(1, 100))).Items.Select(x => x.Id));
    }

    /// <summary>Limits unjoined discovery to public fields while proving that active membership exposes the corresponding protected projection.</summary>
    /// <returns>A task completing after exact list versions and detail descriptions are compared for joined and unjoined Events.</returns>
    [Fact]
    public async Task ListAllHidesMemberOnlyProjectionFromUnjoinedRows()
    {
        var context = new EventTestContext(database);
        var actor = FoundationSeed.NewUser();
        var creator = FoundationSeed.NewUser();
        creator.TenantId = actor.TenantId;
        await FoundationSeed.PersistAsync(database, actor, creator);
        var unjoined = Candidate(creator.Id, "Public discovery only");
        unjoined.Description = "Unjoined private description";
        unjoined.DiscoverySummary = "Unjoined public summary";
        var joined = Candidate(creator.Id, "Member projection");
        joined.Description = "Joined private description";
        joined.DiscoverySummary = "Joined public summary";
        await FoundationSeed.PersistAsync(database, unjoined, joined);
        await FoundationSeed.PersistAsync(database, Membership(joined, actor));
        var sut = context.Service(actor);

        var result = await sut.ListAsync(EventListKind.All, new(1, 100));

        var unjoinedSummary = Assert.Single(result.Items, x => x.Id == unjoined.Id);
        var joinedSummary = Assert.Single(result.Items, x => x.Id == joined.Id);
        Assert.Equal("Unjoined public summary", unjoinedSummary.DiscoverySummary);
        Assert.False(unjoinedSummary.IsMember);
        Assert.Empty(unjoinedSummary.Version);
        Assert.Equal("Joined public summary", joinedSummary.DiscoverySummary);
        Assert.True(joinedSummary.IsMember);
        Assert.Equal(Convert.ToBase64String(joined.Version), joinedSummary.Version);
        var unjoinedDetail = await sut.GetAsync(unjoined.Id);
        var joinedDetail = await sut.GetAsync(joined.Id);
        Assert.Null(unjoinedDetail.Description);
        Assert.Equal("Joined private description", joinedDetail.Description);
        Assert.Empty(unjoinedDetail.Summary.Version);
        Assert.Equal(joinedSummary.Version, joinedDetail.Summary.Version);
    }

    private static EventMembership Membership(Event item, UserAccount user,
        MembershipStatus status = MembershipStatus.Active) => new()
    {
        EventId = item.Id,
        UserId = user.Id,
        Status = status,
        ChangedById = user.Id,
        ChangedUtc = FoundationSeed.Now
    };

    /// <summary>An undefined list discriminator is rejected at the public Event service boundary with its stable field association.</summary>
    /// <returns>A task completing after the exact validation category and field are observed.</returns>
    [Fact]
    public async Task ListRejectsUndefinedKindAsValidationOnKind()
    {
        var context = new EventTestContext(database);
        var actor = FoundationSeed.NewUser();
        var sut = context.Service(actor);

        var exception = await Assert.ThrowsAsync<DomainException>(() =>
            sut.ListAsync((EventListKind)int.MaxValue, new(1, 25)));

        Assert.Equal(ErrorCode.Validation, exception.Code);
        Assert.Equal("Kind", exception.Field);
        Assert.Equal("Choose a valid Event list.", exception.Message);
    }
}
