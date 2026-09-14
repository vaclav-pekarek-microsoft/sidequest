using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications;
using Sidequest.Domain.Rules;
using Sidequest.Web.Authentication;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.CoreHost;

/// <summary>Exercises the calendar HTTP boundary with real routing and request-scoped identity but no database or email provider.</summary>
public sealed class NotificationEndpointsTests
{
    private const string Calendar = "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nSUMMARY:Caf\u00e9\r\nEND:VCALENDAR\r\n";

    /// <summary>Verifies exact UTF-8 bytes, safe attachment headers, disabled ranges, and forwarding of the Quest and request cancellation token.</summary>
    /// <returns>A task completing after the authorized HTTP download is inspected.</returns>
    [Fact]
    public async Task AuthorizedDownloadReturnsExactUtf8AttachmentWithoutCachingOrRanges()
    {
        await using var host = await EndpointHost.StartAsync();
        var questId = Guid.NewGuid();
        using var request = Request(questId, "Alice");
        request.Headers.Range = new(0, 3);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes(Calendar), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("text/calendar", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.Contains(response.Content.Headers.ContentType.Parameters, value => value.Name == "method" && value.Value == "REQUEST");
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("sidequest.ics", response.Content.Headers.ContentDisposition.FileName);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
        Assert.Null(response.Content.Headers.ContentRange);
        var call = Assert.Single(host.Probe.Calls);
        Assert.Equal(questId, call.QuestId);
        Assert.Equal(DevelopmentPersonas.All.Single(persona => persona.Name == "Alice").ObjectId, call.Identity!.ObjectId);
        Assert.True(call.TokenMatchesRequest);
    }

    /// <summary>Verifies authorization middleware rejects anonymous requests before any application call.</summary>
    /// <returns>A task completing after the anonymous HTTP outcome is inspected.</returns>
    [Fact]
    public async Task AnonymousRequestCannotInvokeCalendarService()
    {
        await using var host = await EndpointHost.StartAsync();
        using var response = await host.Client.GetAsync($"/notifications/calendar/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.Probe.Calls);
    }

    /// <summary>Verifies malformed route identifiers never reach the application service.</summary>
    /// <returns>A task completing after the unmatched route is inspected.</returns>
    [Fact]
    public async Task InvalidQuestIdentifierDoesNotInvokeCalendarService()
    {
        await using var host = await EndpointHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/notifications/calendar/not-a-guid");
        request.Headers.Add("Test-Persona", "Alice");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(host.Probe.Calls);
    }

    /// <summary>Verifies domain failures retain their HTTP status without exposing private calendar or exception details.</summary>
    /// <param name="code">The application failure to return.</param>
    /// <param name="status">The expected safe HTTP status.</param>
    /// <returns>A task completing after the failure response is inspected.</returns>
    [Theory]
    [InlineData(ErrorCode.Validation, HttpStatusCode.BadRequest)]
    [InlineData(ErrorCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(ErrorCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData(ErrorCode.Conflict, HttpStatusCode.Conflict)]
    [InlineData(ErrorCode.DependencyUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task DomainFailureReturnsSafeNoncacheableResponse(ErrorCode code, HttpStatusCode status)
    {
        await using var host = await EndpointHost.StartAsync(failure: code);
        using var request = Request(Guid.NewGuid(), "Alice");
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("correlationId", body);
        Assert.DoesNotContain("private service failure", body);
        Assert.DoesNotContain("BEGIN:VCALENDAR", body);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Single(host.Probe.Calls);
    }

    /// <summary>Verifies separate HTTP requests do not reuse each other's identity or overwrite an existing independent circuit scope.</summary>
    /// <returns>A task completing after both requests and the independent authentication state are inspected.</returns>
    [Fact]
    public async Task RequestsUseIndependentIdentityWithoutChangingAnotherScope()
    {
        await using var host = await EndpointHost.StartAsync();
        await using var circuit = host.Services.CreateAsyncScope();
        var circuitState = circuit.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        var admin = DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All.Single(persona => persona.Name == "Admin"));
        ((IHostEnvironmentAuthenticationStateProvider)circuitState)
            .SetAuthenticationState(Task.FromResult(new AuthenticationState(admin)));

        foreach (var name in new[] { "Alice", "Bob" })
        {
            using var request = Request(Guid.NewGuid(), name);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(new[] { "Alice", "Bob" }.Select(name =>
            DevelopmentPersonas.All.Single(persona => persona.Name == name).ObjectId),
            host.Probe.Calls.Select(call => call.Identity!.ObjectId));
        Assert.Same(admin, (await circuitState.GetAuthenticationStateAsync()).User);
    }

    /// <summary>Verifies seeding request authentication state never renews an expired signed session.</summary>
    /// <returns>A task completing after the expired identity is rejected.</returns>
    [Fact]
    public async Task ExpiredRequestSessionIsNotExtendedByDownload()
    {
        await using var host = await EndpointHost.StartAsync();
        using var request = Request(Guid.NewGuid(), "expired");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(Assert.Single(host.Probe.Calls).Identity);
        Assert.DoesNotContain("BEGIN:VCALENDAR", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Verifies a misconfigured non-host provider fails explicitly before calling the application.</summary>
    /// <returns>A task completing after the safe configuration failure is inspected.</returns>
    [Fact]
    public async Task IncompatibleAuthenticationProviderFailsClosed()
    {
        await using var host = await EndpointHost.StartAsync(brokenAuthentication: true);
        using var request = Request(Guid.NewGuid(), "Alice");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(host.Probe.Calls);
        Assert.DoesNotContain("host-initialized", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Verifies disconnect cancellation reaches the pending application operation rather than abandoning uncancelled work.</summary>
    /// <returns>A task completing after both client and service observe cancellation.</returns>
    [Fact]
    public async Task RequestCancellationReachesApplicationOperation()
    {
        await using var host = await EndpointHost.StartAsync(pause: true);
        using var cancellation = new CancellationTokenSource();
        using var request = Request(Guid.NewGuid(), "Alice");
        var response = host.Client.SendAsync(request, cancellation.Token);
        await host.Probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response);
        await host.Probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Assert.Single(host.Probe.Calls).TokenMatchesRequest);
    }

    private static HttpRequestMessage Request(Guid questId, string persona)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/notifications/calendar/{questId}");
        request.Headers.Add("Test-Persona", persona);
        return request;
    }

    private sealed class EndpointHost(WebApplication app, HttpClient client, DownloadProbe probe) : IAsyncDisposable
    {
        /// <summary>Gets the client connected only to this test's ephemeral loopback listener.</summary>
        public HttpClient Client { get; } = client;
        /// <summary>Gets the shared observation sink, not a shared current-user service.</summary>
        public DownloadProbe Probe { get; } = probe;
        /// <summary>Gets the host services for creating a separate simulated circuit scope.</summary>
        public IServiceProvider Services => app.Services;

        /// <summary>Starts a separately owned real HTTP pipeline with deterministic service outcomes and no SQL or provider calls.</summary>
        /// <param name="failure">An optional domain failure emitted after identity resolution.</param>
        /// <param name="pause">Whether to wait for request cancellation inside the service.</param>
        /// <param name="brokenAuthentication">Whether to register an incompatible provider for the configuration-failure test.</param>
        /// <returns>The owned host and loopback client.</returns>
        public static async Task<EndpointHost> StartAsync(ErrorCode? failure = null, bool pause = false, bool brokenAuthentication = false)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var probe = new DownloadProbe(failure, pause);
            builder.Services.AddSingleton(probe);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(new FoundationAuthenticationSettings(
                true, DevelopmentPersonas.TenantId, DevelopmentPersonas.WorkforceRole, null));
            builder.Services.AddScoped<AuthenticationStateProvider, WorkforceAuthenticationStateProvider>();
            if (brokenAuthentication)
            {
                builder.Services.AddScoped<AuthenticationStateProvider, IncompatibleAuthenticationState>();
            }
            builder.Services.AddScoped<ICurrentUser, CircuitCurrentUser>();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<INotificationService, RecordingNotifications>();
            builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, PersonaAuthenticationHandler>("test", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddExceptionHandler<SafeExceptionHandler>();
            builder.Services.AddProblemDetails();
            var app = builder.Build();
            app.UseExceptionHandler();
            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                await next(context);
            });
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapSidequestNotifications();
            try
            {
                await app.StartAsync();
                return new(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(20) }, probe);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class DownloadProbe(ErrorCode? failure, bool pause)
    {
        /// <summary>Gets the configured application error, if any.</summary>
        public ErrorCode? Failure { get; } = failure;
        /// <summary>Gets whether the service must remain pending until disconnected.</summary>
        public bool Pause { get; } = pause;
        /// <summary>Gets captured calls, including resolved identities and token forwarding evidence.</summary>
        public ConcurrentQueue<(Guid QuestId, UserIdentity? Identity, bool TokenMatchesRequest)> Calls { get; } = new();
        /// <summary>Signals that the application operation has been entered.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Signals that the application operation observed cancellation.</summary>
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecordingNotifications(ICurrentUser current, IHttpContextAccessor http, DownloadProbe probe) : INotificationService
    {
        /// <inheritdoc/>
        public async Task<string> DownloadCalendarAsync(Guid questId, CancellationToken cancellationToken = default)
        {
            var identity = await current.GetIdentityAsync(cancellationToken);
            probe.Calls.Enqueue((questId, identity, cancellationToken.CanBeCanceled && cancellationToken == http.HttpContext!.RequestAborted));
            probe.Entered.TrySetResult();
            if (identity is null) throw new DomainException(ErrorCode.Forbidden, "private service failure");
            if (probe.Failure is { } code) throw new DomainException(code, "private service failure");
            if (probe.Pause)
            {
                using var registration = cancellationToken.Register(() => probe.Cancelled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return Calendar;
        }

        /// <inheritdoc/>
        public Task<PageResult<NotificationSummary>> ListAsync(PageRequest page, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <inheritdoc/>
        public Task<int> UnreadCountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <inheritdoc/>
        public Task MarkReadAsync(Guid? notificationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <inheritdoc/>
        public Task<PreferenceInput> GetPreferencesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <inheritdoc/>
        public Task SavePreferencesAsync(PreferenceInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <inheritdoc/>
        public Task SetEventNewQuestEmailAsync(Guid eventId, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <inheritdoc/>
        public Task<IReadOnlyList<DeliveryFailure>> FailedDeliveriesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <inheritdoc/>
        public Task ReplayAsync(Guid deliveryId, string kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PersonaAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <inheritdoc/>
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var name = Request.Headers["Test-Persona"].ToString();
            if (name.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var persona = DevelopmentPersonas.All.Single(value => value.Name == (name == "expired" ? "Alice" : name));
            var principal = DevelopmentPersonas.CreatePrincipal(persona);
            WorkforceSession.Stamp(principal, DateTimeOffset.UtcNow.AddHours(name == "expired" ? -2 : 0));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class IncompatibleAuthenticationState : AuthenticationStateProvider
    {
        /// <inheritdoc/>
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal()));
    }
}
