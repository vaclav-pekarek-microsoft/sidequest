using System.Collections.Concurrent;
using Microsoft.Playwright;
using Sidequest.BrowserTests.FoundationBrowser;

namespace Sidequest.BrowserTests.SecondaryExperience;

/// <summary>Collects bounded, failure-only authentication observations without recording credentials, form content or arbitrary URLs.</summary>
internal static class SyntheticLoginDiagnostics
{
    private const string LoginDiagnosticsScript = """
        () => {
            const completion = document.querySelector('[data-authentication-completion]');
            const form = document.querySelector('form[data-authentication-change]');
            const result = completion?.querySelector('[data-authentication-result]')?.textContent ?? '';
            return JSON.stringify({
                readyState: document.readyState,
                formPresent: form !== null,
                formGenerationPresent: Boolean(form?.querySelector('input[name="experienceEpoch"]')?.value),
                completionPresent: completion !== null,
                completionGenerationPresent: Boolean(completion?.dataset.authenticationCompletion),
                completionState: !completion ? 'absent' :
                    result === 'Checking the device-clearing boundary before opening Sidequest.' ? 'checking' :
                    result.startsWith('Sign-in succeeded, but device saving could not be activated') ? 'blocked' : 'other',
                clearWarningVisible: document.querySelector('[data-authentication-warning]')?.hidden === false
            });
        }
        """;

    /// <summary>Observes one existing sign-in attempt and rethrows its original automation failure after safe diagnostics; never retries or continues an authentication failure.</summary>
    /// <param name="page">The page whose requests are observed until the supplied attempt ends.</param>
    /// <param name="settings">Validated synthetic origin used to exclude unrelated URLs.</param>
    /// <param name="category">A fixed test-suite label, never a persona or user-controlled value.</param>
    /// <param name="attempt">The original UI operations and assertions, invoked exactly once.</param>
    /// <returns>Completion after the attempt succeeds or its failure is reported and rethrown; all listeners are removed.</returns>
    internal static async Task ObserveAsync(IPage page, SyntheticAppSettings settings, string category, Func<Task> attempt)
    {
        var transport = new ConcurrentQueue<string>();
        var observations = 0;
        void Record(IRequest request, string state)
        {
            var path = DiagnosticPath(request.Url, settings);
            if (path is "<other>" or "<off-origin>") return;
            if (Interlocked.Increment(ref observations) <= 48)
                transport.Enqueue($"{(request.Method is "GET" or "POST" ? request.Method : "<other>")} {path} {state}");
        }
        void Requested(object? sender, IRequest request) => Record(request, "requested");
        void Responded(object? sender, IResponse response) => Record(response.Request, $"HTTP {response.Status}");
        void Finished(object? sender, IRequest request) => Record(request, "finished");
        void Failed(object? sender, IRequest request) => Record(request, "failed");
        page.Request += Requested;
        page.Response += Responded;
        page.RequestFinished += Finished;
        page.RequestFailed += Failed;
        try
        {
            await attempt();
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            await Console.Out.WriteLineAsync(
                $"{category} login failed: path={DiagnosticPath(page.Url, settings)}; HTTP=[{string.Join("; ", transport)}]; truncated={observations > 48}");
            try
            {
                var state = await page.EvaluateAsync<string>(LoginDiagnosticsScript).WaitAsync(TimeSpan.FromSeconds(2));
                await Console.Out.WriteLineAsync($"{category} login completion state: {state}");
            }
            catch (Exception diagnosticError) when (diagnosticError is PlaywrightException or TimeoutException)
            {
                await Console.Out.WriteLineAsync(
                    $"{category} login completion state unavailable ({diagnosticError.GetType().Name}); original failure retained.");
            }
            throw;
        }
        finally
        {
            page.Request -= Requested;
            page.Response -= Responded;
            page.RequestFinished -= Finished;
            page.RequestFailed -= Failed;
        }
    }

    private static string DiagnosticPath(string url, SyntheticAppSettings settings)
    {
        if (!settings.IsSameOrigin(url)) return "<off-origin>";
        var path = new Uri(url).AbsolutePath;
        return path switch
        {
            "/" or "/signin" or "/foundation" or "/auth/development" or "/auth/complete" or
                "/Sidequest.Web.lib.module.js" or "/Components/App.razor.js" or
                "/Components/Experience/ConnectionStatus.razor.js" or "/experience/snapshot-store.js" or
                "/experience/refresh.js" => path,
            _ when path.StartsWith("/_framework/blazor.", StringComparison.Ordinal) &&
                path.EndsWith(".js", StringComparison.Ordinal) => "<framework-script>",
            _ => "<other>"
        };
    }
}
