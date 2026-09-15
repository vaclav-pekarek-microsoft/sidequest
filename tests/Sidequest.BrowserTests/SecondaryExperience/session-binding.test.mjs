import { readFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import assert from "node:assert/strict";
import test from "node:test";

const source = await readFile(new URL("../../../src/Sidequest.Web/Components/Experience/ConnectionStatus.razor.js", import.meta.url), "utf8");

async function setup(status, storageFailure = false) {
    const calls = [];
    const storage = [];
    const requests = [];
    const elements = new Map();
    const main = { hidden: false, inert: false };
    const signOut = { disabled: false, hidden: false };
    const doc = new EventTarget();
    doc.body = {};
    doc.querySelector = () => main;
    doc.querySelectorAll = () => [main];
    globalThis.document = doc;
    globalThis.window = new EventTarget();
    globalThis.MutationObserver = class { observe() {} disconnect() {} };
    Object.defineProperty(globalThis, "navigator", { configurable: true, value: { onLine: true } });
    globalThis.localStorage = { setItem(key, value) { storage.push([key, value]); } };
    globalThis.BroadcastChannel = class extends EventTarget { postMessage(value) { storage.push(["broadcast", value]); } };
    globalThis.fetch = async (url, options) => {
        requests.push({ url, options });
        assert.equal(main.inert, true);
        assert.deepEqual(calls, []);
        return { status };
    };
    const root = {
        isConnected: true,
        querySelector(name) {
            if (!elements.has(name)) {
                const element = new EventTarget();
                element.textContent = "";
                elements.set(name, element);
            }
            return elements.get(name);
        }
    };
    globalThis.__sessionBindingStore = {
        async currentEpoch() { storage.push(["currentEpoch"]); return "captured-epoch"; },
        async readSnapshot() { storage.push(["readSnapshot"]); return null; },
        async clearAccount() { storage.push(["clearAccount"]); return "new-epoch"; },
        async clearAccountForEpoch(epoch) {
            storage.push(["clearAccountForEpoch", epoch]);
            if (storageFailure) throw new Error("Device policy denied");
            return true;
        },
        async completeAuthentication() { throw new Error("Reconnect must never complete authentication"); },
        async refreshJoined(epoch) { storage.push(["refreshJoined", epoch]); return "2026-09-15T08:00:00Z"; }
    };
    const executable = source.replace(/^import [^\n]+\nimport [^\n]+\n/,
        "const { currentEpoch, readSnapshot, clearAccount, clearAccountForEpoch, completeAuthentication, refreshJoined } = globalThis.__sessionBindingStore;\n");
    const module = await import(`data:text/javascript;base64,${Buffer.from(executable + `\n// ${randomUUID()}`).toString("base64")}`);
    const bridge = await module.initialize(root, { async invokeMethodAsync(...args) { calls.push(args); } }, "circuit-memory-proof");
    return { module, bridge, main, signOut, elements, calls, storage, requests };
}

test("Matching current-cookie proof is sent only in the no-store HTTP header before enabling actions", async () => {
    const state = await setup(204);
    try {
        assert.equal(state.requests.length, 1);
        assert.equal(state.requests[0].url, "/experience/session");
        assert.equal(state.requests[0].options.credentials, "same-origin");
        assert.equal(state.requests[0].options.cache, "no-store");
        assert.deepEqual(state.requests[0].options.headers, { "X-Sidequest-Circuit-Binding": "circuit-memory-proof" });
        assert.equal(state.main.inert, false);
        assert.equal(state.main.hidden, false);
        assert.equal(state.calls[0][0], "ConnectionChangedAsync");
        assert.equal(state.calls[0][1], true);
        assert.equal(JSON.stringify(state.storage).includes("circuit-memory-proof"), false);
    } finally { state.bridge.dispose(); }
});

for (const storageFailure of [false, true]) {
    test(`Authenticated session mismatch permanently hides old content without depending on storage clearing (${storageFailure})`, async () => {
        const state = await setup(409, storageFailure);
        try {
            assert.equal(state.main.inert, true);
            assert.equal(state.main.hidden, true);
            assert.deepEqual(state.calls, []);
            assert.match(state.elements.get("[data-connection]").textContent, /full online reload/);
            assert.equal(state.elements.get("[data-refresh]").disabled, true);
            assert.deepEqual(state.signOut, { disabled: false, hidden: false });
            assert.deepEqual(state.storage.filter(call => call[0] === "clearAccountForEpoch"), [["clearAccountForEpoch", "captured-epoch"]]);
            assert.equal(state.storage.some(call => call[0] === "refreshJoined"), false);
            if (storageFailure) assert.match(state.elements.get("[data-snapshot]").textContent, /clearing could not be verified/);
            state.module.reportCircuitConnection(true);
            await state.bridge.connection();
            assert.equal(state.main.hidden, true);
            assert.equal(state.main.inert, true);
            assert.equal(state.calls.some(call => call[1] === true), false);
            assert.equal(JSON.stringify(state.storage).includes("circuit-memory-proof"), false);
        } finally { state.bridge.dispose(); }
    });
}
