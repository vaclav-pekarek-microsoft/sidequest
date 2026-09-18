import { spawn } from "node:child_process";

const messages = Object.freeze({
    usage: "Planning usage: node infra/deployment/plan-workflow.mjs gate|plan|--help.",
    context: "Planning context rejected. Dispatch a fresh run from this repository's current main source.",
    optin: "Planning is disabled. The deployment owner must explicitly enable the repository planning opt-in.",
    acknowledgement: "Planning requires the owner's explicit acknowledgement of manually verified administrator controls.",
    metadata: "Planning metadata failed. Check GitHub read permissions, response completeness and existing protection settings.",
    source: "Planning source failed. Require the exact checked-out main SHA and successful accepted CI for that source.",
    environment: "Planning environment failed. Require an existing environment, explicit User reviewers, self-review prevention and only the main branch rule.",
    settings: "Planning settings failed. Supply approved environment configuration and disabled-only valid template parameters.",
    compiler: "Planning compilation failed. Verify the pinned compiler download, hash and exact source template.",
    tooling: "Planning tooling failed. Require a supported Azure CLI 2.x version at least 2.76.0.",
    oidc: "Planning authentication failed at OIDC. Check the trusted runner endpoint, audience and federated identity configuration.",
    login: "Planning authentication failed at Azure login. Check the approved federation and subscription access.",
    account: "Planning account binding failed. Check the authenticated tenant, subscription and service-principal client.",
    resourceGroup: "Planning resource-group binding failed. The approved resource group must already exist in the selected subscription.",
    validate: "Planning ARM validation failed or was incomplete. Review through the separately approved private diagnostic process.",
    whatif: "Planning what-if failed or was incomplete. Review through the separately approved private diagnostic process.",
    cleanup: "Planning cleanup failed. Discard this run and have the deployment owner inspect the isolated runner.",
    output: "Planning output failed. Discard incomplete output; it is not authorization.",
});

export class PlanError extends Error {
    constructor(stage) {
        const safeStage = Object.hasOwn(messages, stage) ? stage : "context";
        super(messages[safeStage]);
        this.name = "PlanError";
        this.stage = safeStage;
    }
}

export function requirePlan(condition, stage) {
    if (!condition) throw new PlanError(stage);
}

export async function atStage(stage, action) {
    try {
        return await action();
    } catch {
        throw new PlanError(stage);
    }
}

export function parseJson(text, stage, limit = 2 * 1024 * 1024) {
    requirePlan(typeof text === "string" && Buffer.byteLength(text, "utf8") <= limit, stage);
    try {
        return JSON.parse(text);
    } catch {
        throw new PlanError(stage);
    }
}

export async function readResponse(response, stage, limit) {
    requirePlan(response.status === 200 && response.body, stage);
    const length = response.headers.get("content-length");
    requirePlan(length === null || (/^\d+$/.test(length) && Number(length) <= limit), stage);
    let size = 0;
    const chunks = [];
    return atStage(stage, async () => {
        for await (const chunk of response.body) {
            size += chunk.length;
            requirePlan(size <= limit, stage);
            chunks.push(chunk);
        }
        return Buffer.concat(chunks);
    });
}

/** GET only, no redirects, no raw HTTP failures, no uncertain pagination. */
export async function getJson(url, token, stage = "metadata", fetcher = fetch) {
    return atStage(stage, async () => {
        const response = await fetcher(url, {
            method: "GET", redirect: "error", signal: AbortSignal.timeout(30_000),
            headers: {
                Accept: "application/vnd.github+json",
                Authorization: `Bearer ${token}`,
                "X-GitHub-Api-Version": "2026-03-10",
            },
        });
        requirePlan(!response.headers.get("link"), stage);
        requirePlan(/^application\/json(?:;|$)/i.test(response.headers.get("content-type") ?? ""), stage);
        const data = await readResponse(response, stage, 2 * 1024 * 1024);
        return parseJson(new TextDecoder("utf-8", { fatal: true }).decode(data), stage);
    });
}

/**
 * Captures both streams, never uses a shell, bounds execution and output, and
 * never includes arguments, child diagnostics or exceptions in an error.
 */
export async function runCaptured(file, args, { stage, cwd, env, timeout = 300_000, limit = 8 * 1024 * 1024 } = {}) {
    return new Promise((resolve, reject) => {
        let child;
        let size = 0;
        let failed = false;
        const stdout = [];
        const stop = () => {
            failed = true;
            child?.kill("SIGKILL");
        };
        try {
            child = spawn(file, args, { cwd, env, shell: false, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
        } catch {
            reject(new PlanError(stage));
            return;
        }
        const timer = setTimeout(stop, timeout);
        child.on("error", () => {
            clearTimeout(timer);
            failed = true;
            reject(new PlanError(stage));
        });
        for (const [stream, retain] of [[child.stdout, true], [child.stderr, false]]) {
            stream.on("data", chunk => {
                size += chunk.length;
                if (size > limit) stop();
                else if (retain && !failed) stdout.push(chunk);
            });
            stream.on("error", stop);
        }
        child.on("close", (code, signal) => {
            clearTimeout(timer);
            if (failed || code !== 0 || signal !== null) reject(new PlanError(stage));
            else {
                try {
                    resolve(new TextDecoder("utf-8", { fatal: true }).decode(Buffer.concat(stdout)));
                } catch {
                    reject(new PlanError(stage));
                }
            }
        });
    });
}
