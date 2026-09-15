import { readFile } from "node:fs/promises";
import vm from "node:vm";
import assert from "node:assert/strict";
import test from "node:test";

const source = await readFile(new URL("../../../src/Sidequest.Web/wwwroot/experience/service-worker.js", import.meta.url), "utf8");
const origin = "http://127.0.0.1:5017";

function harness() {
    const handlers = new Map();
    const calls = [];
    const fallback = { kind: "static-offline-page" };
    const sandbox = {
        URL,
        self: { location: { origin }, addEventListener: (name, callback) => handlers.set(name, callback) },
        fetch: async request => { calls.push(["fetch", request.url]); throw new TypeError("Synthetic network loss"); },
        caches: {
            open: async name => {
                calls.push(["open", name]);
                return { match: async path => { calls.push(["match", path]); return fallback; } };
            }
        }
    };
    vm.runInNewContext(source, sandbox, { filename: "service-worker.js" });
    return {
        calls, fallback,
        dispatch(path, mode = "navigate", method = "GET") {
            let response;
            handlers.get("fetch")({
                request: { url: new URL(`${origin}${path}`).href, mode, method },
                respondWith: promise => { response = promise; }
            });
            return response;
        }
    };
}

for (const path of [
    "/media", "/media/covers/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "/MeDiA/CoVeRs/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa?moderation=true",
    "//MEDIA//covers/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "/%6dedia/covers/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "/media%2fcovers%2faaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "/%256dedia/covers/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "/notifications/calendar", "/notifications/calendar/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "/NoTiFiCaTiOnS/CaLeNdAr/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "/notifications/%63alendar/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "/notifications%2fcalendar%2faaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "/notifications/calendar/%invalid",
    "/quests/%61"
]) {
    test(`Network-only navigation bypasses both respondWith and all cache operations: ${path}`, () => {
        const worker = harness();
        assert.equal(worker.dispatch(path), undefined);
        assert.deepEqual(worker.calls, []);
    });
}

test("Unencoded ordinary navigations still receive the dedicated fallback on network failure", async () => {
    const worker = harness();
    const response = worker.dispatch("/quests/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    assert.notEqual(response, undefined);
    assert.equal(await response, worker.fallback);
    assert.deepEqual(worker.calls, [
        ["fetch", `${origin}/quests/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa`],
        ["open", "sidequest-public-experience-v1"],
        ["match", "/experience/offline.html"]
    ]);
});

test("Public versioned assets still use only their explicit cache entries", async () => {
    const worker = harness();
    assert.equal(await worker.dispatch("/experience/offline.css?v=1", "cors"), worker.fallback);
    assert.equal(worker.calls[0][0], "open");
    assert.equal(worker.calls[1][0], "match");
    assert.equal(worker.calls[1][1].url, `${origin}/experience/offline.css?v=1`);
    assert.equal(worker.calls.length, 2);
});

test("A similarly named non-protected route is not confused with the protected download namespaces", async () => {
    const worker = harness();
    assert.equal(await worker.dispatch("/media-info"), worker.fallback);
    assert.equal(worker.calls[0][0], "fetch");
});
