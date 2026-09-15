import { readFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import assert from "node:assert/strict";
import test from "node:test";

// Test the production ES module without adding a package manifest or browser substitute.
const source = await readFile(new URL("../../../src/Sidequest.Web/wwwroot/experience/snapshot-store.js", import.meta.url), "utf8");
const { validateSnapshot, maximumAge } = await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);
const now = Date.parse("2026-09-15T10:00:00Z");
const sample = () => ({
    accountId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    refreshedUtc: "2026-09-15T09:00:00Z",
    quests: [{
        id: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
        eventId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
        title: "Joined Quest",
        location: "Room",
        startUtc: "2026-10-01T10:00:00Z",
        endUtc: "2026-10-01T11:00:00Z",
        timeZoneId: "Europe/Prague",
        status: 1
    }]
});

test("Only the exact minimal allowlist is accepted, including an empty full replacement", () => {
    const value = sample();
    assert.equal(validateSnapshot(value, now), value);
    value.quests = [];
    assert.deepEqual(validateSnapshot(value, now).quests, []);
    for (const field of ["description", "eventName", "coverAssetId", "owners", "invitations", "token"]) {
        const extra = sample();
        extra.quests[0][field] = "PRIVATE";
        assert.throws(() => validateSnapshot(extra, now), /invalid/);
    }
    const extra = sample();
    extra.accountName = "PRIVATE";
    assert.throws(() => validateSnapshot(extra, now), /invalid/);
});

test("Reference metadata is rejected rather than stripped from snapshots", () => {
    const root = { ...sample(), $id: "1" };
    const quest = sample();
    quest.quests[0].$id = "2";
    const collection = sample();
    collection.quests = { $id: "3", $values: collection.quests };
    for (const value of [root, quest, collection]) {
        assert.throws(() => validateSnapshot(value, now), /invalid/);
    }
    assert.equal(root.$id, "1");
    assert.equal(quest.quests[0].$id, "2");
    assert.equal(collection.quests.$id, "3");
});

test("Expiry is exact at 24h, future freshness is rejected, and the preceding millisecond is valid", () => {
    const value = sample();
    const refreshed = Date.parse(value.refreshedUtc);
    assert.equal(validateSnapshot(value, refreshed + maximumAge - 1), value);
    assert.throws(() => validateSnapshot(value, refreshed + maximumAge), /expired/);
    assert.throws(() => validateSnapshot(value, refreshed + maximumAge + 1), /expired/);
    assert.throws(() => validateSnapshot(value, refreshed - 1), /invalid refresh time/);
});

test("Exactly 1000 records are allowed; unbounded lists and repeated identifiers are rejected", () => {
    const value = sample();
    value.quests = Array.from({ length: 1000 }, () => ({ ...value.quests[0], id: randomUUID() }));
    assert.equal(validateSnapshot(value, now).quests.length, 1000);
    value.quests.push({ ...value.quests[0], id: randomUUID() });
    assert.throws(() => validateSnapshot(value, now), /invalid/);
    value.quests = [value.quests[0], value.quests[0]];
    assert.throws(() => validateSnapshot(value, now), /invalid/);
});

for (const [field, values] of Object.entries({
    title: ["ab", "a".repeat(121), null, 42, "\u0000bad"],
    location: ["", "a".repeat(501), null],
    id: ["", "not-a-guid", "00000000-0000-0000-0000-000000000000"],
    eventId: [null, "not-a-guid"],
    startUtc: ["2026-02-30T10:00:00Z", "2026-10-01T10:00:00+02:00", "yesterday", null],
    endUtc: ["2026-10-01T10:00:00Z", "2026-10-01T09:59:59Z"],
    timeZoneId: ["Invalid/Zone", "a".repeat(101), "+01:00", null],
    status: [-1, 6, 1.5, "Active", null]
})) {
    test(`Malformed ${field} cannot be persisted or rendered`, () => {
        for (const candidate of values) {
            const value = sample();
            value.quests[0][field] = candidate;
            assert.throws(() => validateSnapshot(value, now));
        }
    });
}

test("Valid title/location limits and all known lifecycle statuses remain displayable", () => {
    for (const status of [0, 1, 2, 3, 4, 5]) {
        const value = sample();
        value.quests[0].title = "a".repeat(120);
        value.quests[0].location = "b".repeat(500);
        value.quests[0].status = status;
        assert.equal(validateSnapshot(value, now).quests[0].status, status);
    }
});
