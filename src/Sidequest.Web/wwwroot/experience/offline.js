import { currentEpoch, readSnapshot, clearAccount, maximumAge, statusLabel } from "./snapshot-store.js?v=1";
import { refreshJoined } from "./refresh.js?v=1";

const list = document.querySelector("#quests");
const refresh = document.querySelector("#refresh");
const failure = document.querySelector("#failure");
let expiry;
let renderVersion = 0;
let rendering;
let renderRequested = false;
const channel = typeof BroadcastChannel === "function" ? new BroadcastChannel("sidequest-experience") : null;

function textElement(tag, value) {
    const element = document.createElement(tag);
    element.textContent = value;
    return element;
}

function render() {
    // Invalid reads purge storage; overlapping lifecycle reads must not erase the resulting warning.
    renderRequested = true;
    return rendering ??= drainRenders();
}

async function drainRenders() {
    try {
        while (renderRequested) {
            renderRequested = false;
            await renderOnce();
        }
    } finally { rendering = null; }
}

async function renderOnce() {
    const version = ++renderVersion;
    clearTimeout(expiry);
    list.replaceChildren();
    try {
        const snapshot = await readSnapshot();
        if (version !== renderVersion) return;
        if (!snapshot) {
            refresh.textContent = failure.textContent ?
                "Offline — saved basics unavailable." : "Offline — no saved joined basics available.";
            return;
        }
        failure.textContent = "";
        refresh.textContent = `Offline — last refreshed ${new Date(snapshot.refreshedUtc).toLocaleString()}. This copy may be stale.`;
        for (const quest of snapshot.quests) {
            const card = document.createElement("article");
            const options = { timeZone: quest.timeZoneId, dateStyle: "medium", timeStyle: "short" };
            const formatter = new Intl.DateTimeFormat(undefined, options);
            card.append(textElement("h2", quest.title), textElement("p", quest.location),
                textElement("p", `${formatter.format(new Date(quest.startUtc))} – ${formatter.format(new Date(quest.endUtc))} (Event time: ${quest.timeZoneId})`),
                textElement("p", `Last-known status: ${statusLabel(quest.status)}. Not an actionable invitation.`));
            const browserZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
            if (browserZone && browserZone !== quest.timeZoneId)
                card.append(textElement("p", `Your device time (${browserZone}): ${new Date(quest.startUtc).toLocaleString()} – ${new Date(quest.endUtc).toLocaleString()}`));
            list.append(card);
        }
        if (!snapshot.quests.length) list.append(textElement("p", "No joined Quests in the last authorized refresh."));
        expiry = setTimeout(render, Math.max(0, Date.parse(snapshot.refreshedUtc) + maximumAge - Date.now()));
    } catch (error) {
        if (version !== renderVersion) return;
        refresh.textContent = "Offline — saved basics unavailable.";
        failure.textContent = error instanceof Error ? error.message : "Device storage failed.";
    }
}

document.querySelector("#clear").addEventListener("click", async () => {
    renderVersion++;
    clearTimeout(expiry);
    list.replaceChildren();
    try {
        await clearAccount();
        failure.textContent = "";
        channel?.postMessage("account-cleared");
        try { localStorage.setItem("sidequest-experience-signal", crypto.randomUUID()); }
        catch { if (!channel) failure.textContent = "Other tabs could not be notified. Close other Sidequest tabs."; }
        refresh.textContent = "Saved basics cleared. Sign in online to enable saving again.";
    } catch { failure.textContent = "Device storage could not be cleared. Clear this site's data in browser settings."; }
});
channel?.addEventListener("message", render);
document.addEventListener("visibilitychange", render);
window.addEventListener("pageshow", render);
window.addEventListener("online", async () => {
    try {
        const epoch = await currentEpoch();
        if (epoch) await refreshJoined(epoch);
        await render();
    } catch {
        await render();
        failure.textContent = "Online refresh failed. No freshness was advanced; saved basics may be stale or unavailable.";
    }
});
window.addEventListener("storage", event => {
    if (event.key === "sidequest-experience-signal") render();
});
await render();
