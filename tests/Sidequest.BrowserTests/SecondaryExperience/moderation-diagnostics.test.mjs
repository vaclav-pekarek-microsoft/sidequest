import { readFile } from "node:fs/promises";
import vm from "node:vm";
import assert from "node:assert/strict";
import test from "node:test";

const source = await readFile(new URL("../CoreBrowser/CoreWorkflowBrowserTests.cs", import.meta.url), "utf8");
const match = source.match(/\bconst string ModerationDiagnosticsScript = """\r?\n([\s\S]*?)\r?\n\s*""";/);
assert.ok(match, "Exercise the actual browser failure diagnostic.");

function diagnose({ present = true, view = "Moderation", eventId = "private-event", alerts = [],
    loading = false, empty = false, visible = true, hidden = false } = {}) {
    const sentinel = "private-title-token-address";
    const link = { textContent: sentinel, getClientRects: () => visible ? [{}] : [] };
    const main = {
        textContent: `${loading ? "Loading authorized Quests" : ""}${empty ? "No Quests in this view." : ""}${sentinel}`,
        hidden, inert: false,
        querySelectorAll: selector => {
            switch (selector) {
                case "select": return [{ value: view }, { value: eventId }];
                case "a": return [link];
                case '[role="alert"]': return alerts.map(textContent => ({ textContent }));
                default: throw new Error("Unexpected selector");
            }
        }
    };
    const result = vm.runInNewContext(`(${match[1]})(expected)`, {
        expected: { title: sentinel, eventId: "private-event" },
        document: { querySelector: selector => {
            switch (selector) {
                case "main": return present ? main : null;
                case "[data-connection]": return { textContent: `Connected ${sentinel}` };
                case "#components-reconnect-modal": return { classList: { contains: () => false } };
                default: throw new Error("Unexpected selector");
            }
        } },
        getComputedStyle: () => ({ visibility: "visible", display: "block" })
    });
    assert.doesNotMatch(result, /private-title|private-event|private-other|token-address/);
    return JSON.parse(result);
}

test("Moderation diagnostic records bounded state without titles, identities, arguments or proof content", () => {
    assert.deepEqual(diagnose(), {
        main: true, view: "Moderation", eventMatches: true, loading: false, empty: false,
        alert: "none", matchingLinks: 1, visibleMatches: 1, mainHidden: false, connected: true, reconnectVisible: false
    });
    assert.deepEqual(diagnose({ present: false }), { main: false });
});

test("Moderation diagnostic distinguishes query mismatch, loading, empty data and hidden results", () => {
    const result = diagnose({ view: "Joined", eventId: "private-other", loading: true, empty: true, visible: false, hidden: true });
    assert.equal(result.view, "Joined");
    assert.equal(result.eventMatches, false);
    assert.equal(result.loading, true);
    assert.equal(result.empty, true);
    assert.equal(result.matchingLinks, 1);
    assert.equal(result.visibleMatches, 0);
    assert.equal(result.mainHidden, true);
    assert.equal(diagnose({ view: "private-title-token-address" }).view, "other");
});

test("Moderation diagnostic classifies failures without exporting alert details", () => {
    for (const [message, expected] of [
        ["This operation conflicted. private-title-token-address", "conflict"],
        ["This resource is unavailable. private-title-token-address", "unavailable"],
        ["Sign in to continue. private-title-token-address", "identity"],
        ["Quests could not be loaded. private-title-token-address", "unexpected"],
        ["private-title-token-address", "other"]
    ]) {
        assert.equal(diagnose({ alerts: [message] }).alert, expected);
    }
});
