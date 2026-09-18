using System.Net;
using System.Net.Http.Headers;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Directory;

namespace Sidequest.UnitTests.CoreEvents;

/// <summary>Exercises the real Graph adapter with scripted HTTP and an explicitly approved synthetic workforce policy.</summary>
public sealed class GraphDirectoryGatewayTests
{
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid User = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Group = Guid.Parse("30000000-0000-0000-0000-000000000003");

    internal static GraphDirectoryOptions Policy(bool approved = true) => new()
    {
        TenantId = Tenant, WorkforcePolicyApproved = approved,
        WorkforceExtension = "extension_test_workforce", WorkforceValue = "true"
    };

    private static string Person(Guid id, string policy = "true", string enabled = "true", string type = "Member") =>
        $$"""{"id":"{{id}}","displayName":"Ada","mail":null,"userPrincipalName":"not-mail@example.invalid","userType":"{{type}}","accountEnabled":{{enabled}},"extension_test_workforce":{{policy}}}""";

    /// <summary>Accepts only enabled explicitly approved participants, including guests, without manufacturing workforce evidence.</summary>
    /// <param name="listed">Whether the object was approved in the authoritative host list.</param>
    /// <param name="enabled">Graph account-enabled flag.</param>
    /// <param name="type">Graph user type.</param>
    /// <param name="expected">Expected participant eligibility.</param>
    /// <returns>A task completing after the real Graph parser applies the participant policy.</returns>
    [Theory]
    [InlineData(true, "true", "Guest", true)]
    [InlineData(true, "true", "Member", true)]
    [InlineData(false, "true", "Guest", false)]
    [InlineData(false, "true", "Member", false)]
    [InlineData(true, "false", "Guest", false)]
    [InlineData(true, "true", "Unknown", false)]
    public async Task HackathonDirectoryRequiresApprovedEnabledParticipant(bool listed, string enabled, string type, bool expected)
    {
        var source = new HashSet<Guid> { listed ? User : Guid.NewGuid() };
        var policy = new GraphDirectoryOptions(Tenant, source);
        source.Clear();
        using var handler = new ControlledHttpHandler((_, _) => Task.FromResult(
            ControlledHttpHandler.Json(Person(User, "null", enabled, type))));
        using var http = new HttpClient(handler);
        var result = await new GraphDirectoryGateway(http, new TokenStub(), policy, new ControlledTimeProvider()).GetUserAsync(User);
        Assert.Equal(User, result.ObjectId);
        Assert.Equal(Tenant, result.TenantId);
        Assert.Equal(expected, result.IsEligible);
        Assert.DoesNotContain("extension_", Assert.Single(handler.Requests).Uri);
        Assert.Equal("", result.Email);
        Assert.Single(policy.HackathonParticipants);
    }

    /// <summary>Rejects an empty participant policy without obtaining a token or issuing Graph requests.</summary>
    /// <returns>A task completing after the fail-closed policy guard.</returns>
    [Fact]
    public async Task HackathonDirectoryEmptyPolicyFailsBeforeHttp()
    {
        using var handler = new ControlledHttpHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var http = new HttpClient(handler);
        var tokens = new TokenStub();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new GraphDirectoryGateway(http, tokens, new GraphDirectoryOptions(Tenant, new HashSet<Guid>()),
                new ControlledTimeProvider()).GetUserAsync(User));
        Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, tokens.Calls);
    }

    /// <summary>Applies the same authoritative participant intersection to complete group expansion, not just direct lookups.</summary>
    /// <returns>A task completing after an approved guest survives and an unlisted enabled member is excluded.</returns>
    [Fact]
    public async Task HackathonGroupExpansionExcludesUnlistedIdentities()
    {
        var unlisted = Guid.Parse("20000000-0000-0000-0000-000000000003");
        var replies = new Queue<string>([
            $$"""{"id":"{{Group}}","displayName":"Team","securityEnabled":true,"groupTypes":[]}""",
            $$"""{"value":[{{Person(User, "null", "true", "Guest")}},{{Person(unlisted)}}]}"""
        ]);
        using var handler = new ControlledHttpHandler((_, _) => Task.FromResult(ControlledHttpHandler.Json(replies.Dequeue())));
        using var http = new HttpClient(handler);
        var result = await new GraphDirectoryGateway(http, new TokenStub(),
            new GraphDirectoryOptions(Tenant, new HashSet<Guid> { User }), new ControlledTimeProvider()).ExpandGroupAsync(Group);
        Assert.Equal(User, Assert.Single(result).ObjectId);
        Assert.True(result[0].IsEligible);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Empty(replies);
    }

    /// <summary>Enumerates transitive users across trusted continuations and deduplicates overlapping pages for both supported group kinds.</summary>
    /// <param name="security">Whether the selected group is security-enabled.</param>
    /// <param name="types">Graph group type array, including Unified for Microsoft 365 groups.</param>
    /// <returns>A task completing after all pages and identity assertions.</returns>
    [Theory]
    [InlineData(true, "[]")]
    [InlineData(false, "[\"Unified\"]")]
    public async Task ExpandSupportedGroupReadsAllTransitivePagesAndDeduplicates(bool security, string types)
    {
        var second = Guid.Parse("20000000-0000-0000-0000-000000000003");
        var next = $"https://graph.microsoft.com/v1.0/groups/{Group}/transitiveMembers/microsoft.graph.user?$skiptoken=next";
        var replies = new Queue<string>([
            $$"""{"id":"{{Group}}","displayName":"Team","securityEnabled":{{security.ToString().ToLowerInvariant()}},"groupTypes":{{types}}}""",
            $$"""{"value":[{{Person(User)}}],"@odata.nextLink":"{{next}}"}""",
            $$"""{"value":[{{Person(User)}},{{Person(second)}}]}"""
        ]);
        using var handler = new ControlledHttpHandler((_, _) => Task.FromResult(ControlledHttpHandler.Json(replies.Dequeue())));
        using var http = new HttpClient(handler);
        var tokens = new TokenStub();
        var sut = new GraphDirectoryGateway(http, tokens, Policy(), new ControlledTimeProvider());

        var result = await sut.ExpandGroupAsync(Group);

        Assert.Equal(new[] { User, second }, result.Select(x => x.ObjectId));
        Assert.All(result, x => { Assert.Equal(Tenant, x.TenantId); Assert.True(x.IsEligible); Assert.Equal("", x.Email); });
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("/transitiveMembers/microsoft.graph.user?", handler.Requests[1].Uri);
        Assert.Equal(next, handler.Requests[2].Uri);
        Assert.All(handler.Requests, x => Assert.Equal("Bearer controlled-token", x.Authorization));
        Assert.Equal(3, tokens.Calls);
        Assert.Empty(replies);
    }

    /// <summary>Fails closed before token or HTTP access when the workforce policy has not been explicitly approved.</summary>
    /// <returns>A task completing after the safe dependency failure is asserted.</returns>
    [Fact]
    public async Task UnapprovedPolicyFailsBeforeAnyProviderCall()
    {
        using var handler = new ControlledHttpHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var http = new HttpClient(handler);
        var token = new TokenStub();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new GraphDirectoryGateway(http, token, Policy(false), new ControlledTimeProvider()).GetUserAsync(User));
        Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
        Assert.Contains("not configured or approved", error.Message);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, token.Calls);
    }

    /// <summary>Requires enabled Member accounts and an exact approved extension; a UPN never becomes delivery email.</summary>
    /// <param name="policy">JSON eligibility extension value.</param>
    /// <param name="enabled">JSON account-enabled value.</param>
    /// <param name="type">Graph user type.</param>
    /// <param name="eligible">Expected workforce admission decision.</param>
    /// <returns>A task completing after identity and admission are checked.</returns>
    [Theory]
    [InlineData("true", "true", "Member", true)]
    [InlineData("false", "true", "Member", false)]
    [InlineData("null", "true", "Member", false)]
    [InlineData("\"True\"", "true", "Member", false)]
    [InlineData("true", "false", "Member", false)]
    [InlineData("true", "true", "Guest", false)]
    public async Task GetUserEnforcesWorkforceGateAndDoesNotUseUpn(string policy, string enabled, string type, bool eligible)
    {
        using var handler = new ControlledHttpHandler((_, _) => Task.FromResult(ControlledHttpHandler.Json(Person(User, policy, enabled, type))));
        using var http = new HttpClient(handler);
        var result = await new GraphDirectoryGateway(http, new TokenStub(), Policy(), new ControlledTimeProvider()).GetUserAsync(User);
        Assert.Equal(User, result.ObjectId);
        Assert.Equal(Tenant, result.TenantId);
        Assert.Equal("Ada", result.DisplayName);
        Assert.Equal(eligible, result.IsEligible);
        Assert.Equal("", result.Email);
        Assert.Contains($"users/{User}", Assert.Single(handler.Requests).Uri);
    }

    /// <summary>Rejects malformed or denied Graph responses rather than returning partial directory success.</summary>
    /// <param name="body">Controlled provider response.</param>
    /// <param name="status">Controlled HTTP status.</param>
    /// <returns>A task completing after failure classification and single-call behavior are checked.</returns>
    [Theory]
    [InlineData("{", HttpStatusCode.OK)]
    [InlineData("{}", HttpStatusCode.OK)]
    [InlineData("[]", HttpStatusCode.OK)]
    [InlineData("null", HttpStatusCode.OK)]
    [InlineData("{\"id\":\"not-a-guid\"}", HttpStatusCode.OK)]
    [InlineData("{\"error\":\"private provider diagnostic\"}", HttpStatusCode.Forbidden)]
    public async Task MalformedOrDeniedUserResponseFailsExplicitly(string body, HttpStatusCode status)
    {
        using var handler = new ControlledHttpHandler((_, _) => Task.FromResult(ControlledHttpHandler.Json(body, status)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new GraphDirectoryGateway(http, new TokenStub(), Policy(), new ControlledTimeProvider()).GetUserAsync(User));
        Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
        Assert.DoesNotContain("private provider diagnostic", error.Message);
        Assert.Single(handler.Requests);
    }

    /// <summary>Rejects an incomplete second page or an untrusted continuation without returning the first page's valid user.</summary>
    /// <param name="continuation">Continuation location returned by Graph.</param>
    /// <param name="expectedCalls">Number of allowed HTTP requests before failure.</param>
    /// <returns>A task completing after the all-or-nothing expansion assertion.</returns>
    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/groups/30000000-0000-0000-0000-000000000003/transitiveMembers/microsoft.graph.user?$skiptoken=next", 3)]
    [InlineData("https://untrusted.invalid/steal", 2)]
    [InlineData("https://graph.microsoft.com/v1.0/users", 2)]
    public async Task ExpansionRejectsIncompleteOrUntrustedContinuation(string continuation, int expectedCalls)
    {
        var replies = new Queue<string>([
            $$"""{"id":"{{Group}}","displayName":"Team","securityEnabled":true,"groupTypes":[]}""",
            $$"""{"value":[{{Person(User)}}],"@odata.nextLink":"{{continuation}}"}""", "{}"
        ]);
        using var handler = new ControlledHttpHandler((_, _) => Task.FromResult(ControlledHttpHandler.Json(replies.Dequeue())));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new GraphDirectoryGateway(http, new TokenStub(), Policy(), new ControlledTimeProvider()).ExpandGroupAsync(Group));
        Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
        Assert.Equal(expectedCalls, handler.Requests.Count);
        Assert.All(handler.Requests, x => Assert.StartsWith("https://graph.microsoft.com/", x.Uri));
    }

    /// <summary>Schedules the exact Retry-After delay and does not retry until the controlled clock releases it.</summary>
    /// <returns>A task completing after the second response is consumed without wall-clock waiting.</returns>
    [Fact]
    public async Task ThrottlingWaitsForExactRetryAfterBeforeRetrying()
    {
        var clock = new ControlledTimeProvider();
        var calls = 0;
        using var handler = new ControlledHttpHandler((_, _) =>
        {
            var response = ControlledHttpHandler.Json(Person(User), calls++ == 0 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var pending = new GraphDirectoryGateway(http, new TokenStub(), Policy(), clock).GetUserAsync(User);
        Assert.Equal(TimeSpan.FromSeconds(17), await clock.TimerCreated.Task);
        Assert.False(pending.IsCompleted);
        Assert.Single(handler.Requests);
        clock.Elapse(TimeSpan.FromSeconds(17));
        var result = await pending;
        Assert.Equal(User, result.ObjectId);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>Rejects unsupported distribution-only groups without ever enumerating their members.</summary>
    /// <returns>A task completing after the group policy rejection and request-count assertion.</returns>
    [Fact]
    public async Task UnsupportedGroupNeverEnumeratesMembers()
    {
        using var handler = new ControlledHttpHandler((_, _) => Task.FromResult(ControlledHttpHandler.Json(
            $$"""{"id":"{{Group}}","displayName":"Distribution only","securityEnabled":false,"groupTypes":[]}""")));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new GraphDirectoryGateway(http, new TokenStub(), Policy(), new ControlledTimeProvider()).ExpandGroupAsync(Group));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal("Group", error.Field);
        Assert.DoesNotContain("transitiveMembers", Assert.Single(handler.Requests).Uri);
    }

    /// <summary>Rejects the empty object identifier locally, without requesting a token or sending HTTP.</summary>
    /// <returns>A task completing after both user and group identifier guard assertions.</returns>
    [Fact]
    public async Task EmptyDirectoryIdsFailBeforeTokenAcquisition()
    {
        using var handler = new ControlledHttpHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        using var http = new HttpClient(handler);
        var tokens = new TokenStub();
        var sut = new GraphDirectoryGateway(http, tokens, Policy(), new ControlledTimeProvider());
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() => sut.GetUserAsync(Guid.Empty))).Code);
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() => sut.ExpandGroupAsync(Guid.Empty))).Code);
        Assert.Equal(0, tokens.Calls);
        Assert.Empty(handler.Requests);
    }

    /// <summary>Excludes users lacking the approved extension while retaining a missing mailbox as an explicit empty address.</summary>
    /// <returns>A task completing after direct admission and search filtering checks.</returns>
    [Fact]
    public async Task MissingWorkforceExtensionFailsClosedAndSearchEscapesApostrophes()
    {
        var missing = Person(User).Replace(",\"extension_test_workforce\":true", "", StringComparison.Ordinal);
        var second = Guid.Parse("20000000-0000-0000-0000-000000000003");
        var replies = new Queue<string>([missing, $$"""{"value":[{{missing}},{{Person(second)}}]}"""]);
        using var handler = new ControlledHttpHandler((_, _) => Task.FromResult(ControlledHttpHandler.Json(replies.Dequeue())));
        using var http = new HttpClient(handler);
        var sut = new GraphDirectoryGateway(http, new TokenStub(), Policy(), new ControlledTimeProvider());
        Assert.False((await sut.GetUserAsync(User)).IsEligible);
        var result = await sut.SearchUsersAsync("O'Brien");
        Assert.Equal(second, Assert.Single(result).ObjectId);
        Assert.Equal("", result[0].Email);
        Assert.Contains("startswith(displayName,'O''Brien')", Uri.UnescapeDataString(handler.Requests[1].Uri));
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>Detects repeating pagination links rather than looping or returning a partially enumerated group.</summary>
    /// <returns>A task completing after bounded continuation failure.</returns>
    [Fact]
    public async Task CyclicContinuationFailsWithoutPartialSuccess()
    {
        var next = $"https://graph.microsoft.com/v1.0/groups/{Group}/transitiveMembers/microsoft.graph.user?$skiptoken=cycle";
        var page = $$"""{"value":[{{Person(User)}}],"@odata.nextLink":"{{next}}"}""";
        var replies = new Queue<string>([
            $$"""{"id":"{{Group}}","displayName":"Team","securityEnabled":true,"groupTypes":[]}""", page, page
        ]);
        using var handler = new ControlledHttpHandler((_, _) => Task.FromResult(ControlledHttpHandler.Json(replies.Dequeue())));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new GraphDirectoryGateway(http, new TokenStub(), Policy(), new ControlledTimeProvider()).ExpandGroupAsync(Group));
        Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
        Assert.Contains("incomplete", error.Message);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Empty(replies);
    }

    private sealed class TokenStub : IGraphAccessTokenProvider
    {
        internal int Calls { get; private set; }
        /// <inheritdoc />
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult("controlled-token");
        }
    }
}
