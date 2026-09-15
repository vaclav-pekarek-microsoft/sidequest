using System.Text.Json;
using Microsoft.Playwright;
using Sidequest.BrowserTests.FoundationBrowser;
using static Microsoft.Playwright.Assertions;

namespace Sidequest.BrowserTests.SecondaryExperience;

/// <summary>Runs actual IndexedDB, HTTP refresh and cross-tab race checks in the existing CI-only browser fixture.</summary>
/// <param name="fixture">Parent-managed Chromium; these tests never launch a local alternative browser.</param>
public sealed class OfflineStorageBrowserTests(FoundationBrowserFixture fixture) : IClassFixture<FoundationBrowserFixture>
{
    /// <summary>A delayed session denial cannot clear a newer account; a denial for the current epoch still purges and blocks the snapshot.</summary>
    /// <returns>Completion after real IndexedDB transactions and exact old/new-generation state assertions.</returns>
    [Fact]
    public async Task SessionDenialClearsOnlyItsOwnDeviceGeneration()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/experience/offline.html");
        var result = await page.EvaluateAsync<JsonElement>("""
            async snapshot => {
                const store = await import('/experience/snapshot-store.js?v=1');
                const oldEpoch = await store.currentEpoch();
                const attempt = await store.clearAccount();
                await store.completeAuthentication(attempt);
                const current = await store.currentEpoch();
                await store.replaceSnapshot(await store.beginRefresh(current), snapshot);
                const oldCleared = await store.clearAccountForEpoch(oldEpoch);
                const retained = await store.readSnapshot();
                const currentCleared = await store.clearAccountForEpoch(current);
                return {oldCleared, retained, currentCleared, after: await store.readSnapshot(), epoch: await store.currentEpoch()};
            }
            """, ExperienceBrowserSupport.Snapshot("CURRENT GENERATION"));
        Assert.False(result.GetProperty("oldCleared").GetBoolean());
        Assert.Equal("CURRENT GENERATION", result.GetProperty("retained").GetProperty("quests")[0].GetProperty("title").GetString());
        Assert.True(result.GetProperty("currentCleared").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("after").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("epoch").ValueKind);
    }

    /// <summary>Exact 24-hour expiry hides and purges the snapshot; the immediately preceding millisecond still renders.</summary>
    /// <returns>Completion after boundary reads and persisted purge assertions.</returns>
    [Fact]
    public async Task ExactTwentyFourHourBoundaryPurgesRatherThanRendering()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/experience/offline.html");
        await ExperienceBrowserSupport.SaveAsync(page, ExperienceBrowserSupport.Snapshot());
        var result = await page.EvaluateAsync<JsonElement>("""
            async () => {
                const s = await import('/experience/snapshot-store.js?v=1');
                const saved = await s.readSnapshot();
                const boundary = Date.parse(saved.refreshedUtc) + s.maximumAge;
                const before = await s.readSnapshot(boundary - 1);
                let expired;
                try { await s.readSnapshot(boundary); } catch (e) { expired = e.message; }
                return { before: before.quests.length, expired, after: await s.readSnapshot(boundary + 1) };
            }
            """);
        Assert.Equal(1, result.GetProperty("before").GetInt32());
        Assert.Contains("expired", result.GetProperty("expired").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("after").ValueKind);
    }

    /// <summary>Unexpected or oversized local fields are rejected and purged, and text is never interpreted as HTML.</summary>
    /// <returns>Completion after malformed storage purge and safe text rendering assertions.</returns>
    [Fact]
    public async Task UntrustedLocalFieldsAreRejectedAndTitlesUseTextContent()
    {
        await using var context = await fixture.CreateContextAsync(width: 360);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/experience/offline.html");
        await ExperienceBrowserSupport.SaveAsync(page, ExperienceBrowserSupport.Snapshot("<img src=x onerror=alert(1)>"));
        await page.GotoAsync("/experience/offline.html");
        await Expect(page.Locator("#quests h2")).ToHaveTextAsync("<img src=x onerror=alert(1)>");
        await Expect(page.Locator("#quests img")).ToHaveCountAsync(0);
        await page.EvaluateAsync("""
            async () => {
                await new Promise((resolve,reject) => {
                    const open = indexedDB.open('sidequest-experience-v1',1);
                    open.onsuccess = () => {
                        const db=open.result, tx=db.transaction('state','readwrite'), store=tx.objectStore('state');
                        const read=store.get('last-account');
                        read.onsuccess=()=>{const value=read.result; value.snapshot.quests[0].description='FORBIDDEN';store.put(value,'last-account');};
                        tx.oncomplete=()=>{db.close();resolve();}; tx.onerror=reject;
                    }; open.onerror=reject;
                });
            }
            """);
        await page.ReloadAsync();
        await Expect(page.Locator("#failure")).ToContainTextAsync("invalid");
        await Expect(page.Locator("#quests article")).ToHaveCountAsync(0);
        Assert.True(await page.EvaluateAsync<bool>("async()=> (await (await import('/experience/snapshot-store.js?v=1')).readSnapshot()) === null"));
    }

    /// <summary>Failed refreshes do not advance timestamps; HTTP authorization denial clears the old protected snapshot.</summary>
    /// <param name="status">A failure response from the explicit snapshot endpoint.</param>
    /// <returns>Completion after unchanged freshness or cleared-snapshot assertions.</returns>
    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    [InlineData(403)]
    public async Task RefreshFailurePreservesFreshnessUnlessAuthorizationIsDenied(int status)
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/experience/offline.html");
        await ExperienceBrowserSupport.SaveAsync(page, ExperienceBrowserSupport.Snapshot());
        var before = await page.EvaluateAsync<string>("async()=> (await (await import('/experience/snapshot-store.js?v=1')).readSnapshot()).refreshedUtc");
        await context.RouteAsync("**/experience/joined-snapshot", route =>
            route.FulfillAsync(new() { Status = status, ContentType = "application/problem+json", Body = "{}" }));
        var message = await page.EvaluateAsync<string>("""
            async () => {
                const s=await import('/experience/snapshot-store.js?v=1'), r=await import('/experience/refresh.js?v=1');
                try { await r.refreshJoined(await s.currentEpoch()); return 'unexpected success'; } catch(e) { return e.message; }
            }
            """);
        Assert.NotEqual("unexpected success", message);
        var after = await page.EvaluateAsync<string?>("async()=> (await (await import('/experience/snapshot-store.js?v=1')).readSnapshot())?.refreshedUtc ?? null");
        if (status == 500) Assert.Equal(before, after); else Assert.Null(after);
    }

    /// <summary>Denied browser storage is a visible failure rather than a success-shaped empty or saved snapshot.</summary>
    /// <returns>Completion after actual browser API denial and accessible error/empty-display assertions.</returns>
    [Fact]
    public async Task StorageDenialIsVisibleAndNeverReportsSuccessfulSaving()
    {
        await using var context = await fixture.CreateContextAsync();
        await context.AddInitScriptAsync("""
            Object.defineProperty(window, 'indexedDB', {
                get() { throw new DOMException('Synthetic device policy denial', 'SecurityError'); }
            });
            """);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/experience/offline.html");
        await Expect(page.Locator("#failure")).ToContainTextAsync("Device storage is unavailable");
        await Expect(page.Locator("#quests article")).ToHaveCountAsync(0);
        await Expect(page.Locator("#refresh")).ToHaveTextAsync("Offline — saved basics unavailable.");
    }

    /// <summary>An old-account response already in flight cannot resurrect data after logout or replace the next account's snapshot across tabs.</summary>
    /// <returns>Completion after actual HTTP delay, atomic epoch fence and last-account-only assertions.</returns>
    [Fact]
    public async Task DelayedOldResponseCannotResurrectLogoutOrOverwriteNextAccount()
    {
        await using var context = await fixture.CreateContextAsync();
        var oldTab = await context.NewPageAsync();
        var newTab = await context.NewPageAsync();
        await oldTab.GotoAsync("/experience/offline.html");
        await newTab.GotoAsync("/experience/offline.html");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await context.RouteAsync("**/experience/joined-snapshot", async route =>
        {
            entered.SetResult();
            await release.Task;
            await route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(ExperienceBrowserSupport.Snapshot("OLD RESPONSE"))
            });
        });
        var pending = oldTab.EvaluateAsync<string>("""
            async()=> {
                const s=await import('/experience/snapshot-store.js?v=1'),r=await import('/experience/refresh.js?v=1');
                try { await r.refreshJoined(await s.currentEpoch()); return 'unexpected success'; } catch(e) { return e.message; }
            }
            """);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var epoch = await newTab.EvaluateAsync<string>("async()=> await (await import('/Components/Experience/ConnectionStatus.razor.js')).beforeAuthenticationChange()");
            Assert.True(await newTab.EvaluateAsync<bool>("async()=> (await (await import('/experience/snapshot-store.js?v=1')).readSnapshot())===null"));
            await newTab.EvaluateAsync("async epoch=> await (await import('/Components/Experience/ConnectionStatus.razor.js')).afterAuthenticationSuccess(epoch)", epoch);
            var replacement = JsonSerializer.SerializeToElement(ExperienceBrowserSupport.Snapshot("NEW ACCOUNT"));
            await newTab.EvaluateAsync("""
                async value => {
                    value.accountId='dddddddd-dddd-4ddd-8ddd-dddddddddddd';
                    const s=await import('/experience/snapshot-store.js?v=1');
                    await s.replaceSnapshot(await s.beginRefresh(await s.currentEpoch()), value);
                }
                """, replacement);
        }
        finally { release.TrySetResult(); }
        Assert.Contains("outdated", await pending);
        var saved = await newTab.EvaluateAsync<JsonElement>("async()=> await (await import('/experience/snapshot-store.js?v=1')).readSnapshot()");
        Assert.Equal("dddddddd-dddd-4ddd-8ddd-dddddddddddd", saved.GetProperty("accountId").GetString());
        Assert.Equal("NEW ACCOUNT", saved.GetProperty("quests")[0].GetProperty("title").GetString());
        Assert.DoesNotContain("OLD RESPONSE", saved.ToString());
    }
}
