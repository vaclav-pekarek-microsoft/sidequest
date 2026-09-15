using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Web.Authentication;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Runs the snapshot HTTP boundary with real cookie authentication and a narrowly recorded application-query double.</summary>
public sealed class OfflineSnapshotEndpointTests
{
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
    /// <returns>Completion after anonymous denial, two account snapshots, no-store headers and scoped identity assertions.</returns>
    [Fact]
    public async Task SnapshotUsesCurrentCookieNotStaleCircuitPrincipal()
    {
        await using var host = await SnapshotHost.StartAsync();
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
        using (var session = await host.Client.GetAsync("/experience/session"))
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
            Assert.Equal(new[] { "endUtc", "eventId", "id", "location", "startUtc", "status", "timeZoneId", "title" },
                quest.EnumerateObject().Select(p => p.Name).Order());
        }
        using (var signIn = await host.Client.PostAsync("/test-session/second", null)) signIn.EnsureSuccessStatusCode();
        using (var response = await host.Client.GetAsync("/experience/joined-snapshot"))
        {
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(SnapshotProbe.SecondAccount, document.RootElement.GetProperty("accountId").GetGuid());
            Assert.Empty(document.RootElement.GetProperty("quests").EnumerateArray());
        }
        Assert.Equal(2, host.Probe.QueryCalls);
        Assert.True(host.Probe.RequestTokenMatched);
        Assert.Same(oldPrincipal, (await state.GetAuthenticationStateAsync()).User);
    }

    private static ClaimsPrincipal Principal(string account) => new(new ClaimsIdentity([new Claim("account", account)], "test"));

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
        internal static async Task<SnapshotHost> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { WebRootPath = WebRoot });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddAuthentication(FoundationAuthenticationSettings.CookieScheme)
                .AddCookie(FoundationAuthenticationSettings.CookieScheme);
            builder.Services.AddAuthorization();
            builder.Services.AddAntiforgery();
            var authentication = new FoundationAuthenticationSettings(true, DevelopmentPersonas.TenantId, "Workforce", null);
            builder.Services.AddSingleton(authentication);
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
            app.MapPost("/test-session/{account}", async (string account, HttpContext context) =>
            {
                await context.SignInAsync(FoundationAuthenticationSettings.CookieScheme, Principal(account));
                return Results.NoContent();
            });
            app.MapSidequestExperience();
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
