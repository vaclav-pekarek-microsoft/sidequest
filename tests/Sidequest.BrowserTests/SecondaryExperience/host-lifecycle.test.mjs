import { readFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import assert from "node:assert/strict";
import test from "node:test";

const source = await readFile(new URL("../../../src/Sidequest.Web/Components/App.razor.js", import.meta.url), "utf8");

async function host({ clear = async () => "attempt-epoch", complete = async () => {}, marker } = {}) {
    const listeners = new Map();
    const reconnectListeners = new Map();
    const buttons = new Map();
    const reports = [];
    const navigation = [];
    const completions = [];
    const warning = { hidden: true, textContent: "" };
    const message = { textContent: "" };
    const result = { textContent: "" };
    const modal = {
        className: "components-reconnect-hide",
        addEventListener(name, callback) { reconnectListeners.set(name, callback); },
        querySelector(name) {
            if (name === "[data-reconnect-message]") return message;
            if (!buttons.has(name)) buttons.set(name, {
                disabled: false, addEventListener(name, callback) { this[name] = callback; }
            });
            return buttons.get(name);
        }
    };
    const completion = marker === undefined ? null : {
        dataset: { authenticationCompletion: marker },
        querySelector(name) { return name === "[data-authentication-result]" ? result : { href: "http://localhost/quests" }; }
    };
    class Form {
        dataset = {};
        inputs = new Map();
        submitted = 0;
        hasAttribute(name) { return name === "data-authentication-change"; }
        querySelector(selector) { return this.inputs.get(selector.match(/name="([^"]+)"/)[1]); }
        append(input) { this.inputs.set(input.name, input); }
        requestSubmit() {
            throw new Error("A reentrant requestSubmit can be ignored by the native submission algorithm.");
        }
        submit() {
            this.submitted++;
        }
    }
    globalThis.HTMLFormElement = Form;
    globalThis.document = {
        addEventListener(name, callback) { listeners.set(name, callback); },
        getElementById() { return modal; },
        createElement() { return {}; },
        querySelector(name) { return name === "[data-authentication-completion]" ? completion : warning; }
    };
    globalThis.location = {
        href: "http://localhost/signin",
        assign(url) { navigation.push(url); },
        replace(url) { navigation.push(url); },
        reload() { navigation.push("reload"); }
    };
    globalThis.__hostLifecycle = {
        beforeAuthenticationChange: clear,
        async afterAuthenticationSuccess(epoch) { completions.push(epoch); await complete(epoch); },
        reportCircuitConnection(connected) { reports.push(connected); }
    };
    const executable = source.replace(/^import [^\n]+\n/, "const { beforeAuthenticationChange, afterAuthenticationSuccess, reportCircuitConnection } = globalThis.__hostLifecycle;\n");
    const module = await import(`data:text/javascript;base64,${Buffer.from(executable + `\n// ${randomUUID()}`).toString("base64")}`);
    return { module, listeners, reconnectListeners, buttons, reports, navigation, completions, warning, message, result, Form, modal };
}

test("The native auth POST waits for clearing and preserves antiforgery form and submitter", async () => {
    let release;
    const state = await host({ clear: () => new Promise(resolve => { release = resolve; }) });
    await state.module.beforeWebStart();
    const form = new state.Form();
    const submitter = { name: "operation", value: "sign-out" };
    form.inputs.set("__RequestVerificationToken", { value: "synthetic-antiforgery" });
    form.submit = { name: "submit" };
    let prevented = false;
    const pending = state.listeners.get("submit")({ target: form, submitter, preventDefault() { prevented = true; } });
    assert.equal(prevented, true);
    assert.equal(form.submitted, 0);
    release("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    await pending;
    assert.equal(form.submitted, 1);
    assert.equal(form.inputs.get("operation").value, "sign-out");
    assert.equal(form.inputs.get("__RequestVerificationToken").value, "synthetic-antiforgery");
    assert.equal(form.inputs.get("experienceEpoch").value, "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    assert.equal(form.inputs.get("experienceClearFailed").value, "false");
    assert.equal(state.completions.length, 0);
    await state.listeners.get("submit")({ target: form, submitter, preventDefault() {} });
    assert.equal(form.submitted, 1);
});

test("Submission before initializer installation stays native; missing completion generation never auto-activates or navigates", async () => {
    let clears = 0;
    const state = await host({ clear: async () => { clears++; return "attempt-epoch"; } });
    const form = new state.Form();
    form.inputs.set("__RequestVerificationToken", { value: "synthetic-antiforgery" });
    form.inputs.set("returnUrl", { value: "/" });
    let prevented = false;
    assert.equal(state.listeners.has("submit"), false);
    const pending = state.listeners.get("submit")?.({
        target: form, preventDefault() { prevented = true; }
    });
    if (!prevented) state.Form.prototype.submit.call(form);
    await pending;
    assert.equal(prevented, false);
    assert.equal(form.submitted, 1);
    assert.equal(clears, 0);
    assert.equal(form.inputs.has("experienceEpoch"), false);
    assert.equal(form.inputs.get("__RequestVerificationToken").value, "synthetic-antiforgery");
    assert.equal(form.inputs.get("returnUrl").value, "/");

    // Installing a listener later cannot intercept an already-dispatched native submission.
    await state.module.beforeWebStart();
    assert.equal(state.listeners.has("submit"), true);
    assert.equal(clears, 0);
    assert.equal(form.submitted, 1);
    assert.equal(form.inputs.has("experienceEpoch"), false);

    const completion = await host({ marker: "" });
    await completion.module.beforeWebStart();
    assert.deepEqual(completion.completions, []);
    assert.deepEqual(completion.navigation, []);
    assert.match(completion.result.textContent, /Sign-in succeeded/);
    assert.match(completion.result.textContent, /Continue uses the current online account/);
    assert.match(completion.result.textContent, /Nothing was refreshed/);
});

test("A storage failure is visible and cannot prevent sign-out or pretend clearing succeeded", async () => {
    const state = await host({ clear: async () => { throw new Error("denied"); } });
    await state.module.beforeWebStart();
    const form = new state.Form();
    await state.listeners.get("submit")({ target: form, preventDefault() {} });
    assert.equal(form.submitted, 1);
    assert.equal(form.inputs.get("experienceClearFailed").value, "true");
    assert.equal(form.inputs.get("experienceEpoch").value, "");
    assert.equal(state.warning.hidden, false);
    assert.match(state.warning.textContent, /clear this site's data/);
});

test("Entra and account-switch navigation await clearing and carry only the non-secret attempt generation", async () => {
    const state = await host();
    await state.module.beforeWebStart();
    await state.listeners.get("click")({
        target: { closest() { return { href: "http://localhost/auth/login?returnUrl=%2Fquests" }; } },
        preventDefault() {}
    });
    assert.deepEqual(state.navigation, ["http://localhost/auth/login?returnUrl=%2Fquests&experienceEpoch=attempt-epoch"]);
    assert.deepEqual(state.completions, []);
});

test("Ordinary pages and missing completion markers never activate a new authentication epoch", async () => {
    const ordinary = await host();
    await ordinary.module.beforeWebStart();
    assert.deepEqual(ordinary.completions, []);
    const absent = await host({ marker: "" });
    await absent.module.beforeWebStart();
    assert.deepEqual(absent.completions, []);
    assert.deepEqual(absent.navigation, []);
    assert.match(absent.result.textContent, /could not be activated/);
});

test("Verified completion is awaited before navigation; superseded completion remains visibly blocked", async () => {
    let release;
    const state = await host({ marker: "verified-epoch", complete: () => new Promise(resolve => { release = resolve; }) });
    const pending = state.module.beforeWebStart();
    assert.deepEqual(state.completions, ["verified-epoch"]);
    assert.deepEqual(state.navigation, []);
    release();
    await pending;
    assert.deepEqual(state.navigation, ["http://localhost/quests"]);
    const stale = await host({ marker: "old-epoch", complete: async () => { throw new Error("superseded"); } });
    await stale.module.beforeWebStart();
    assert.deepEqual(stale.navigation, []);
    assert.match(stale.result.textContent, /superseded/);
    assert.match(stale.result.textContent, /Nothing was refreshed/);
});

test("Framework reconnect events gate immediately; failed/rejected connections never auto-reload or retry mutations", async () => {
    const state = await host();
    await state.module.beforeWebStart();
    let retries = 0;
    state.module.afterWebStarted({ addEventListener() {}, async reconnect() { retries++; return false; } });
    const change = state.reconnectListeners.get("components-reconnect-state-changed");
    for (const value of ["show", "retrying", "failed", "rejected", "paused", "hide"]) change({ detail: { state: value } });
    assert.deepEqual(state.reports, [false, false, false, false, false, true]);
    assert.equal(retries, 0);
    assert.deepEqual(state.navigation, []);
    const retry = state.buttons.get("[data-reconnect-retry]");
    await retry.click({ currentTarget: retry });
    assert.equal(retries, 1);
    assert.equal(state.reports.at(-1), false);
    assert.equal(retry.disabled, false);
    assert.match(state.message.textContent, /Copy unsaved input/);
    assert.deepEqual(state.navigation, []);
    state.buttons.get("[data-reconnect-reload]").click();
    assert.deepEqual(state.navigation, ["reload"]);
});
