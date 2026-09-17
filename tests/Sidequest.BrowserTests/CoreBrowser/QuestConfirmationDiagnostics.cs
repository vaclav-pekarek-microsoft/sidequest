using Microsoft.Playwright;

namespace Sidequest.BrowserTests.CoreBrowser;

internal static class QuestConfirmationDiagnostics
{
    private const string StartScript = """
        element => {
            const host = element.getRootNode().host ?? element;
            const started = performance.now();
            const events = [];
            const length = value => typeof value === 'string' ? value.length : null;
            const snapshot = category => ({
                category,
                milliseconds: Math.round(performance.now() - started),
                connected: host.isConnected && element.isConnected,
                controlDisabled: element.disabled === true,
                hostDisabled: host.disabled === true,
                controlLength: length(element.value),
                hostLength: length(host.value),
                currentLength: length(host.currentValue),
                valueAttributeLength: length(host.getAttribute('value')),
                currentAttributeLength: length(host.getAttribute('current-value'))
            });
            const record = category => {
                if (events.length < 32) events.push(snapshot(category));
            };
            const capture = event => {
                if (event.composedPath().includes(host)) record(`${event.type}:capture`);
            };
            const bubble = event => {
                if (event.composedPath().includes(host)) record(`${event.type}:bubble`);
            };
            for (const type of ['input', 'change']) {
                document.addEventListener(type, capture, true);
                document.addEventListener(type, bubble);
            }
            const observer = new MutationObserver(records => {
                for (const change of records) record(`attribute:${change.attributeName}`);
            });
            observer.observe(host, {
                attributes: true,
                attributeFilter: ['value', 'current-value', 'disabled']
            });
            record('attached');
            window.sidequestReasonDiagnostics = {
                read: () => JSON.stringify({ events, final: snapshot('failure') }),
                dispose: () => {
                    observer.disconnect();
                    for (const type of ['input', 'change']) {
                        document.removeEventListener(type, capture, true);
                        document.removeEventListener(type, bubble);
                    }
                    delete window.sidequestReasonDiagnostics;
                }
            };
        }
        """;

    internal static async Task ObserveAsync(IPage page, ILocator reason, string action, Func<Task> operation)
    {
        var attached = false;
        try
        {
            await reason.EvaluateAsync(StartScript);
            attached = true;
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            await Console.Out.WriteLineAsync($"Quest confirmation diagnostics unavailable ({error.GetType().Name}); action still runs.");
        }
        try
        {
            await operation();
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            if (attached)
            {
                try
                {
                    var state = await page.EvaluateAsync<string>("() => window.sidequestReasonDiagnostics?.read() ?? 'unavailable'");
                    await Console.Out.WriteLineAsync($"Quest confirmation '{action}' failed; reason lengths/events only: {state}");
                }
                catch (Exception diagnosticError) when (diagnosticError is PlaywrightException or TimeoutException)
                {
                    await Console.Out.WriteLineAsync($"Quest confirmation diagnostics lost ({diagnosticError.GetType().Name}); original failure retained.");
                }
            }
            throw;
        }
        finally
        {
            if (attached)
            {
                try
                {
                    await page.EvaluateAsync("() => window.sidequestReasonDiagnostics?.dispose()");
                }
                catch (Exception error) when (error is PlaywrightException or TimeoutException)
                {
                    await Console.Out.WriteLineAsync($"Quest confirmation diagnostics cleanup unavailable ({error.GetType().Name}).");
                }
            }
        }
    }
}
