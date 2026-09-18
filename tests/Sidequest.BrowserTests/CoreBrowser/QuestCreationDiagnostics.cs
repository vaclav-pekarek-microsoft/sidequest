using Microsoft.Playwright;

namespace Sidequest.BrowserTests.CoreBrowser;

internal static class QuestCreationDiagnostics
{
    private const string StartScript = """
        element => {
            const initialHost = element.getRootNode().host ?? element;
            const initialForm = initialHost.closest('form');
            const initialMain = initialForm?.closest('main');
            if (!initialForm || !initialMain) throw new Error('Quest editor form unavailable.');
            const started = performance.now();
            const events = [];
            const selectors = [
                ['title', 'fluent-text-field', 0],
                ['description', 'fluent-text-area', 0],
                ['location', 'fluent-text-field', 1]
            ];
            const fields = form => selectors.map(([category, selector, index]) => {
                const host = form?.querySelectorAll(selector)[index];
                return { category, host, control: host?.shadowRoot?.querySelector('input,textarea') };
            });
            const original = fields(initialForm);
            const length = value => typeof value === 'string' ? value.length : null;
            const snapshot = category => {
                const main = document.querySelector('main');
                const form = main?.querySelector('form');
                return {
                    category,
                    milliseconds: Math.round(performance.now() - started),
                    originalFormConnected: initialForm.isConnected,
                    sameForm: form === initialForm,
                    mainBlocked: !main || main.hidden || main.inert,
                    bridgeConnected: (document.querySelector('[data-connection]')?.textContent ?? '').startsWith('Connected'),
                    controls: fields(form).map(({ category, host, control }, index) => ({
                        category,
                        originalHostConnected: original[index].host?.isConnected === true,
                        sameHost: host === original[index].host,
                        sameControl: control === original[index].control,
                        controlDisabled: control?.disabled === true,
                        hostDisabled: host?.disabled === true,
                        controlLength: length(control?.value),
                        hostLength: length(host?.value),
                        currentLength: length(host?.currentValue),
                        valueAttributeLength: length(host?.getAttribute('value')),
                        currentAttributeLength: length(host?.getAttribute('current-value'))
                    }))
                };
            };
            const record = category => {
                if (events.length < 32) events.push(snapshot(category));
            };
            const relevant = event => event.composedPath().some(node =>
                node === initialForm || node?.localName === 'fluent-text-field' || node?.localName === 'fluent-text-area');
            const capture = event => {
                if (relevant(event)) record(`${event.type}:capture`);
            };
            const bubble = event => {
                if (relevant(event)) record(`${event.type}:bubble`);
            };
            for (const type of ['input', 'change', 'reset']) {
                document.addEventListener(type, capture, true);
                document.addEventListener(type, bubble);
            }
            const observer = new MutationObserver(records => {
                for (const change of records) {
                    if (change.type === 'attributes' && selectors.some(([, selector]) => change.target.matches(selector)))
                        record(`attribute:${change.attributeName}`);
                    else if (change.type === 'childList' && !initialForm.isConnected)
                        record('original-form-removed');
                }
            });
            observer.observe(initialMain, {
                subtree: true, childList: true, attributes: true,
                attributeFilter: ['value', 'current-value', 'disabled']
            });
            record('attached');
            window.sidequestCreationDiagnostics = {
                read: () => JSON.stringify({ events, final: snapshot('failure') }),
                dispose: () => {
                    observer.disconnect();
                    for (const type of ['input', 'change', 'reset']) {
                        document.removeEventListener(type, capture, true);
                        document.removeEventListener(type, bubble);
                    }
                    delete window.sidequestCreationDiagnostics;
                }
            };
        }
        """;

    internal static async Task ObserveAsync(IPage page, ILocator title, Func<Task> operation)
    {
        var attached = false;
        try
        {
            await title.EvaluateAsync(StartScript);
            attached = true;
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            await Console.Out.WriteLineAsync($"Quest creation diagnostics unavailable ({error.GetType().Name}); creation still runs.");
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
                    var state = await page.EvaluateAsync<string>("() => window.sidequestCreationDiagnostics?.read() ?? 'unavailable'");
                    await Console.Out.WriteLineAsync($"Quest creation failed; input lengths and lifecycle only: {state}");
                }
                catch (Exception diagnosticError) when (diagnosticError is PlaywrightException or TimeoutException)
                {
                    await Console.Out.WriteLineAsync($"Quest creation diagnostics lost ({diagnosticError.GetType().Name}); original failure retained.");
                }
            }
            throw;
        }
        finally
        {
            if (attached)
            {
                try { await page.EvaluateAsync("() => window.sidequestCreationDiagnostics?.dispose()"); }
                catch (Exception error) when (error is PlaywrightException or TimeoutException)
                {
                    await Console.Out.WriteLineAsync($"Quest creation diagnostics cleanup unavailable ({error.GetType().Name}).");
                }
            }
        }
    }
}
