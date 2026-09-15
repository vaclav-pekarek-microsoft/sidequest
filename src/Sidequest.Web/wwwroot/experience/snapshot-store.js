// Only this explicit Joined projection enters IndexedDB. Local values never grant access.
export const maximumAge = 24 * 60 * 60 * 1000;
export const databaseName = "sidequest-experience-v1";
const id = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const zeroId = "00000000-0000-0000-0000-000000000000";
const instant = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/;
const statuses = ["Draft", "Active", "Suspended", "Completed", "Cancelled", "Archived"];
const exact = (value, names) => value !== null && typeof value === "object" && !Array.isArray(value) &&
    Object.keys(value).length === names.length && names.every(name => Object.hasOwn(value, name));
const guid = value => typeof value === "string" && id.test(value) && value !== zeroId;
const text = (value, max, min = 1) => typeof value === "string" && value.length >= min && value.length <= max &&
    !/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/.test(value);
const date = value => typeof value === "string" && instant.test(value) && Number.isFinite(Date.parse(value)) &&
    new Date(value).toISOString().slice(0, 19) === value.slice(0, 19);

export function validateSnapshot(value, now = Date.now()) {
    if (!exact(value, ["accountId", "refreshedUtc", "quests"]) || !guid(value.accountId) ||
        !date(value.refreshedUtc) || !Array.isArray(value.quests) || value.quests.length > 1000)
        throw new Error("Saved basics are invalid and have been removed.");
    const refreshed = Date.parse(value.refreshedUtc);
    if (refreshed > now || now - refreshed >= maximumAge)
        throw new Error("Saved basics expired or have an invalid refresh time and have been removed.");
    const ids = new Set();
    for (const quest of value.quests) {
        if (!exact(quest, ["id", "eventId", "title", "location", "startUtc", "endUtc", "timeZoneId", "status"]) ||
            !guid(quest.id) || !guid(quest.eventId) || ids.has(quest.id) ||
            !text(quest.title, 120, 3) || !text(quest.location, 500) ||
            !date(quest.startUtc) || !date(quest.endUtc) || Date.parse(quest.endUtc) <= Date.parse(quest.startUtc) ||
            !text(quest.timeZoneId, 100) || !/^[A-Za-z_]+(?:\/[A-Za-z0-9_+-]+)*$/.test(quest.timeZoneId) ||
            !(Number.isInteger(quest.status) && quest.status >= 0 && quest.status < statuses.length))
            throw new Error("Saved basics are invalid and have been removed.");
        try { new Intl.DateTimeFormat("en", { timeZone: quest.timeZoneId }); }
        catch { throw new Error("Saved time zone is invalid and the basics have been removed."); }
        ids.add(quest.id);
    }
    return value;
}

export function statusLabel(status) { return statuses[status] ?? "Unknown"; }

function openDatabase() {
    return new Promise((resolve, reject) => {
        let request;
        try { request = indexedDB.open(databaseName, 1); }
        catch { reject(new Error("Device storage is unavailable. Basics were not saved.")); return; }
        request.onupgradeneeded = () => request.result.createObjectStore("state");
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(new Error("Device storage is unavailable. Basics were not saved."));
        request.onblocked = () => reject(new Error("Device storage is blocked by another tab."));
    });
}

function newState(blocked = false) {
    return { epoch: crypto.randomUUID(), revision: 0, blocked, snapshot: null };
}

function validState(value) {
    return exact(value, ["epoch", "revision", "blocked", "snapshot"]) && guid(value.epoch) &&
        Number.isSafeInteger(value.revision) && value.revision >= 0 && typeof value.blocked === "boolean";
}

async function transaction(update) {
    const db = await openDatabase();
    try {
        return await new Promise((resolve, reject) => {
            const tx = db.transaction("state", "readwrite");
            const store = tx.objectStore("state");
            const read = store.get("last-account");
            let result;
            let error;
            let invalidMetadata = false;
            read.onsuccess = () => {
                try {
                    invalidMetadata = read.result !== undefined && !validState(read.result);
                    const state = read.result === undefined ? newState() :
                        validState(read.result) ? read.result : newState(true);
                    result = update(state);
                    store.put(state, "last-account");
                } catch (failure) {
                    error = failure instanceof DOMException ? new Error("Device storage failed. Basics were not saved.") : failure;
                    tx.abort();
                }
            };
            tx.oncomplete = () => invalidMetadata ?
                reject(new Error("Device cache metadata was invalid and has been cleared. Sign in online again.")) : resolve(result);
            tx.onabort = tx.onerror = () => reject(error ?? new Error("Device storage failed. Basics were not saved."));
        });
    } finally { db.close(); }
}

export async function currentEpoch() {
    return transaction(state => state.blocked ? null : state.epoch);
}

export async function beginRefresh(epoch) {
    return transaction(state => {
        if (state.blocked || state.epoch !== epoch)
            throw new Error("Account changed or signed out. Reload online before refreshing.");
        if (state.revision >= Number.MAX_SAFE_INTEGER)
            throw new Error("Device refresh limit reached. Clear saved basics.");
        state.revision++;
        return { epoch: state.epoch, revision: state.revision };
    });
}

export async function replaceSnapshot(ticket, snapshot, now = Date.now()) {
    validateSnapshot(snapshot, now);
    return transaction(state => {
        if (state.blocked || state.epoch !== ticket.epoch || state.revision !== ticket.revision)
            throw new Error("An outdated refresh was discarded. No saved basics were changed.");
        state.snapshot = snapshot;
    });
}

export async function readSnapshot(now = Date.now()) {
    const result = await transaction(state => {
        if (state.blocked) { state.snapshot = null; return { snapshot: null }; }
        if (state.snapshot === null) return { snapshot: null };
        try { return { snapshot: validateSnapshot(state.snapshot, now) }; }
        catch (failure) {
            state.snapshot = null;
            return { snapshot: null, error: failure.message };
        }

    });
    if (result.error) throw new Error(result.error);
    return result.snapshot;
}

export async function discardSnapshot(ticket) {
    return transaction(state => {
        if (state.epoch === ticket.epoch && state.revision === ticket.revision) state.snapshot = null;
    });
}

// Auth callers await this BEFORE submitting logout or changing accounts.
export async function clearAccount(ticket = null) {
    return transaction(state => {
        if (ticket && (state.epoch !== ticket.epoch || state.revision !== ticket.revision)) return;
        Object.assign(state, newState(true));
        return state.epoch;
    });
}

// A delayed HTTP session denial must not clear a later sign-in's generation.
export async function clearAccountForEpoch(epoch) {
    return transaction(state => {
        if (state.epoch !== epoch) return false;
        Object.assign(state, newState(true));
        return true;
    });
}

// Invoke only on the successful online sign-in completion boundary, never circuit reconnect.
export async function completeAuthentication(expectedEpoch) {
    return transaction(state => {
        if (!guid(expectedEpoch) || !state.blocked || state.epoch !== expectedEpoch)
            throw new Error("Sign-in completion is outdated or device clearing was unavailable. Saved basics remain blocked; sign in again to enable saving.");
        Object.assign(state, newState());
    });
}
