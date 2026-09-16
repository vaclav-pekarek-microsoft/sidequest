import { beforeAuthenticationChange, afterAuthenticationSuccess, reportCircuitConnection } from "./Experience/ConnectionStatus.razor.js";

const warningText = "Device basics could not be cleared. Sign-out continues, but close other Sidequest tabs and clear this site's data in browser settings before sharing this device.";
let changingAuthentication = false;
let blazor;
let retrying = false;
let reconnect;

function showWarning() {
    const warning = document.querySelector("[data-authentication-warning]");
    if (warning) { warning.textContent = warningText; warning.hidden = false; }
}

function field(form, name, value) {
    let input = form.querySelector(`input[name="${name}"]`);
    if (!input) {
        input = document.createElement("input");
        input.type = "hidden";
        input.name = name;
        form.append(input);
    }
    input.value = value;
}

async function changeAuthentication(target, submitter) {
    if (changingAuthentication) return;
    changingAuthentication = true;
    let epoch = "";
    let failed = false;
    try { epoch = await beforeAuthenticationChange(); }
    catch { failed = true; showWarning(); }
    if (target instanceof HTMLFormElement) {
        field(target, "experienceEpoch", epoch);
        field(target, "experienceClearFailed", String(failed));
        if (submitter?.name) field(target, submitter.name, submitter.value);
        // The original submit event already passed native validation. Do not re-enter its submission algorithm.
        HTMLFormElement.prototype.submit.call(target);
    } else {
        const url = new URL(target.href, location.href);
        url.searchParams.set("experienceEpoch", epoch);
        location.assign(url.href);
    }
}

function bindReconnect() {
    const current = document.getElementById("components-reconnect-modal");
    if (!current || current === reconnect) return;
    reconnect = current;
    current.addEventListener("components-reconnect-state-changed", event => {
        const state = event.detail?.state;
        reportCircuitConnection(state === "hide");
        current.querySelector("[data-reconnect-message]").textContent = state === "rejected" ?
            "The server could not restore this session. Copy unsaved input before reloading; no actions were retried." :
            "Connection interrupted. Online actions are unavailable; unsaved input is kept. Reconnecting does not retry actions.";
    });
    current.querySelector("[data-reconnect-retry]").addEventListener("click", async event => {
        if (!blazor || retrying) return;
        retrying = true;
        event.currentTarget.disabled = true;
        try {
            const restored = await blazor.reconnect();
            reportCircuitConnection(restored);
            if (restored) current.className = "components-reconnect-hide";
            else current.querySelector("[data-reconnect-message]").textContent = "The server rejected this session. Copy unsaved input before reloading.";
        } catch {
            reportCircuitConnection(false);
            current.querySelector("[data-reconnect-message]").textContent = "Reconnection failed. Your input is kept; check the connection before trying again.";
        } finally {
            retrying = false;
            current.querySelector("[data-reconnect-retry]").disabled = false;
        }
    });
    current.querySelector("[data-reconnect-reload]").addEventListener("click", () => location.reload());
}

export async function beforeWebStart() {
    document.addEventListener("submit", async event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute("data-authentication-change")) return;
        event.preventDefault();
        await changeAuthentication(form, event.submitter);
    }, true);
    document.addEventListener("click", async event => {
        const link = event.target.closest?.("a[data-authentication-change]");
        if (!link) return;
        event.preventDefault();
        await changeAuthentication(link);
    }, true);
    bindReconnect();
    const completion = document.querySelector("[data-authentication-completion]");
    if (!completion) return;
    try {
        if (!completion.dataset.authenticationCompletion) throw new Error("No successful sign-in generation is available.");
        const proof = completion.dataset.authenticationBinding;
        if (!proof) throw new Error("No protected sign-in session is available.");
        const response = await fetch("/experience/session", {
            credentials: "same-origin", cache: "no-store", redirect: "manual",
            headers: { "X-Sidequest-Circuit-Binding": proof },
            signal: AbortSignal.timeout(10_000)
        });
        if (response.status !== 204) throw new Error("The completed sign-in is no longer the current session.");
        await afterAuthenticationSuccess(completion.dataset.authenticationCompletion);
        location.replace(completion.querySelector("[data-authentication-continue]").href);
    } catch {
        completion.querySelector("[data-authentication-result]").textContent =
            "Sign-in succeeded, but device saving could not be activated or this sign-in was superseded. Nothing was refreshed. Continue uses the current online account; saving still requires a successful authorized refresh. If clearing failed, clear this site's data before sharing the device.";
    }
}

export function afterWebStarted(instance) {
    blazor = instance;
    instance.addEventListener("enhancedload", bindReconnect);
}
