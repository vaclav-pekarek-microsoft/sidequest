using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sidequest.Application.Media;
using Sidequest.Domain.Rules;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.SecondaryMedia;

/// <summary>Exercises real loopback HTTP routing, request-scoped auth initialization and private image headers.</summary>
public sealed class MediaEndpointTests
{
    /// <summary>Authenticated downloads return exact bytes, no-store/nosniff and no ranges while initializing only request scope.</summary>
    /// <returns>Completion after two independent identities and an unaffected circuit scope are verified.</returns>
    [Fact]
    public async Task Download_IsMediatedNoStoreNosniffAndRequestScoped()
    {
        await using var host = await Host.StartAsync();
        await using var circuit = host.App.Services.CreateAsyncScope();
        var state = circuit.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        ((IHostEnvironmentAuthenticationStateProvider)state).SetAuthenticationState(Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "circuit")], "test")))));
        var id = Guid.NewGuid();
        foreach (var name in new[] { "first", "second" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/media/covers/{id}?moderation=true");
            request.Headers.Add("Test-Name", name);
            request.Headers.Range = new(0, 1);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Null(response.Headers.ETag);
            Assert.Null(response.Content.Headers.ContentRange);
            Assert.Null(response.Content.Headers.LastModified);
            Assert.Null(response.Content.Headers.ContentDisposition);
        }
        Assert.Equal(new[] { "first", "second" }, host.Calls.Select(x => x.Name));
        Assert.All(host.Calls, call =>
        {
            Assert.Equal(id, call.Id);
            Assert.True(call.Moderation);
            Assert.True(call.TokenMatches);
        });
        Assert.Equal("circuit", (await state.GetAuthenticationStateAsync()).User.Identity!.Name);
    }

    /// <summary>Anonymous and invalid identifier requests never invoke the application read boundary.</summary>
    /// <returns>Completion after middleware and route constraints reject both requests.</returns>
    [Fact]
    public async Task AnonymousAndMalformedRequests_DoNotReachApplication()
    {
        await using var host = await Host.StartAsync();
        using var anonymous = await host.Client.GetAsync($"/media/covers/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/media/covers/not-a-guid");
        request.Headers.Add("Test-Name", "first");
        using var malformed = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        Assert.Empty(host.Calls);
    }

    /// <summary>Authorization and provider failures remain noncacheable, nonsniffable and redacted through the real exception middleware.</summary>
    /// <param name="code">Expected application failure.</param>
    /// <param name="status">Safe status produced by the host exception handler.</param>
    /// <returns>Completion after response status, headers and non-disclosure are checked.</returns>
    [Theory]
    [InlineData(ErrorCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData(ErrorCode.DependencyUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task FailedDownload_IsRedactedAndCannotBeCachedOrSniffed(ErrorCode code, HttpStatusCode status)
    {
        await using var host = await Host.StartAsync(code);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/media/covers/{Guid.NewGuid()}");
        request.Headers.Add("Test-Name", "first");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("private-provider-sentinel", await response.Content.ReadAsStringAsync());
    }

    private sealed record Call(Guid Id, string? Name, bool Moderation, bool TokenMatches);
    private sealed record Failure(ErrorCode? Code);

    private sealed class Host(WebApplication app, HttpClient client, ConcurrentQueue<Call> calls) : IAsyncDisposable
    {
        internal WebApplication App => app;
        internal HttpClient Client => client;
        internal ConcurrentQueue<Call> Calls => calls;

        internal static async Task<Host> StartAsync(ErrorCode? failure = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var calls = new ConcurrentQueue<Call>();
            builder.Services.AddSingleton(calls);
            builder.Services.AddSingleton(new Failure(failure));
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<AuthenticationStateProvider, ScopedState>();
            builder.Services.AddScoped<IMediaService, RecordingMedia>();
            builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddExceptionHandler<SafeExceptionHandler>();
            builder.Services.AddProblemDetails();
            var app = builder.Build();
            app.UseExceptionHandler();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapSidequestMedia();
            try
            {
                await app.StartAsync();
                return new(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) }, calls);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class ScopedState : AuthenticationStateProvider, IHostEnvironmentAuthenticationStateProvider
    {
        private Task<AuthenticationState> state = Task.FromResult(new AuthenticationState(new ClaimsPrincipal()));
        /// <inheritdoc />
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => state;
        /// <inheritdoc />
        public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask) => state = authenticationStateTask;
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <inheritdoc />
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var name = Request.Headers["Test-Name"].ToString();
            return Task.FromResult(string.IsNullOrEmpty(name) ? AuthenticateResult.NoResult() :
                AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class RecordingMedia(AuthenticationStateProvider state, IHttpContextAccessor http,
        ConcurrentQueue<Call> calls, Failure failure) : IMediaService
    {
        /// <inheritdoc />
        public async Task<MediaContent> ReadAsync(Guid assetId, bool moderation = false, CancellationToken cancellationToken = default)
        {
            calls.Enqueue(new(assetId, (await state.GetAuthenticationStateAsync()).User.Identity?.Name,
                moderation, cancellationToken == http.HttpContext!.RequestAborted));
            if (failure.Code is { } code)
                throw new DomainException(code, "private-provider-sentinel");
            return new([137, 80, 78, 71], "image/png");
        }
        /// <inheritdoc />
        public Task<CoverUpdate> UploadCoverAsync(Guid questId, string expectedVersion, Stream content, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Read endpoint cannot upload.");
        /// <inheritdoc />
        public Task<CoverUpdate> RemoveCoverAsync(Guid questId, string expectedVersion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Read endpoint cannot remove.");
    }
}
