import { currentEpoch, readSnapshot, clearAccount, clearAccountForEpoch, completeAuthentication } from "/experience/snapshot-store.js?v=1";
import { refreshJoined } from "/experience/refresh.js?v=1";

const callbackName = "ConnectionChangedAsync";
const connectionEvent = "sidequest:connection";
const authEvent = "sidequest:account-cleared";
let circuitConnected = true;
const channel = typeof BroadcastChannel === "function" ? new BroadcastChannel("sidequest-experience") : null;
const signalKey = "sidequest-experience-signal";

function broadcastClear() {
    channel?.postMessage("account-cleared");
    try { localStorage.setItem(signalKey, crypto.randomUUID()); }
    catch {
        if (!channel) throw new Error("Saved basics were cleared, but other tabs could not be notified. Close other Sidequest tabs.");
    }
}

// The host listens to the framework's actual reconnect-state events.
export function reportCircuitConnection(connected) {
    circuitConnected = connected === true;
    document.dispatchEvent(new CustomEvent(connectionEvent));
}

// The host awaits this before submitting logout/account-switch requests.
export async function beforeAuthenticationChange() {
    document.dispatchEvent(new CustomEvent(authEvent));
    try { return await clearAccount(); }
    finally { broadcastClear(); }
}

// The static completion page reads this generation only from a successfully authenticated cookie ticket.
export async function afterAuthenticationSuccess(expectedEpoch) {
    await completeAuthentication(expectedEpoch);
    broadcastClear();
}

class ConnectionBridge {
    #root;
    #callback;
    #events = new AbortController();
    #request;
    #epoch;
    #install;
    #observer;
    #cleared = false;
    #lastConnected;
    #pending = false;
    #queuedRefresh = false;
    #reauthorized = false;
    #connectionVersion = 0;
    #clearFailure;

    constructor(root, callback) { this.#root = root; this.#callback = callback; }
    async start() {
        const options = { signal: this.#events.signal };
        window.addEventListener("online", () => this.connection(), options);
        window.addEventListener("offline", () => this.connection(), options);
        document.addEventListener(connectionEvent, () => this.connection(), options);
        document.addEventListener(authEvent, () => this.clearVisible(), options);
        channel?.addEventListener("message", () => this.clearVisible(), options);
        window.addEventListener("storage", event => {
            if (event.key === signalKey) this.clearVisible();
        }, options);
        this.#root.querySelector("[data-refresh]").addEventListener("click", async () => {
            await this.connection();
            await this.refresh();
        }, options);
        window.addEventListener("beforeinstallprompt", event => {
            event.preventDefault();
            this.#install = event;
            this.#root.querySelector("[data-install]").hidden = false;
        }, options);
        this.#root.querySelector("[data-install]").addEventListener("click", async () => {
            if (!this.#install) return;
            try {
                await this.#install.prompt();
                const choice = await this.#install.userChoice;
                this.#root.querySelector("[data-install-status]").textContent =
                    choice.outcome === "accepted" ? "Install request accepted by the browser." : "Installation dismissed. You can keep using the browser.";
            } catch { this.#root.querySelector("[data-install-status]").textContent = "Installation is unavailable. See browser installation guidance."; }
            this.#install = null;
            this.#root.querySelector("[data-install]").hidden = true;
        }, options);
        this.#observer = new MutationObserver(() => {
            if (this.#root.isConnected) this.gate(); else this.dispose();
        });
        this.#observer.observe(document.body, { childList: true, subtree: true });
        try {
            this.#epoch = await currentEpoch();
            const saved = await readSnapshot();
            if (saved) this.status(`Saved basics last refreshed ${new Date(saved.refreshedUtc).toLocaleString()}; times, status and access may have changed.`);
        } catch (error) {
            this.status(error instanceof Error ? error.message : "Device storage is unavailable. Basics have not been saved.");
        }
        if ("serviceWorker" in navigator && isSecureContext) {
            try { await navigator.serviceWorker.register("/service-worker.js", { scope: "/" }); }
            catch { this.#root.querySelector("[data-install-status]").textContent = "Offline launch assets could not be installed. Online use remains available."; }
        } else this.#root.querySelector("[data-install-status]").textContent = "This browser or connection does not support offline installation. Use HTTPS in deployment.";
        await this.connection();
        await this.refresh();
    }
    get connected() { return navigator.onLine && circuitConnected && !this.#cleared && !this.#events.signal.aborted; }
    status(value) { this.#root.querySelector("[data-snapshot]").textContent = this.#clearFailure ?? value; }
    gate() {
        for (const element of document.querySelectorAll("[data-online-actions]"))
            element.inert = !this.connected || !this.#reauthorized;
        if (this.#cleared)
            for (const element of document.querySelectorAll("[data-protected-experience]")) element.hidden = true;
        this.#root.querySelector("[data-refresh]").disabled = !this.connected || this.#pending;
    }
    clearVisible() {
        this.#cleared = true;
        this.#request?.abort();
        this.#epoch = null;
        this.status("Account changed or signed out. Refresh stopped; reload online. If clearing failed, use browser site-data settings.");
        this.gate();
        // A disconnected old circuit cannot update itself, so hide protected display immediately.
        for (const element of document.querySelectorAll("[data-protected-experience]")) element.hidden = true;
    }
    async connection() {
        if (!this.connected) this.#reauthorized = false;
        this.gate();
        this.#root.querySelector("[data-connection]").textContent = this.connected ?
            "Connected — checking current access before enabling online actions." :
            "Offline or disconnected — online actions unavailable. Unsaved input is kept; no mutations will be retried.";
        if (!this.connected) this.#request?.abort();
        if (this.#lastConnected === this.connected) return;
        this.#lastConnected = this.connected;
        const version = ++this.#connectionVersion;
        const connected = this.connected;
        let zone = null;
        try { zone = Intl.DateTimeFormat().resolvedOptions().timeZone; } catch { /* Labeled Event-zone fallback. */ }
        try {
            if (connected && document.querySelector('[data-requires-session="true"]')) {
                const checkedEpoch = this.#epoch;
                const response = await fetch("/experience/session", {
                    credentials: "same-origin", cache: "no-store", redirect: "manual"
                });
                if (version !== this.#connectionVersion || !this.connected) return;
                if ([401, 403].includes(response.status) || response.type === "opaqueredirect") {
                    this.clearVisible();
                    try {
                        if (await clearAccountForEpoch(checkedEpoch)) broadcastClear();
                        else if (!checkedEpoch) throw new Error("The device generation could not be verified.");
                    } catch {
                        this.#clearFailure = "Current access was lost and device clearing could not be verified. Close other Sidequest tabs and clear this site's data before sharing the device.";
                        this.status(this.#clearFailure);
                    }
                    throw new Error("Current HTTP session was rejected.");
                }
                if (response.status !== 204) throw new Error("Current HTTP session could not be checked.");
            }
            await this.#callback.invokeMethodAsync(callbackName, connected, zone);
            if (version !== this.#connectionVersion) return;
            this.#reauthorized = this.connected;
            this.gate();
            if (this.connected)
                this.#root.querySelector("[data-connection]").textContent = "Connected — actions still require current server authorization.";
        }
        catch {
            if (version !== this.#connectionVersion) return;
            this.#root.querySelector("[data-connection]").textContent = "Server connection or current access unavailable. Retry refresh or reconnect before acting.";
            this.#reauthorized = false;
            this.#lastConnected = undefined;
            this.gate();
        }
    }
    async refresh() {
        if (!this.connected || !this.#epoch) {
            this.status("Saving requires an online signed-in session and available device storage. Sign in again if saving was cleared.");
            return;
        }
        if (this.#pending) { this.#queuedRefresh = true; return; }
        this.#pending = true;
        this.#request = new AbortController();
        this.gate();
        try {
            const refreshed = await refreshJoined(this.#epoch, this.#request.signal);
            this.status(`Joined basics saved — last refreshed ${new Date(refreshed).toLocaleString()}. Valid for at most 24 hours; access may change.`);
        } catch (error) {
            this.status(error?.name === "AbortError" ? "Refresh interrupted. Saved freshness is unchanged." :
                error instanceof TypeError ? "Network refresh failed. Saved freshness is unchanged." :
                error instanceof Error ? error.message : "Device refresh failed. Saved freshness is unchanged.");
        } finally { this.#pending = false; this.gate(); }
        if (this.#queuedRefresh && !this.#events.signal.aborted) {
            this.#queuedRefresh = false;
            await this.refresh();
        }
    }
    dispose() {
        this.#events.abort();
        this.#request?.abort();
        this.#observer?.disconnect();
    }
}

export async function initialize(root, callback) {
    const bridge = new ConnectionBridge(root, callback);
    await bridge.start();
    return bridge;
}
