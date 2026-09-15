using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Web.Authentication;
using Sidequest.Web.Experience;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Runs the snapshot HTTP boundary with real cookie authentication and a narrowly recorded application-query double.</summary>
public sealed class OfflineSnapshotEndpointTests
{
    private static readonly DateTimeOffset SessionIssuedUtc = DateTimeOffset.UtcNow;

    /// <summary>Real cookie authentication is necessary but insufficient: only the circuit's exact identity and sign-in instance can pass the HTTP comparison.</summary>
    /// <param name="transition">The cookie or proof change after the circuit captures its server-generated binding.</param>
    /// <returns>Completion after matching/mismatching HTTP status, empty no-store response and independently valid replacement-cookie assertions.</returns>
    [Theory]
    [InlineData("unchanged")]
    [InlineData("different-account")]
    [InlineData("different-account-same-session")]
    [InlineData("new-session-same-account")]
    [InlineData("missing-proof")]
    [InlineData("tampered-proof")]
    [InlineData("oversized-proof")]
    [InlineData("legacy-session")]
    public async Task SessionCheckRequiresMatchingCookieIdentityAndSignInInstance(string transition)
    {
        await using var host = await SnapshotHost.StartAsync();
        using (var signIn = await host.Client.PostAsync("/test-session/first", null)) signIn.EnsureSuccessStatusCode();
        var proof = await host.Client.GetStringAsync("/test-circuit-binding");
        Assert.NotEmpty(proof);
        var differentAccount = transition is "different-account" or "different-account-same-session";
        if (differentAccount || transition is "new-session-same-account" or "legacy-session")
        {
            var account = differentAccount ? "second" : transition == "legacy-session" ? "legacy" : "first";
            var query = transition == "different-account-same-session" ? "?reuseSession=true" : "";
            using var signIn = await host.Client.PostAsync($"/test-session/{account}{query}", null);
            signIn.EnsureSuccessStatusCode();
        }
        var supplied = transition switch
        {
            "missing-proof" => null,
            "tampered-proof" => "invalid-" + proof,
            "oversized-proof" => new string('x', 2049),
            _ => proof
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/experience/session");
        if (supplied is not null) request.Headers.Add(ExperienceSessionBinding.HeaderName, supplied);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(transition == "unchanged" ? HttpStatusCode.NoContent : HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Null(response.Headers.Location);
        Assert.Equal(0, host.Probe.QueryCalls);

        if (differentAccount || transition == "new-session-same-account")
        {
            var replacementProof = await host.Client.GetStringAsync("/test-circuit-binding");
            using var fresh = new HttpRequestMessage(HttpMethod.Get, "/experience/session");
            fresh.Headers.Add(ExperienceSessionBinding.HeaderName, replacementProof);
            using var matched = await host.Client.SendAsync(fresh);
            Assert.Equal(HttpStatusCode.NoContent, matched.StatusCode);
            using var snapshot = await host.Client.GetAsync("/experience/joined-snapshot");
            snapshot.EnsureSuccessStatusCode();
            using var data = JsonDocument.Parse(await snapshot.Content.ReadAsStringAsync());
            Assert.Equal(differentAccount ? SnapshotProbe.SecondAccount : SnapshotProbe.FirstAccount,
                data.RootElement.GetProperty("accountId").GetGuid());
        }

        using var anonymous = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = host.Client.BaseAddress };
        using var proofOnly = new HttpRequestMessage(HttpMethod.Get, "/experience/session");
        proofOnly.Headers.Add(ExperienceSessionBinding.HeaderName, proof);
        using var denied = await anonymous.SendAsync(proofOnly);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    /// <summary>The public root worker serves the exact feature source with root scope, JavaScript MIME and update-friendly caching, without querying protected data.</summary>
    /// <returns>Completion after a real anonymous HTTP request and exact body/header assertions.</returns>
    [Fact]
    public async Task RootWorkerServesExactPublicSourceWithoutAuthentication()
    {
        await using var host = await SnapshotHost.StartAsync();
        using var response = await host.Client.GetAsync("/service-worker.js");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoCache);
        Assert.Equal("/", Assert.Single(response.Headers.GetValues("Service-Worker-Allowed")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(SnapshotHost.WebRoot, "experience", "service-worker.js")),
            await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, host.Probe.QueryCalls);
    }

    /// <summary>The real antiforgery logout endpoint removes its cookie even when client clearing failed, preserving failure guidance in the local redirect.</summary>
    /// <returns>Completion after real cookie sign-in, token issuance, logout and subsequent current-session denial.</returns>
    [Fact]
    public async Task LogoutStillRemovesCookieAndRetainsDeviceClearFailureWarning()
    {
        await using var host = await SnapshotHost.StartAsync();
        using (var signIn = await host.Client.PostAsync("/test-session/first", null)) signIn.EnsureSuccessStatusCode();
        var token = await host.Client.GetStringAsync("/test-antiforgery");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["experienceClearFailed"] = "true"
        });
        using var logout = await host.Client.PostAsync("/auth/logout", form);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/?deviceClearFailed=true", logout.Headers.Location!.OriginalString);
        using var session = await host.Client.GetAsync("/experience/session");
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        Assert.Equal(0, host.Probe.QueryCalls);
    }

    /// <summary>Current cookies govern account identity and exact minimal fields, independent of another scope's stale circuit principal.</summary>
    /// <param name="customHostJson">Whether unrelated HTTP JSON uses reference metadata, different property names and string enums.</param>
    /// <returns>Completion after exact raw HTTP schemas unaffected by host policy, anonymous denial, two accounts, no-store headers and scoped identity assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotUsesCurrentCookieNotStaleCircuitPrincipal(bool customHostJson)
    {
        await using var host = await SnapshotHost.StartAsync(customHostJson);
        using (var policy = JsonDocument.Parse(await host.Client.GetStringAsync("/test-json-policy")))
        {
            Assert.Equal(customHostJson, policy.RootElement.TryGetProperty("$id", out _));
            var status = policy.RootElement.GetProperty(customHostJson ? "public_status" : "publicStatus");
            if (customHostJson) Assert.Equal("Active", status.GetString());
            else Assert.Equal((int)QuestStatus.Active, status.GetInt32());
        }
        using (var anonymous = await host.Client.GetAsync("/experience/joined-snapshot"))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var anonymousSession = await host.Client.GetAsync("/experience/session"))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousSession.StatusCode);
        Assert.Equal(0, host.Probe.QueryCalls);
        await using var circuit = host.Services.CreateAsyncScope();
        var state = circuit.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        var oldPrincipal = Principal("first");
        ((IHostEnvironmentAuthenticationStateProvider)state).SetAuthenticationState(Task.FromResult(new AuthenticationState(oldPrincipal)));
        using (var signIn = await host.Client.PostAsync("/test-session/first", null)) signIn.EnsureSuccessStatusCode();
        using var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/experience/session");
        sessionRequest.Headers.Add(ExperienceSessionBinding.HeaderName, await host.Client.GetStringAsync("/test-circuit-binding"));
        using (var session = await host.Client.SendAsync(sessionRequest))
        {
            Assert.Equal(HttpStatusCode.NoContent, session.StatusCode);
            Assert.True(session.Headers.CacheControl!.NoStore);
            Assert.Empty(await session.Content.ReadAsStringAsync());
        }
        using (var response = await host.Client.GetAsync("/experience/joined-snapshot"))
        {
            response.EnsureSuccessStatusCode();
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Null(response.Headers.ETag);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var snapshot = document.RootElement;
            Assert.Equal(SnapshotProbe.FirstAccount, snapshot.GetProperty("accountId").GetGuid());
            Assert.Equal(new[] { "accountId", "quests", "refreshedUtc" }, snapshot.EnumerateObject().Select(p => p.Name).Order());
            var quest = Assert.Single(snapshot.GetProperty("quests").EnumerateArray());
            Assert.Equal("Joined Quest", quest.GetProperty("title").GetString());
            Assert.Equal((int)QuestStatus.Active, quest.GetProperty("status").GetInt32());
            Assert.Equal(new[] { "endUtc", "eventId", "id", "location", "startUtc", "status", "timeZoneId", "title" },
                quest.EnumerateObject().Select(p => p.Name).Order());
        }
        using (var signIn = await host.Client.PostAsync("/test-session/second", null)) signIn.EnsureSuccessStatusCode();
        using (var response = await host.Client.GetAsync("/experience/joined-snapshot"))
        {
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "accountId", "quests", "refreshedUtc" },
                document.RootElement.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(SnapshotProbe.SecondAccount, document.RootElement.GetProperty("accountId").GetGuid());
            Assert.Empty(document.RootElement.GetProperty("quests").EnumerateArray());
        }
        Assert.Equal(2, host.Probe.QueryCalls);
        Assert.True(host.Probe.RequestTokenMatched);
        Assert.Same(oldPrincipal, (await state.GetAuthenticationStateAsync()).User);
    }

    private static ClaimsPrincipal Principal(string account)
    {
        var persona = DevelopmentPersonas.All.Single(p => p.Name == (account == "second" ? "Bob" : "Alice"));
        var principal = DevelopmentPersonas.CreatePrincipal(persona);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim("account", account));
        WorkforceSession.Stamp(principal, SessionIssuedUtc);
        if (account == "legacy") identity.RemoveClaim(identity.FindFirst(WorkforceSession.IdClaim)!);
        return principal;
    }

    private sealed class SnapshotProbe
    {
        internal static readonly Guid FirstAccount = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        internal static readonly Guid SecondAccount = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        internal int QueryCalls { get; private set; }
        internal bool RequestTokenMatched { get; private set; } = true;
        internal async Task<UserAccount> UserAsync(IServiceProvider services)
        {
            var principal = (await services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync()).User;
            return new() { Id = principal.FindFirstValue("account") == "first" ? FirstAccount : SecondAccount };
        }
        internal async Task<IReadOnlyList<OfflineQuest>> JoinedAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            QueryCalls++;
            RequestTokenMatched &= cancellationToken == services.GetRequiredService<IHttpContextAccessor>().HttpContext!.RequestAborted;
            var user = await UserAsync(services);
            return user.Id == FirstAccount ?
                [new(Guid.NewGuid(), Guid.NewGuid(), "Joined Quest", "Room", DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddHours(1), "Etc/UTC", QuestStatus.Active)] : [];
        }
    }

    private sealed class SnapshotHost(WebApplication app, HttpClient client, SnapshotProbe probe) : IAsyncDisposable
    {
        internal static string WebRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "Sidequest.Web", "wwwroot"));
        internal HttpClient Client { get; } = client;
        internal IServiceProvider Services => app.Services;
        internal SnapshotProbe Probe { get; } = probe;
        internal static async Task<SnapshotHost> StartAsync(bool customHostJson = false)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { WebRootPath = WebRoot });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddAuthentication(FoundationAuthenticationSettings.CookieScheme)
                .AddCookie(FoundationAuthenticationSettings.CookieScheme);
            builder.Services.AddAuthorization();
            builder.Services.AddAntiforgery();
            if (customHostJson)
            {
                builder.Services.ConfigureHttpJsonOptions(options =>
                {
                    options.SerializerOptions.ReferenceHandler = ReferenceHandler.Preserve;
                    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
                });
            }
            var authentication = new FoundationAuthenticationSettings(true, DevelopmentPersonas.TenantId, DevelopmentPersonas.WorkforceRole, null);
            builder.Services.AddSingleton(authentication);
            builder.Services.AddSingleton<ExperienceSessionBinding>();
            builder.Services.AddScoped<WorkforceAccounts>();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();
            var probe = new SnapshotProbe();
            var db = SnapshotServiceProxy.Create<ISidequestDbContext>((method, _) =>
                method.Name == nameof(IAsyncDisposable.DisposeAsync) ? ValueTask.CompletedTask : throw new NotSupportedException());
            builder.Services.AddScoped(_ => SnapshotServiceProxy.Create<ISidequestDbContextFactory>((_, _) => Task.FromResult(db)));
            builder.Services.AddScoped(sp => SnapshotServiceProxy.Create<IResourceAccess>((method, _) =>
                method.Name == nameof(IResourceAccess.RequireUserAsync) ? probe.UserAsync(sp) : throw new NotSupportedException()));
            builder.Services.AddScoped(sp => SnapshotServiceProxy.Create<IQuestService>((method, args) =>
                method.Name == nameof(IQuestService.GetOfflineJoinedAsync) ?
                    probe.JoinedAsync(sp, (CancellationToken)args![0]!) : throw new NotSupportedException()));
            builder.Services.AddSingleton(TimeProvider.System);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseAntiforgery();
            app.MapGet("/test-json-policy", () => Results.Json(new { PublicStatus = QuestStatus.Active }));
            app.MapPost("/test-session/{account}", async (string account, bool? reuseSession, HttpContext context) =>
            {
                var principal = Principal(account);
                if (reuseSession == true)
                {
                    var id = context.User.FindFirstValue(WorkforceSession.IdClaim) ??
                        throw new InvalidOperationException("The test requires an existing authenticated session.");
                    var identity = (ClaimsIdentity)principal.Identity!;
                    identity.RemoveClaim(identity.FindFirst(WorkforceSession.IdClaim)!);
                    identity.AddClaim(new Claim(WorkforceSession.IdClaim, id));
                }
                await context.SignInAsync(FoundationAuthenticationSettings.CookieScheme, principal);
                return Results.NoContent();
            });
            app.MapSidequestExperience();
            app.MapGet("/test-circuit-binding", (HttpContext context, ExperienceSessionBinding binding) =>
                Results.Text(binding.Create(context.User) ?? "")).RequireAuthorization();
            app.MapFoundationAuthentication(authentication);
            app.MapGet("/test-antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
                Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken!));
            try
            {
                await app.StartAsync();
                return new(app, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                {
                    BaseAddress = new Uri(app.Urls.Single()),
                    Timeout = TimeSpan.FromSeconds(30)
                }, probe);
            }
            catch { await app.DisposeAsync(); throw; }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
