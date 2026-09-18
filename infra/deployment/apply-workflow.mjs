import { createHash } from "node:crypto";
import { appendFile, open } from "node:fs/promises";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { buildArmParameters } from "./parameter-validation.mjs";
import {
    APPLY_WORKFLOW, bindAccount, contextFromEnvironment, metadataGate, readWorkflowInputs,
    runtimeIo, summarizeWhatIf, validateOidcToken, validateSettings,
} from "./plan-workflow.mjs";
import { getJson, parseJson, PlanError, runCaptured } from "./plan-io.mjs";

const messages = Object.freeze({
    usage: "Apply helper usage: gate|apply|--help, or fingerprints TEMPLATE PARAMETERS FULL_WHAT_IF (local files only).",
    context: "Apply context rejected. Dispatch a fresh infrastructure-apply run from the exact current main source.",
    optin: "Infrastructure apply is disabled. Require its separate explicit deployment-owner opt-in.",
    acknowledgement: "Apply requires separate owner acknowledgement of manually verified deployment administrator controls.",
    source: "Apply source rejected. Require the exact checked-out main source and successful accepted CI.",
    metadata: "Apply metadata failed. Check supported GitHub read permissions and complete responses.",
    environment: "Apply environment rejected. Require the existing separate protected apply environment and unchanged identity.",
    settings: "Apply settings rejected. Supply valid disabled-only parameters and distinct approved deployment and planning clients.",
    approval: "Apply approval rejected. Require a current run-bound owner review record with exact source, scope, identities and no application artifact.",
    fingerprints: "Apply fingerprints rejected. Require exact reviewed template, disabled parameters and complete full-payload what-if.",
    compiler: "Apply compilation failed. Check the pinned compiler and exact-source template contract.",
    tooling: "Apply tooling rejected. Require Azure CLI 2.x at least 2.76.0.",
    oidc: "Apply OIDC rejected. Check the apply workflow/environment federation and trusted runtime claims.",
    login: "Apply login failed. Check the distinct approved deployment identity and federation.",
    account: "Apply account binding failed. Check the approved deployment client, tenant and subscription.",
    resourceGroup: "Apply scope rejected. The approved resource group must already exist in the bound subscription.",
    validate: "Apply ARM validation failed or was incomplete. Obtain a new private review; do not weaken validation.",
    whatif: "Apply what-if did not match the approved full private review. Obtain fresh review; no automatic override exists.",
    create: "Infrastructure apply did not return verified disabled completion. Inspect the deployment privately before any new run.",
    cleanup: "Apply cleanup failed. Inspect the isolated runner and deployment privately before any new run.",
    output: "Apply reporting failed. Discard incomplete reporting and inspect deployment state privately.",
});

export class ApplyError extends Error {
    constructor(stage, mayHaveChanged = false) {
        const safe = Object.hasOwn(messages, stage) ? stage : "context";
        super(`${messages[safe]}${mayHaveChanged ? " Resources may have changed; do not automatically retry or roll back." : ""}`);
        this.name = "ApplyError";
        this.stage = safe;
        this.mayHaveChanged = mayHaveChanged;
    }
}

const check = (condition, stage) => { if (!condition) throw new ApplyError(stage); };
const object = value => value !== null && typeof value === "object" && !Array.isArray(value);
const same = (a, b) => typeof a === "string" && typeof b === "string" && a.toLowerCase() === b.toLowerCase();
const digest = bytes => createHash("sha256").update(bytes).digest("hex");
const sha256 = value => typeof value === "string" && /^[a-f0-9]{64}$/.test(value);
const groupId = settings => `/subscriptions/${settings.subscriptionId}/resourceGroups/${settings.resourceGroup}`;

async function stage(name, action) {
    try { return await action(); } catch { throw new ApplyError(name); }
}

/** Deterministic object-key order; array order and every provider field are preserved. */
export function canonicalJson(value, depth = 0) {
    check(depth <= 32, "fingerprints");
    if (value === null || ["string", "boolean"].includes(typeof value)) return JSON.stringify(value);
    if (typeof value === "number") {
        check(Number.isFinite(value), "fingerprints");
        return JSON.stringify(value);
    }
    check(typeof value === "object", "fingerprints");
    if (Array.isArray(value)) {
        check(Reflect.ownKeys(value).length === value.length + 1, "fingerprints");
        return `[${Array.from({ length: value.length }, (_, index) => {
            const property = Object.getOwnPropertyDescriptor(value, String(index));
            check(property && Object.hasOwn(property, "value"), "fingerprints");
            return canonicalJson(property.value, depth + 1);
        }).join(",")}]`;
    }
    check([Object.prototype, null].includes(Object.getPrototypeOf(value)), "fingerprints");
    const keys = Reflect.ownKeys(value);
    check(keys.every(key => typeof key === "string"), "fingerprints");
    return `{${keys.sort().map(key => {
        const property = Object.getOwnPropertyDescriptor(value, key);
        check(property.enumerable && Object.hasOwn(property, "value"), "fingerprints");
        return `${JSON.stringify(key)}:${canonicalJson(property.value, depth + 1)}`;
    }).join(",")}}`;
}

export function reviewFingerprints(templateBytes, parameters, whatIf) {
    check(Buffer.isBuffer(templateBytes) && templateBytes.length <= 8 * 1024 * 1024, "fingerprints");
    check(object(parameters) && parameters.parameters?.enableApplication?.value === false, "fingerprints");
    check(Buffer.byteLength(canonicalJson(parameters)) <= 32768, "fingerprints");
    check(Buffer.byteLength(canonicalJson(whatIf)) <= 8 * 1024 * 1024, "fingerprints");
    // Reject ResourceIdOnly/Deploy, unresolved and partial results. A zero-change
    // result is explicit evidence, never a fallback for missing provider output.
    summarizeWhatIf(whatIf, { sourceSha: "", environmentName: "" });
    for (const change of whatIf.changes) {
        check(typeof change.resourceId === "string" && change.resourceId.startsWith("/subscriptions/"), "fingerprints");
        check(change.changeType !== "Deploy", "fingerprints");
        if (["Create", "Modify"].includes(change.changeType)) check(object(change.after), "fingerprints");
        if (["Delete", "Modify"].includes(change.changeType)) check(object(change.before), "fingerprints");
    }
    check(new Set(whatIf.changes.map(change => change.resourceId.toLowerCase())).size === whatIf.changes.length, "fingerprints");
    return {
        templateSha256: digest(templateBytes),
        parametersSha256: digest(canonicalJson(parameters)),
        whatIfSha256: digest(canonicalJson(whatIf)),
    };
}

const approvalKeys = [
    "version", "purpose", "applyRunId", "sourceSha", "templateSha256", "parametersSha256", "whatIfSha256",
    "applicationArtifact", "tenantId", "subscriptionId", "resourceGroup", "deploymentClientId", "planningClientId",
    "permissionScope", "leastPrivilegeReviewed", "roleAssignmentsReviewed", "reviewReference", "reviewedAt", "expiresAt",
];

export function parseApproval(json) {
    check(typeof json === "string" && Buffer.byteLength(json) <= 16384, "approval");
    try {
        const tokens = [...json.matchAll(/"(?:[^"\\]|\\[\s\S])*"|[{}[\]:,]/g)];
        const keys = new Set();
        let depth = 0;
        for (let index = 0; index < tokens.length; index++) {
            const token = tokens[index][0];
            if (token === "{") check(++depth === 1, "approval");
            if (token === "}") depth--;
            check(token !== "[" && token !== "]", "approval");
            if (token.startsWith('"') && tokens[index + 1]?.[0] === ":") {
                const key = JSON.parse(token);
                check(!keys.has(key), "approval");
                keys.add(key);
            }
        }
        const value = JSON.parse(json);
        check(object(value) && Object.keys(value).sort().join(",") === [...approvalKeys].sort().join(","), "approval");
        return value;
    } catch {
        throw new ApplyError("approval");
    }
}

export async function applySettings(env, context) {
    return stage("settings", async () => {
        const settings = await validateSettings({
            tenantId: env.APPLY_TENANT_ID, subscriptionId: env.APPLY_SUBSCRIPTION_ID,
            clientId: env.APPLY_CLIENT_ID, resourceGroup: env.APPLY_RESOURCE_GROUP, parametersJson: env.APPLY_PARAMETERS,
        }, context);
        await validateSettings({
            tenantId: settings.tenantId, subscriptionId: settings.subscriptionId,
            clientId: env.APPLY_PLANNING_CLIENT_ID, resourceGroup: settings.resourceGroup, parametersJson: settings.parametersJson,
        }, context);
        check(!same(settings.clientId, env.APPLY_PLANNING_CLIENT_ID)
            && !same(settings.clientId, settings.parameters.workforceClientId), "settings");
        return { ...settings, planningClientId: env.APPLY_PLANNING_CLIENT_ID };
    });
}

/** Owner assertions plus native apply-environment review, not API or signature evidence. */
export function verifyApproval(approval, context, settings, now = Date.now()) {
    check(object(approval) && Object.keys(approval).sort().join(",") === [...approvalKeys].sort().join(","), "approval");
    check(approval.version === 1 && approval.purpose === "disabled-infrastructure-apply"
        && approval.applyRunId === String(context.runId) && approval.sourceSha === context.sourceSha
        && approval.applicationArtifact === null && approval.leastPrivilegeReviewed === true
        && approval.roleAssignmentsReviewed === true
        && same(approval.tenantId, settings.tenantId) && same(approval.subscriptionId, settings.subscriptionId)
        && approval.resourceGroup === settings.resourceGroup && same(approval.deploymentClientId, settings.clientId)
        && same(approval.planningClientId, settings.planningClientId)
        && same(approval.permissionScope, groupId(settings))
        && ["templateSha256", "parametersSha256", "whatIfSha256"].every(key => sha256(approval[key]))
        && typeof approval.reviewReference === "string" && approval.reviewReference.trim().length > 0
        && approval.reviewReference.length <= 256 && !/[\u0000-\u001f\u007f]/.test(approval.reviewReference), "approval");
    const reviewed = Date.parse(approval.reviewedAt);
    const expires = Date.parse(approval.expiresAt);
    check(typeof approval.reviewedAt === "string" && typeof approval.expiresAt === "string"
        && Number.isFinite(reviewed) && Number.isFinite(expires)
        && new Date(reviewed).toISOString() === approval.reviewedAt && new Date(expires).toISOString() === approval.expiresAt
        && reviewed <= now && expires > now && expires > reviewed && expires - reviewed <= 24 * 60 * 60 * 1000, "approval");
}

export async function readLocalBytes(path, limit = 8 * 1024 * 1024) {
    return stage("fingerprints", async () => {
        const file = await open(path, "r");
        try {
            check((await file.stat()).isFile(), "fingerprints");
            const buffer = Buffer.alloc(limit + 1);
            let used = 0;
            while (used < buffer.length) {
                const { bytesRead } = await file.read(buffer, used, buffer.length - used, null);
                if (bytesRead === 0) break;
                used += bytesRead;
            }
            check(used <= limit, "fingerprints");
            return buffer.subarray(0, used);
        } finally { await file.close(); }
    });
}

export async function executeApply(context, settings, approval, io) {
    const now = io.now ?? Date.now;
    verifyApproval(approval, context, settings, now());
    let directory;
    let loginAttempted = false;
    let applyAttempted = false;
    let failure;
    let result;
    try {
        directory = await stage("compiler", () => io.createDirectory());
        const template = join(directory, "template.json");
        const parameterFile = join(directory, "parameters.json");
        await stage("compiler", () => io.compile(directory, template));
        const templateBytes = await stage("fingerprints", () => io.read(template));
        const parameters = buildArmParameters(settings.parameters, { subscriptionId: settings.subscriptionId, tenantId: settings.tenantId });
        check(digest(templateBytes) === approval.templateSha256 && digest(canonicalJson(parameters)) === approval.parametersSha256, "fingerprints");
        const version = parseJson(await stage("tooling", () => io.run("az", ["version", "--output", "json"], "tooling", directory)), "tooling");
        check(object(version) && /^2\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$/.test(version["azure-cli"] ?? "")
            && Number(version["azure-cli"].split(".")[1]) >= 76, "tooling");
        const token = await stage("oidc", async () => validateOidcToken(await io.oidc(), context, Math.floor(now() / 1000), APPLY_WORKFLOW));
        await stage("oidc", () => io.mask(token));
        loginAttempted = true;
        await stage("login", () => io.run("az", [
            "login", "--service-principal", "--username", settings.clientId, "--tenant", settings.tenantId,
            "--federated-token", token, "--output", "none", "--only-show-errors",
        ], "login", directory));
        await stage("account", () => io.run("az", ["account", "set", "--subscription", settings.subscriptionId], "account", directory));
        const account = parseJson(await stage("account", () => io.run("az", [
            "account", "show", "--subscription", settings.subscriptionId, "--output", "json", "--only-show-errors",
        ], "account", directory)), "account");
        const boundParameters = await stage("account", () => bindAccount(account, settings));
        check(digest(canonicalJson(boundParameters)) === approval.parametersSha256, "fingerprints");
        const group = parseJson(await stage("resourceGroup", () => io.run("az", [
            "group", "show", "--name", settings.resourceGroup, "--subscription", settings.subscriptionId, "--output", "json", "--only-show-errors",
        ], "resourceGroup", directory)), "resourceGroup");
        check(object(group) && same(group.id, groupId(settings)) && group.name === settings.resourceGroup
            && group.properties?.provisioningState === "Succeeded", "resourceGroup");
        await stage("settings", () => io.write(parameterFile, JSON.stringify(boundParameters), 0o600));
        const args = [
            "--resource-group", settings.resourceGroup, "--subscription", settings.subscriptionId,
            "--name", `sidequest-apply-${context.runId}`, "--template-file", template, "--parameters", `@${parameterFile}`,
            "--mode", "Incremental", "--validation-level", "Provider", "--no-prompt", "true", "--output", "json", "--only-show-errors",
        ];
        const validation = parseJson(await stage("validate", () => io.run("az", ["deployment", "group", "validate", ...args], "validate", directory)), "validate", 8 * 1024 * 1024);
        check(validation?.error == null && validation?.properties?.provisioningState === "Succeeded"
            && (validation.properties.diagnostics == null || (Array.isArray(validation.properties.diagnostics) && validation.properties.diagnostics.length === 0)), "validate");
        const whatIf = parseJson(await stage("whatif", () => io.run("az", [
            "deployment", "group", "what-if", ...args, "--result-format", "FullResourcePayloads", "--no-pretty-print",
        ], "whatif", directory)), "whatif", 8 * 1024 * 1024);
        const fingerprints = await stage("whatif", () => reviewFingerprints(templateBytes, boundParameters, whatIf));
        check(fingerprints.whatIfSha256 === approval.whatIfSha256
            && whatIf.changes.every(change => change.resourceId.toLowerCase().startsWith(`${groupId(settings).toLowerCase()}/providers/`)), "whatif");
        // The same local bytes are used for review comparison and create. There is
        // no saved-plan/atomic-state guarantee in ARM; external changes remain possible.
        await stage("fingerprints", async () => {
            check(digest(await io.read(template)) === approval.templateSha256
                && digest(canonicalJson(parseJson((await io.read(parameterFile)).toString("utf8"), "settings", 32768))) === approval.parametersSha256, "fingerprints");
        });
        verifyApproval(approval, context, settings, now());
        applyAttempted = true;
        const applied = await stage("create", async () => parseJson(
            await io.run("az", ["deployment", "group", "create", ...args], "create", directory, undefined, 1800000),
            "create", 8 * 1024 * 1024,
        ));
        check(applied?.error == null && same(applied?.id, `${groupId(settings)}/providers/Microsoft.Resources/deployments/sidequest-apply-${context.runId}`)
            && applied?.properties?.provisioningState === "Succeeded"
            && same(applied.properties.outputs?.applicationIsEnabled?.type, "Bool")
            && applied.properties.outputs?.applicationIsEnabled?.value === false, "create");
        result = {
            sourceSha: context.sourceSha, environment: context.environmentName,
            deploymentName: `sidequest-apply-${context.runId}`, ...fingerprints,
            infrastructureProvisioning: "Succeeded", enableApplication: false, applicationArtifact: null,
            limitations: "Infrastructure only. No SQL bootstrap, migrations, application artifact deployment, activation or provider/device-data/recovery acceptance.",
        };
    } catch (error) {
        failure = new ApplyError(error instanceof ApplyError || error instanceof PlanError ? error.stage : "context", applyAttempted);
    } finally {
        if (directory) {
            try { if (loginAttempted) await io.run("az", ["account", "clear", "--only-show-errors"], "cleanup", directory); }
            catch { failure = new ApplyError("cleanup", applyAttempted); }
            try { await io.removeDirectory(directory); }
            catch { failure = new ApplyError("cleanup", applyAttempted); }
        }
    }
    if (failure) throw failure;
    return result;
}

export async function main(args, env = process.env, dependencies = {}) {
    const output = dependencies.output ?? (text => process.stdout.write(text));
    const read = dependencies.read ?? readLocalBytes;
    if (args.length === 1 && args[0] === "--help") {
        output("Disabled infrastructure apply helper: gate|apply; trusted GitHub apply context required.\n"
            + "Offline fingerprint utility: fingerprints TEMPLATE PARAMETERS FULL_WHAT_IF.\n"
            + "Fingerprints are not approval. No application artifact or activation is supported. See infra/deployment/APPLY.md.\n");
        return;
    }
    if (args[0] === "fingerprints") {
        check(args.length === 4 && args.slice(1).every(path => typeof path === "string" && path.length > 0 && path.length <= 4096), "usage");
        const hashes = await stage("fingerprints", async () => reviewFingerprints(
            await read(args[1]), parseJson((await read(args[2])).toString("utf8"), "fingerprints", 32768),
            parseJson((await read(args[3])).toString("utf8"), "fingerprints", 8 * 1024 * 1024),
        ));
        output(`${JSON.stringify(hashes, null, 2)}\n`);
        return;
    }
    check(args.length === 1 && ["gate", "apply"].includes(args[0]), "usage");
    const inputs = await (dependencies.readInputs ?? readWorkflowInputs)(env);
    const context = contextFromEnvironment({ ...env, PLAN_INPUTS: inputs }, APPLY_WORKFLOW);
    check(typeof env.GH_TOKEN === "string" && /^[A-Za-z0-9._~-]{20,16384}$/.test(env.GH_TOKEN), "metadata");
    const source = (await (dependencies.run ?? runCaptured)("git", ["rev-parse", "HEAD"], { stage: "source", timeout: 10000, limit: 1024 })).trim();
    const get = dependencies.get ?? (url => getJson(url, env.GH_TOKEN));
    const checked = await metadataGate(context, get, source, APPLY_WORKFLOW);
    const append = dependencies.append ?? appendFile;
    if (args[0] === "gate") {
        check(typeof env.GITHUB_OUTPUT === "string" && env.GITHUB_OUTPUT.length > 0, "output");
        await stage("output", () => append(env.GITHUB_OUTPUT, `environment=${checked.environmentName}\nenvironment-id=${checked.environmentId}\nsource-sha=${checked.sourceSha}\n`));
        return;
    }
    check(env.APPLY_EXPECTED_SOURCE_SHA === checked.sourceSha && env.APPLY_EXPECTED_ENVIRONMENT_ID === String(checked.environmentId), "environment");
    const settings = await applySettings(env, checked);
    const approval = parseApproval(env.APPLY_APPROVAL);
    const io = dependencies.io ?? { ...runtimeIo(env), read: readLocalBytes };
    const result = await executeApply(checked, settings, approval, io);
    const json = `${JSON.stringify(result, null, 2)}\n`;
    try {
        check(typeof env.GITHUB_STEP_SUMMARY === "string" && env.GITHUB_STEP_SUMMARY.length > 0, "output");
        await append(env.GITHUB_STEP_SUMMARY, `## Disabled infrastructure apply\n\n\`\`\`json\n${json}\`\`\`\n`);
        output(json);
    } catch { throw new ApplyError("output", true); }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    process.stdout.on("error", () => { process.exitCode = 1; process.stderr.write(`${new ApplyError("output", true).message}\n`); });
    process.stderr.on("error", () => { process.exitCode = 1; });
    try { await main(process.argv.slice(2)); } catch (error) {
        process.exitCode = 1;
        const safe = error instanceof ApplyError ? error : new ApplyError(error instanceof PlanError ? error.stage : "context");
        process.stderr.write(`${safe.message}\n`);
    }
}
