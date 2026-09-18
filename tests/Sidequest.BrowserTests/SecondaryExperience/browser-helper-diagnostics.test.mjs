import { readFile } from "node:fs/promises";
import vm from "node:vm";
import assert from "node:assert/strict";
import test from "node:test";

const source = await readFile(new URL("./ExperienceBrowserSupport.cs", import.meta.url), "utf8");
const match = source.match(/private const string ConfirmationDiagnosticsScript = """\r?\n([\s\S]*?)\r?\n\s*""";/);
assert.ok(match, "The helper's production diagnostic projection must be present.");

function capture({ main = null, management = null, alerts = [], files = [], connection = "", reconnect = "", path = "/" } = {}) {
    return vm.runInNewContext(`(${match[1]})()`, {
        document: {
            querySelector(selector) {
                return ({
                    main,
                    "#management-title": management,
                    "[data-connection]": { textContent: connection },
                    "#components-reconnect-modal": { className: reconnect }
                })[selector];
            },
            querySelectorAll(selector) { return selector === 'input[type="file"]' ? files : alerts; }
        },
        location: { pathname: path, search: "?token=NEVER-LOG-QUERY", hash: "#NEVER-LOG-FRAGMENT" },
        navigator: { onLine: false },
        getComputedStyle(element) { return element.style; }
    });
}

test("Confirmation diagnostics bound every content field and exclude form values, HTML and URL credentials", () => {
    const raw = capture({
        path: "/quests/" + "a".repeat(200),
        connection: "c".repeat(300),
        reconnect: "r".repeat(200),
        main: {
            hidden: false, inert: true, style: { display: "block", visibility: "visible" },
            value: "NEVER-LOG-FORM", innerHTML: "NEVER-LOG-HTML"
        },
        management: { hidden: true, inert: false, style: { display: "none", visibility: "hidden" } },
        alerts: Array.from({ length: 8 }, () => ({ textContent: "a".repeat(400) })),
        files: Array.from({ length: 8 }, (_, index) => ({
            isConnected: index !== 1, disabled: true,
            _blazorInputFileNextFileId: index === 0 ? 0 : undefined,
            value: "NEVER-LOG-FILENAME", files: ["NEVER-LOG-FILE"]
        }))
    });
    const state = JSON.parse(raw);
    assert.deepEqual(Object.keys(state).sort(),
        ["alerts", "connection", "fileInputs", "main", "management", "online", "path", "reconnect"]);
    assert.equal(state.path.length, 180);
    assert.equal(state.connection.length, 240);
    assert.equal(state.reconnect.length, 120);
    assert.equal(state.alerts.length, 4);
    assert.ok(state.alerts.every(alert => alert.length === 320));
    assert.equal(state.online, false);
    assert.deepEqual(state.main, { hidden: false, inert: true, display: "block", visibility: "visible" });
    assert.deepEqual(state.management, { hidden: true, inert: false, display: "none", visibility: "hidden" });
    assert.equal(state.fileInputs.length, 4);
    assert.deepEqual(state.fileInputs[0], { connected: true, disabled: true, initialized: true });
    assert.deepEqual(state.fileInputs[1], { connected: false, disabled: true, initialized: false });
    assert.doesNotMatch(raw, /NEVER-LOG/);
    assert.ok(raw.length < 2500);
});

test("Missing confirmation DOM remains explicitly absent rather than being reported as ready", () => {
    assert.deepEqual(JSON.parse(capture()), {
        path: "/", online: false, connection: "", reconnect: "",
        alerts: [], main: null, management: null, fileInputs: []
    });
});

test("Required reasons are determined by the action, not a transiently absent Fluent shadow textbox", async () => {
    const core = await readFile(new URL("../CoreBrowser/CoreWorkflowBrowserTests.cs", import.meta.url), "utf8");
    for (const helper of [source, core]) {
        assert.doesNotMatch(helper, /await reason\.CountAsync\(\)/);
        assert.match(helper, /action is "Cancel Quest" or "Revoke invitation" or "Remove attendee"/);
        assert.match(helper, /await reason\.FillWhenActionableAsync\(/);
    }
    assert.match(core, /var needsReason = moderation \|\|/);
});
