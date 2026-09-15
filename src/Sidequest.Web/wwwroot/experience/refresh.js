import { beginRefresh, clearAccount, discardSnapshot, replaceSnapshot } from "./snapshot-store.js?v=1";

const endpoint = "/experience/joined-snapshot";
const maximumBytes = 2 * 1024 * 1024;

async function boundedJson(response) {
    if (!response.headers.get("content-type")?.includes("application/json") || !response.body)
        throw new Error("Authorized basics were not returned. Saved freshness is unchanged.");
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let bytes = 0;
    let content = "";
    try {
        while (true) {
            const chunk = await reader.read();
            if (chunk.done) break;
            bytes += chunk.value.byteLength;
            if (bytes > maximumBytes) throw new Error("Joined basics exceed the device storage limit.");
            content += decoder.decode(chunk.value, { stream: true });
        }
        content += decoder.decode();
        try { return JSON.parse(content); }
        catch { throw new Error("Authorized basics were malformed. Saved freshness is unchanged."); }
    } finally {
        await reader.cancel();
        reader.releaseLock();
    }
}

export async function refreshJoined(epoch, signal) {
    const ticket = await beginRefresh(epoch);
    const response = await fetch(endpoint, {
        credentials: "same-origin", cache: "no-store", redirect: "manual",
        headers: { Accept: "application/json" }, signal
    });
    if ([401, 403].includes(response.status) || response.type === "opaqueredirect") {
        await clearAccount(ticket);
        throw new Error("Sign-in or access was lost. Saved basics were cleared.");
    }
    if (!response.ok)
        throw new Error("Authorized refresh failed. Saved freshness is unchanged.");
    try {
        const snapshot = await boundedJson(response);
        if (signal?.aborted) throw new DOMException("Refresh cancelled", "AbortError");
        await replaceSnapshot(ticket, snapshot);
        return snapshot.refreshedUtc;
    } catch (failure) {
        // A successful authorization response must not leave obsolete grants when its replacement cannot be stored.
        await discardSnapshot(ticket);
        throw failure;
    }
}
