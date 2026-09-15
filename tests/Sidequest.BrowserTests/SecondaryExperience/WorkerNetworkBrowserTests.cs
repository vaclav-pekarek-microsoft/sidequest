using System.Text.Json;
using Microsoft.Playwright;
using Sidequest.BrowserTests.FoundationBrowser;
using static Microsoft.Playwright.Assertions;

namespace Sidequest.BrowserTests.SecondaryExperience;

/// <summary>Verifies actual registered-worker networking and protected-navigation bypass using only the parent-managed CI browser fixture.</summary>
/// <param name="fixture">CI-only Chromium with context-level off-origin routing and explicit worker opt-in.</param>
public sealed class WorkerNetworkBrowserTests(FoundationBrowserFixture fixture) : IClassFixture<FoundationBrowserFixture>
{
    /// <summary>A fetch executed inside the registered production worker reaches the context route and is aborted by the fixture before contacting a live alternate loopback origin.</summary>
    /// <returns>Completion after a live-host control, worker ownership, delegated route abort and zero worker contacts are proved.</returns>
    [Fact]
    public async Task RegisteredWorkerOffOriginFetchIsAbortedByFixtureBeforeLoopbackContact()
    {
        await using var context = await fixture.CreateContextAsync(allowServiceWorkers: true);
        var page = await context.NewPageAsync();
        await RegisterWorkerAsync(page);
        await using var probe = new LoopbackWorkerProbe();
        using var control = new HttpClient(new HttpClientHandler { UseProxy = false });
        Assert.False(fixture.Settings.IsSameOrigin(probe.Url.AbsoluteUri));
        Assert.Equal(probe.Body, await control.GetStringAsync(probe.Url));
        Assert.Equal(1, probe.Requests);

        var routed = new TaskCompletionSource<IRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<IRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await context.RouteAsync(probe.Url.AbsoluteUri, async route =>
        {
            routed.TrySetResult(route.Request);
            // Observe the real worker request, then delegate unchanged to the fixture's existing origin guard.
            await route.FallbackAsync();
        });
        context.RequestFailed += OnFailed;
        try
        {
            var outcome = await FetchFromRegisteredWorkerAsync(context, page, probe.Url);
            var observed = await routed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var rejection = await failed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(outcome.GetProperty("workerGlobal").GetBoolean());
            Assert.Equal(new Uri(fixture.Settings.BaseUri, "/service-worker.js").AbsoluteUri,
                outcome.GetProperty("scriptUrl").GetString());
            Assert.Equal(probe.Url.AbsoluteUri, observed.Url);
            Assert.Equal(observed.Url, rejection.Url);
            Assert.Equal("TypeError", outcome.GetProperty("outcome").GetString());
            Assert.Equal("net::ERR_FAILED", rejection.Failure);
            Assert.Equal(probe.Body, await control.GetStringAsync(probe.Url));
            Assert.Equal(2, probe.Requests);
        }
        finally { context.RequestFailed -= OnFailed; }

        void OnFailed(object? sender, IRequest request)
        {
            if (request.Url == probe.Url.AbsoluteUri) failed.TrySetResult(request);
        }
    }

    /// <summary>Even direct protected-download navigation fails as a network request while ordinary navigation still receives the installed offline page.</summary>
    /// <param name="path">A media/calendar route, including server case-insensitive and percent-encoded forms.</param>
    /// <returns>Completion after a working offline fallback control and a protected navigation that never returns that HTML.</returns>
    [Theory]
    [InlineData("/media/covers/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData("/MeDiA/covers/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa?moderation=true")]
    [InlineData("/%6dedia/covers/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData("/media%2fcovers%2faaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData("/notifications/calendar/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData("/NoTiFiCaTiOnS/CaLeNdAr/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData("/notifications/%63alendar/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData("/notifications%2fcalendar%2faaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")]
    public async Task ProtectedDownloadNavigationsNeverReceiveOfflineHtml(string path)
    {
        await using var context = await fixture.CreateContextAsync(allowServiceWorkers: true);
        var page = await context.NewPageAsync();
        await RegisterWorkerAsync(page);
        await context.SetOfflineAsync(true);
        var ordinary = await context.NewPageAsync();
        await ordinary.GotoAsync("/");
        await Expect(ordinary.GetByRole(AriaRole.Heading, new() { Name = "Offline joined Quests", Exact = true })).ToBeVisibleAsync();
        await Assert.ThrowsAsync<PlaywrightException>(() => page.GotoAsync(path));
        Assert.DoesNotContain("Offline joined Quests", await page.ContentAsync());
    }

    private static async Task RegisterWorkerAsync(IPage page)
    {
        await page.GotoAsync("/install");
        await page.EvaluateAsync("""
            async () => {
                await navigator.serviceWorker.register('/service-worker.js', { scope: '/' });
                await navigator.serviceWorker.ready;
            }
            """);
        await page.WaitForFunctionAsync("() => navigator.serviceWorker.controller !== null");
    }

    private static async Task<JsonElement> FetchFromRegisteredWorkerAsync(IBrowserContext context, IPage page, Uri probe)
    {
        // The pinned .NET binding has no service-worker handle API. CDP attaches only to this CI context's registered worker.
        var cdp = await context.NewCDPSessionAsync(page);
        string? childSession = null;
        var received = cdp.Event("Target.receivedMessageFromTarget");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        received.OnEvent += OnMessage;
        try
        {
            var pageInfo = (await cdp.SendAsync("Target.getTargetInfo"))!.Value.GetProperty("targetInfo");
            var contextId = pageInfo.GetProperty("browserContextId").GetString();
            var script = new Uri(new Uri(page.Url), "/service-worker.js").AbsoluteUri;
            var targets = (await cdp.SendAsync("Target.getTargets"))!.Value.GetProperty("targetInfos");
            var worker = Assert.Single(targets.EnumerateArray(), target =>
                target.GetProperty("type").GetString() == "service_worker" &&
                target.GetProperty("url").GetString() == script &&
                target.TryGetProperty("browserContextId", out var workerContext) &&
                workerContext.GetString() == contextId);
            childSession = (await cdp.SendAsync("Target.attachToTarget", new()
            {
                ["targetId"] = worker.GetProperty("targetId").GetString()!,
                ["flatten"] = false
            }))!.Value.GetProperty("sessionId").GetString()!;
            var expression = """
                (async () => {
                    const result = {
                        workerGlobal: typeof ServiceWorkerGlobalScope !== 'undefined' && self instanceof ServiceWorkerGlobalScope,
                        scriptUrl: self.location.href
                    };
                    try {
                        const response = await fetch(__PROBE_URL__, { credentials: 'omit', cache: 'no-store' });
                        result.outcome = await response.text();
                    } catch (error) {
                        result.outcome = error.name;
                    }
                    return result;
                })()
                """.Replace("__PROBE_URL__", JsonSerializer.Serialize(probe.AbsoluteUri), StringComparison.Ordinal);
            await cdp.SendAsync("Target.sendMessageToTarget", new()
            {
                ["sessionId"] = childSession,
                ["message"] = JsonSerializer.Serialize(new
                {
                    id = 1,
                    method = "Runtime.evaluate",
                    @params = new { expression, awaitPromise = true, returnByValue = true }
                })
            });
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(response.TryGetProperty("error", out _));
            var evaluation = response.GetProperty("result");
            Assert.False(evaluation.TryGetProperty("exceptionDetails", out _));
            return evaluation.GetProperty("result").GetProperty("value").Clone();
        }
        finally
        {
            received.OnEvent -= OnMessage;
            try
            {
                if (childSession is not null)
                    await cdp.SendAsync("Target.detachFromTarget", new() { ["sessionId"] = childSession });
            }
            finally { await cdp.DetachAsync(); }
        }

        void OnMessage(object? sender, JsonElement? payload)
        {
            if (payload is not { } value || value.GetProperty("sessionId").GetString() != childSession) return;
            using var message = JsonDocument.Parse(value.GetProperty("message").GetString()!);
            if (message.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 1)
                completion.TrySetResult(message.RootElement.Clone());
        }
    }
}
