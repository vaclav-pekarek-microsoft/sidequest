import { readFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import assert from "node:assert/strict";
import test from "node:test";

const source = await readFile(new URL("../../../src/Sidequest.Web/wwwroot/experience/offline.js", import.meta.url), "utf8");

function deferred() {
    let resolve;
    const promise = new Promise(complete => { resolve = complete; });
    return { promise, resolve };
}

function renderer(store) {
    const listeners = new Map();
    class Element {
        textContent = "";
        children = [];
        append(...children) { this.children.push(...children); }
        replaceChildren() { this.children = []; }
        addEventListener(name, callback) { this[name] = callback; }
    }
    const elements = new Map(["#quests", "#refresh", "#failure", "#clear"].map(id => [id, new Element()]));
    globalThis.document = {
        querySelector(selector) { return elements.get(selector); },
        createElement() { return new Element(); },
        addEventListener(name, callback) { listeners.set(name, callback); }
    };
    globalThis.window = { addEventListener(name, callback) { listeners.set(name, callback); } };
    globalThis.BroadcastChannel = undefined;
    globalThis.localStorage = { setItem() {} };
    globalThis.__offlineRenderer = store;
    const executable = source
        .replace(/^import [^\n]+\n/gm, "")
        .replace("const list =", `
            const { currentEpoch, readSnapshot, clearAccount, refreshJoined } = globalThis.__offlineRenderer;
            const maximumAge = 24 * 60 * 60 * 1000;
            const statusLabel = () => 'Scheduled';
            const setTimeout = () => 1, clearTimeout = () => {};
            const list =`);
    const loaded = import(`data:text/javascript;base64,${Buffer.from(executable + `\n// ${randomUUID()}`).toString("base64")}`);
    return { loaded, elements, listeners };
}

for (const message of ["Saved basics are invalid and were removed.", "Saved basics expired and were removed."]) {
    test(`Overlapping initial/pageshow reads retain destructive purge feedback: ${message}`, async () => {
        const entered = deferred();
        const release = deferred();
        let reads = 0;
        let saved = { invalid: true };
        const state = renderer({
            async readSnapshot() {
                reads++;
                if (saved) {
                    entered.resolve();
                    await release.promise;
                    saved = null;
                    throw new Error(message);
                }
                return null;
            }
        });
        await entered.promise;
        const pageshow = state.listeners.get("pageshow")();
        assert.equal(reads, 1);
        release.resolve();
        await Promise.all([state.loaded, pageshow]);
        assert.equal(reads, 2);
        assert.equal(saved, null);
        assert.equal(state.elements.get("#failure").textContent, message);
        assert.match(state.elements.get("#refresh").textContent, /saved basics unavailable/);
        assert.equal(state.elements.get("#quests").children.length, 0);
        await state.listeners.get("visibilitychange")();
        assert.equal(state.elements.get("#failure").textContent, message);
    });
}

test("A successful authorized online refresh replaces purge feedback without restoring purged cards", async () => {
    let purged = false;
    let saved = null;
    let refreshes = 0;
    const state = renderer({
        async readSnapshot() {
            if (!purged) {
                purged = true;
                throw new Error("Saved basics are invalid and were removed.");
            }
            return saved;
        },
        async currentEpoch() { return "current-epoch"; },
        async refreshJoined(epoch) {
            assert.equal(epoch, "current-epoch");
            refreshes++;
            saved = { refreshedUtc: new Date().toISOString(), quests: [] };
        }
    });
    await state.loaded;
    assert.match(state.elements.get("#failure").textContent, /invalid/);
    await state.listeners.get("online")();
    assert.equal(refreshes, 1);
    assert.equal(state.elements.get("#failure").textContent, "");
    assert.match(state.elements.get("#refresh").textContent, /last refreshed/);
    assert.deepEqual(state.elements.get("#quests").children.map(child => child.textContent),
        ["No joined Quests in the last authorized refresh."]);
});

test("Explicit clear removes purge feedback and fences an already pending old snapshot read", async () => {
    const entered = deferred();
    const release = deferred();
    let reads = 0;
    let cleared = false;
    const state = renderer({
        async readSnapshot() {
            if (++reads === 1) throw new Error("Saved basics are invalid and were removed.");
            entered.resolve();
            await release.promise;
            return { refreshedUtc: new Date().toISOString(), quests: [] };
        },
        async clearAccount() { cleared = true; }
    });
    await state.loaded;
    assert.match(state.elements.get("#failure").textContent, /invalid/);
    const pending = state.listeners.get("pageshow")();
    await entered.promise;
    await state.elements.get("#clear").click();
    assert.equal(cleared, true);
    release.resolve();
    await pending;
    assert.equal(state.elements.get("#failure").textContent, "");
    assert.match(state.elements.get("#refresh").textContent, /Saved basics cleared/);
    assert.equal(state.elements.get("#quests").children.length, 0);
});
