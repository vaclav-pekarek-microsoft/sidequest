import { createHash } from "node:crypto";
import { appendFile, chmod, mkdir, mkdtemp, open, rm, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { buildArmParameters, parseRequestJson } from "./parameter-validation.mjs";
import { atStage, getJson, parseJson, PlanError, readResponse, requirePlan, runCaptured } from "./plan-io.mjs";

export const REPOSITORY = "vaclav-pekarek-microsoft/sidequest";
export const WORKFLOW = ".github/workflows/infrastructure-plan.yml";
export const APPLY_WORKFLOW = ".github/workflows/infrastructure-apply.yml";
export const AUDIENCE = "api://AzureADTokenExchange";
export const BICEP_URL = "https://github.com/Azure/bicep/releases/download/v0.47.16/bicep-linux-x64";
export const BICEP_SHA256 = "64c345a58e0c3e48b1bc98a4e62d6b3adb1d238281297de3400aeafb2697aa5a";
const api = `https://api.github.com/repos/${REPOSITORY}`;
const shaPattern = /^[0-9a-f]{40}$/;
const positiveId = value => Number.isSafeInteger(value) && value > 0;
const decimalId = value => typeof value === "string" && /^[1-9][0-9]{0,15}$/.test(value) && positiveId(Number(value));
const object = value => value !== null && typeof value === "object" && !Array.isArray(value);
const user = value => object(value) && value.type === "User" && positiveId(value.id)
    && typeof value.login === "string" && /^[a-zA-Z0-9][a-zA-Z0-9-]{0,38}$/.test(value.login);
const sameGuid = (a, b) => typeof a === "string" && typeof b === "string" && a.toLowerCase() === b.toLowerCase();
const changeTypes = ["Create", "Delete", "Deploy", "Ignore", "Modify", "NoChange", "Unsupported"];

/** GitHub context and the two owner-controlled assertions are never dispatch inputs. */
export function contextFromEnvironment(env, workflow = WORKFLOW) {
    requirePlan([WORKFLOW, APPLY_WORKFLOW].includes(workflow), "context");
    const applying = workflow === APPLY_WORKFLOW;
    const inputs = parseJson(env.PLAN_INPUTS, "context", 1024);
    requirePlan(object(inputs) && Object.keys(inputs).length === 1
        && ["staging", "production"].includes(inputs.environment), "context");
    requirePlan(env.GITHUB_REPOSITORY === REPOSITORY && env.GITHUB_EVENT_NAME === "workflow_dispatch"
        && env.GITHUB_REF === "refs/heads/main" && env.GITHUB_RUN_ATTEMPT === "1"
        && shaPattern.test(env.GITHUB_SHA ?? "") && env.GITHUB_WORKFLOW_SHA === env.GITHUB_SHA
        && env.GITHUB_WORKFLOW_REF === `${REPOSITORY}/${workflow}@refs/heads/main`
        && decimalId(env.GITHUB_RUN_ID) && decimalId(env.GITHUB_REPOSITORY_ID), "context");
    requirePlan((applying ? env.APPLY_ENABLED : env.PLAN_ENABLED) === "true", "optin");
    requirePlan((applying ? env.APPLY_ADMIN_CONTROLS_VERIFIED : env.PLAN_ADMIN_CONTROLS_VERIFIED) === "true", "acknowledgement");
    return {
        sourceSha: env.GITHUB_SHA, runId: Number(env.GITHUB_RUN_ID),
        repositoryId: Number(env.GITHUB_REPOSITORY_ID),
        environment: inputs.environment, environmentName: `sidequest-${applying ? "apply-" : ""}${inputs.environment}`,
    };
}

function repositoryMatches(value, context) {
    return object(value) && value.full_name === REPOSITORY && value.id === context.repositoryId && value.fork === false;
}

export function verifyEnvironment(environment, policies, context) {
    requirePlan(object(environment) && positiveId(environment.id)
        && environment.name === context.environmentName
        && environment.url === `${api}/environments/${context.environmentName}`
        && typeof environment.node_id === "string" && environment.node_id.length > 0
        && typeof environment.created_at === "string" && Number.isFinite(Date.parse(environment.created_at))
        && typeof environment.updated_at === "string" && Number.isFinite(Date.parse(environment.updated_at)), "environment");
    const policy = environment.deployment_branch_policy;
    requirePlan(object(policy) && policy.protected_branches === false && policy.custom_branch_policies === true, "environment");
    requirePlan(object(policies) && policies.total_count === 1 && Array.isArray(policies.branch_policies)
        && policies.branch_policies.length === 1, "environment");
    const branch = policies.branch_policies[0];
    requirePlan(object(branch) && positiveId(branch.id) && branch.type === "branch" && branch.name === "main", "environment");
    const rules = environment.protection_rules;
    requirePlan(Array.isArray(rules) && rules.length >= 2 && rules.length <= 3, "environment");
    const seen = new Set();
    for (const rule of rules) {
        requirePlan(object(rule) && positiveId(rule.id) && typeof rule.node_id === "string" && rule.node_id.length > 0
            && ["required_reviewers", "branch_policy", "wait_timer"].includes(rule.type) && !seen.has(rule.type), "environment");
        seen.add(rule.type);
        if (rule.type === "required_reviewers") {
            requirePlan(rule.prevent_self_review === true && Array.isArray(rule.reviewers)
                && rule.reviewers.length >= 1 && rule.reviewers.length <= 6, "environment");
            const ids = new Set();
            for (const reviewer of rule.reviewers) {
                requirePlan(object(reviewer) && reviewer.type === "User" && user(reviewer.reviewer)
                    && !ids.has(reviewer.reviewer.id), "environment");
                ids.add(reviewer.reviewer.id);
            }
        } else if (rule.type === "wait_timer") {
            requirePlan(Number.isInteger(rule.wait_timer) && rule.wait_timer >= 0 && rule.wait_timer <= 43200, "environment");
        }
    }
    requirePlan(seen.has("required_reviewers") && seen.has("branch_policy"), "environment");
    return environment.id;
}

function verifyRun(run, context, path, workflowId, events) {
    requirePlan(object(run) && positiveId(run.id) && run.workflow_id === workflowId
        && run.path === path && run.head_sha === context.sourceSha && run.head_branch === "main"
        && events.includes(run.event) && positiveId(run.run_number) && positiveId(run.run_attempt)
        && repositoryMatches(run.repository, context) && repositoryMatches(run.head_repository, context), "source");
}

export function verifyCi(workflow, runs, context, filename) {
    const path = `.github/workflows/${filename}`;
    requirePlan(object(workflow) && positiveId(workflow.id) && workflow.path === path && workflow.state === "active", "source");
    requirePlan(object(runs) && Number.isInteger(runs.total_count) && runs.total_count > 0 && runs.total_count < 100
        && Array.isArray(runs.workflow_runs) && runs.workflow_runs.length === runs.total_count, "source");
    const ids = new Set();
    const numbers = new Set();
    for (const run of runs.workflow_runs) {
        verifyRun(run, context, path, workflow.id, filename === "ci.yml" ? ["push"] : ["push", "workflow_dispatch"]);
        requirePlan(!ids.has(run.id) && !numbers.has(run.run_number)
            && ["completed", "in_progress", "queued", "requested", "waiting", "pending"].includes(run.status)
            && (run.status === "completed"
                ? ["success", "failure", "neutral", "cancelled", "skipped", "timed_out", "action_required", "stale"].includes(run.conclusion)
                : run.conclusion === null), "source");
        ids.add(run.id);
        numbers.add(run.run_number);
    }
    const latest = [...runs.workflow_runs].sort((a, b) => b.run_number - a.run_number || b.run_attempt - a.run_attempt)[0];
    requirePlan(latest.status === "completed" && latest.conclusion === "success", "source");
}

/** All endpoints are fixed read-only routes. The caller cannot supply metadata URLs. */
export async function metadataGate(context, get, checkedOutSha, workflowPath = WORKFLOW) {
    requirePlan([WORKFLOW, APPLY_WORKFLOW].includes(workflowPath), "context");
    requirePlan(checkedOutSha === context.sourceSha, "source");
    const repository = await get(api);
    requirePlan(repositoryMatches(repository, context) && repository.default_branch === "main"
        && repository.archived === false && repository.disabled === false, "source");
    const head = await get(`${api}/git/ref/heads/main`);
    requirePlan(object(head) && head.ref === "refs/heads/main" && object(head.object)
        && head.object.type === "commit" && head.object.sha === context.sourceSha, "source");
    const workflow = await get(`${api}/actions/workflows/${workflowPath.split("/").at(-1)}`);
    requirePlan(object(workflow) && workflow.path === workflowPath && workflow.state === "active" && positiveId(workflow.id), "source");
    const run = await get(`${api}/actions/runs/${context.runId}`);
    verifyRun(run, context, workflowPath, workflow.id, ["workflow_dispatch"]);
    requirePlan(run.id === context.runId && run.run_attempt === 1 && run.status === "in_progress"
        && user(run.actor) && user(run.triggering_actor) && run.actor.id === run.triggering_actor.id, "source");
    for (const filename of ["ci.yml", "infrastructure.yml"]) {
        const ci = await get(`${api}/actions/workflows/${filename}`);
        const runs = await get(`${api}/actions/workflows/${filename}/runs?head_sha=${context.sourceSha}&branch=main&per_page=100&page=1`);
        verifyCi(ci, runs, context, filename);
    }
    const environment = await get(`${api}/environments/${context.environmentName}`);
    const policies = await get(`${api}/environments/${context.environmentName}/deployment-branch-policies?per_page=100&page=1`);
    return { ...context, environmentId: verifyEnvironment(environment, policies, context) };
}

export function validateSettings(settings, context) {
    return atStage("settings", () => {
        requirePlan(object(settings) && Object.keys(settings).sort().join(",")
            === "clientId,parametersJson,resourceGroup,subscriptionId,tenantId", "settings");
        // Restrict the command argument to a conservative documented ASCII subset
        // of Azure resource-group names; never interpret it as an option or path.
        requirePlan(typeof settings.resourceGroup === "string"
            && /^[A-Za-z0-9_()][A-Za-z0-9_.()-]{0,89}$/.test(settings.resourceGroup)
            && !settings.resourceGroup.endsWith("."), "settings");
        const accountContext = { subscriptionId: settings.subscriptionId, tenantId: settings.tenantId };
        const request = parseRequestJson(`{"parameters":${settings.parametersJson},"accountContext":${JSON.stringify(accountContext)}}`);
        requirePlan(request.parameters.environmentName === context.environment, "settings");
        buildArmParameters(request.parameters, accountContext);
        // Apply the existing GUID and synthetic-ID checks to the deployment client too.
        buildArmParameters({ ...request.parameters, workforceClientId: settings.clientId }, accountContext);
        return { ...settings, parameters: request.parameters };
    });
}

export function oidcRequest(url, token) {
    requirePlan(typeof url === "string" && url.length <= 8192, "oidc");
    let endpoint;
    try { endpoint = new URL(url); } catch { throw new PlanError("oidc"); }
    requirePlan(endpoint.protocol === "https:" && /^[a-z0-9-]+(?:\.[a-z0-9-]+)*\.actions\.githubusercontent\.com$/.test(endpoint.hostname)
        && !endpoint.username && !endpoint.password && !endpoint.hash && !endpoint.port
        && endpoint.pathname !== "/" && !endpoint.searchParams.has("audience")
        && new Set(endpoint.searchParams.keys()).size === [...endpoint.searchParams.keys()].length
        && typeof token === "string" && /^[A-Za-z0-9._~-]{20,16384}$/.test(token), "oidc");
    endpoint.searchParams.set("audience", AUDIENCE);
    return endpoint.href;
}

export function validateOidcToken(value, context, now = Math.floor(Date.now() / 1000), workflow = WORKFLOW) {
    requirePlan([WORKFLOW, APPLY_WORKFLOW].includes(workflow), "oidc");
    requirePlan(typeof value === "string" && value.length <= 32768 && /^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(value), "oidc");
    const claims = parseJson(Buffer.from(value.split(".")[1], "base64url").toString("utf8"), "oidc", 32768);
    requirePlan(object(claims) && claims.iss === "https://token.actions.githubusercontent.com" && claims.aud === AUDIENCE
        && claims.repository === REPOSITORY && claims.repository_id === String(context.repositoryId)
        && claims.ref === "refs/heads/main" && claims.ref_type === "branch" && claims.sha === context.sourceSha
        && claims.event_name === "workflow_dispatch" && claims.environment === context.environmentName
        && claims.run_id === String(context.runId) && claims.run_attempt === "1"
        && claims.workflow_ref === `${REPOSITORY}/${workflow}@refs/heads/main` && claims.workflow_sha === context.sourceSha
        && Number.isSafeInteger(claims.exp) && claims.exp > now
        && Number.isSafeInteger(claims.nbf) && claims.nbf <= now, "oidc");
    // Parsing is only defense in depth. Entra validates signature and federation.
    return value;
}

export function bindAccount(account, settings) {
    requirePlan(object(account) && sameGuid(account.id, settings.subscriptionId) && sameGuid(account.tenantId, settings.tenantId)
        && account.environmentName === "AzureCloud" && account.state === "Enabled" && account.isDefault === true
        && object(account.user) && account.user.type === "servicePrincipal" && sameGuid(account.user.name, settings.clientId), "account");
    return buildArmParameters(settings.parameters, { subscriptionId: account.id, tenantId: account.tenantId });
}

export function summarizeWhatIf(result, context) {
    requirePlan(object(result) && result.status === "Succeeded" && (result.error === null || result.error === undefined)
        && (result.diagnostics == null || (Array.isArray(result.diagnostics) && result.diagnostics.length === 0))
        && Array.isArray(result.changes) && result.changes.length <= 10000, "whatif");
    const counts = Object.fromEntries(changeTypes.map(type => [type, 0]));
    for (const change of result.changes) {
        requirePlan(object(change) && changeTypes.includes(change.changeType), "whatif");
        counts[change.changeType]++;
    }
    requirePlan(counts.Ignore === 0 && counts.Unsupported === 0, "whatif");
    return {
        sourceSha: context.sourceSha,
        environment: context.environmentName,
        enableApplication: false,
        changeTypeCounts: counts,
        limitations: [
            "Read-only planning, not deployment authorization or full change review.",
            "GitHub environment review relies on owner-verified administrator controls.",
            "No identity/workforce, private routing, runtime, recovery or load acceptance is established.",
        ],
    };
}

/** Runs only after native environment review and a repeated metadata/context gate. */
export async function executePlan(context, settings, io) {
    let directory;
    let loggedIn = false;
    let summary;
    let failure;
    try {
        directory = await atStage("compiler", () => io.createDirectory());
        const template = join(directory, "template.json");
        const parameterFile = join(directory, "parameters.json");
        await atStage("compiler", () => io.compile(directory, template));
        const version = parseJson(await atStage("tooling", () => io.run("az", ["version", "--output", "json"], "tooling", directory)), "tooling");
        requirePlan(object(version) && typeof version["azure-cli"] === "string"
            && /^2\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$/.test(version["azure-cli"])
            && Number(version["azure-cli"].split(".")[1]) >= 76, "tooling");
        const token = validateOidcToken(await atStage("oidc", () => io.oidc()), context);
        io.mask(token);
        // The isolated CLI profile is cleaned even when login fails midway.
        loggedIn = true;
        await atStage("login", () => io.run("az", [
            "login", "--service-principal", "--username", settings.clientId, "--tenant", settings.tenantId,
            "--federated-token", token, "--output", "none", "--only-show-errors",
        ], "login", directory));
        await atStage("account", () => io.run("az", ["account", "set", "--subscription", settings.subscriptionId], "account", directory));
        const account = parseJson(await atStage("account", () => io.run("az", [
            "account", "show", "--subscription", settings.subscriptionId, "--output", "json", "--only-show-errors",
        ], "account", directory)), "account");
        const parameters = bindAccount(account, settings);
        const group = parseJson(await atStage("resourceGroup", () => io.run("az", [
            "group", "show", "--name", settings.resourceGroup, "--subscription", settings.subscriptionId, "--output", "json", "--only-show-errors",
        ], "resourceGroup", directory)), "resourceGroup");
        const resourceId = `/subscriptions/${account.id}/resourceGroups/${settings.resourceGroup}`;
        requirePlan(object(group) && typeof group.id === "string" && group.id.toLowerCase() === resourceId.toLowerCase()
            && group.name === settings.resourceGroup && object(group.properties) && group.properties.provisioningState === "Succeeded", "resourceGroup");
        await atStage("settings", () => io.write(parameterFile, JSON.stringify(parameters), 0o600));
        const args = [
            "--resource-group", settings.resourceGroup, "--subscription", account.id,
            "--name", `sidequest-plan-${context.runId}`, "--template-file", template, "--parameters", `@${parameterFile}`,
            "--mode", "Incremental", "--validation-level", "ProviderNoRbac", "--no-prompt", "true", "--output", "json", "--only-show-errors",
        ];
        const validation = parseJson(await atStage("validate", () => io.run("az", ["deployment", "group", "validate", ...args], "validate", directory)), "validate", 8 * 1024 * 1024);
        requirePlan(object(validation) && (validation.error === null || validation.error === undefined)
            && object(validation.properties) && validation.properties.provisioningState === "Succeeded"
            && (validation.properties.diagnostics == null || (Array.isArray(validation.properties.diagnostics) && validation.properties.diagnostics.length === 0)), "validate");
        const result = parseJson(await atStage("whatif", () => io.run("az", [
            "deployment", "group", "what-if", ...args, "--result-format", "ResourceIdOnly", "--no-pretty-print",
        ], "whatif", directory)), "whatif", 8 * 1024 * 1024);
        summary = summarizeWhatIf(result, context);
    } catch (error) {
        failure = error instanceof PlanError ? error : new PlanError("context");
    } finally {
        if (directory) {
            try {
                if (loggedIn) await io.run("az", ["account", "clear", "--only-show-errors"], "cleanup", directory);
            } catch {
                failure = new PlanError("cleanup");
            }
            try { await io.removeDirectory(directory); } catch { failure = new PlanError("cleanup"); }
        }
    }
    if (failure) throw failure;
    return summary;
}

function childEnvironment(env, directory, template) {
    const result = Object.fromEntries(Object.entries(env).filter(([key]) => !/^(?:GH_|GITHUB_|ACTIONS_|PLAN_|APPLY_|AZURE_|SIDEQUEST_)/.test(key)));
    return {
        ...result, AZURE_CONFIG_DIR: join(directory, "azure"), AZURE_CORE_COLLECT_TELEMETRY: "false",
        AZURE_EXTENSION_USE_DYNAMIC_INSTALL: "no", AZURE_CORE_ONLY_SHOW_ERRORS: "true",
        ...(template ? { SIDEQUEST_ARM_TEMPLATE: template } : {}),
    };
}

export async function compileTemplate(directory, template, io) {
    const bytes = await io.compilerBytes();
    requirePlan(createHash("sha256").update(bytes).digest("hex") === BICEP_SHA256, "compiler");
    const compiler = join(directory, "bicep");
    await io.write(compiler, bytes, 0o700);
    await io.run(compiler, ["build", "infra/main.bicep", "--outfile", template], "compiler", directory);
    await io.run(process.execPath, ["--test", "infra/tests/template.test.mjs"], "compiler", directory, template);
}

export function runtimeIo(env, fetcher = fetch) {
    const cwd = process.cwd();
    const io = {
        createDirectory: async () => {
            const path = await mkdtemp(join(cwd, ".sidequest-plan-"));
            try {
                await chmod(path, 0o700);
                await mkdir(join(path, "azure"), { mode: 0o700 });
            } catch {
                await rm(path, { recursive: true, force: true });
                throw new PlanError("compiler");
            }
            return path;
        },
        removeDirectory: path => rm(path, { recursive: true, force: true }),
        write: (path, data, mode) => writeFile(path, data, { flag: "wx", mode }),
        compilerBytes: async () => readResponse(await fetcher(BICEP_URL, { signal: AbortSignal.timeout(120_000) }), "compiler", 128 * 1024 * 1024),
        run: (file, args, stage, directory, template, timeout = 300_000) => runCaptured(file, args, {
            stage, cwd, env: childEnvironment(env, directory, template), timeout,
        }),
        oidc: async () => {
            const url = oidcRequest(env.ACTIONS_ID_TOKEN_REQUEST_URL, env.ACTIONS_ID_TOKEN_REQUEST_TOKEN);
            const response = await fetcher(url, {
                method: "GET", redirect: "error", signal: AbortSignal.timeout(30_000),
                headers: { Accept: "application/json", Authorization: `Bearer ${env.ACTIONS_ID_TOKEN_REQUEST_TOKEN}` },
            });
            requirePlan(!response.headers.get("link"), "oidc");
            requirePlan(/^application\/json(?:;|$)/i.test(response.headers.get("content-type") ?? ""), "oidc");
            const bytes = await readResponse(response, "oidc", 65536);
            const result = parseJson(new TextDecoder("utf-8", { fatal: true }).decode(bytes), "oidc", 65536);
            requirePlan(object(result) && typeof result.value === "string", "oidc");
            return result.value;
        },
        mask: token => process.stdout.write(`::add-mask::${token}\n`),
    };
    io.compile = (directory, template) => compileTemplate(directory, template, io);
    return io;
}

export async function readWorkflowInputs(runtime) {
    return atStage("context", async () => {
        const file = await open(runtime.GITHUB_EVENT_PATH, "r");
        try {
            requirePlan((await file.stat()).size <= 2 * 1024 * 1024, "context");
            const event = parseJson(await file.readFile("utf8"), "context");
            requirePlan(object(event), "context");
            return JSON.stringify(event.inputs);
        } finally {
            await file.close();
        }
    });
}

export async function main(args, env = process.env, dependencies = {}) {
    const output = dependencies.output ?? (text => process.stdout.write(text));
    const append = dependencies.append ?? appendFile;
    requirePlan(args.length === 1 && ["gate", "plan", "--help"].includes(args[0]), "usage");
    if (args[0] === "--help") {
        output("Planning workflow helper: gate|plan. GitHub-hosted workflow context is required.\n"
            + "Native environment reviews plus an owner assertion of manually verified administrator controls.\n"
            + "No apply authorization. See infra/deployment/PLANNING.md; never supply credentials as arguments.\n");
        return;
    }
    const readInputs = dependencies.readInputs ?? readWorkflowInputs;
    const context = contextFromEnvironment({ ...env, PLAN_INPUTS: await readInputs(env) });
    requirePlan(typeof env.GH_TOKEN === "string" && /^[A-Za-z0-9._~-]{20,16384}$/.test(env.GH_TOKEN), "metadata");
    const get = dependencies.get ?? (url => getJson(url, env.GH_TOKEN));
    const run = dependencies.run ?? runCaptured;
    const source = (await run("git", ["rev-parse", "HEAD"], { stage: "source", timeout: 10_000, limit: 1024 })).trim();
    const checked = await metadataGate(context, get, source);
    if (args[0] === "gate") {
        requirePlan(typeof env.GITHUB_OUTPUT === "string" && env.GITHUB_OUTPUT.length > 0, "output");
        await atStage("output", () => append(env.GITHUB_OUTPUT,
            `environment=${checked.environmentName}\nenvironment-id=${checked.environmentId}\nsource-sha=${checked.sourceSha}\n`));
        return;
    }
    requirePlan(env.PLAN_EXPECTED_SOURCE_SHA === checked.sourceSha
        && env.PLAN_EXPECTED_ENVIRONMENT_ID === String(checked.environmentId), "environment");
    const settings = await validateSettings({
        tenantId: env.PLAN_TENANT_ID, subscriptionId: env.PLAN_SUBSCRIPTION_ID,
        clientId: env.PLAN_CLIENT_ID, resourceGroup: env.PLAN_RESOURCE_GROUP,
        parametersJson: env.PLAN_PARAMETERS,
    }, checked);
    const summary = await executePlan(checked, settings, dependencies.io ?? runtimeIo(env));
    const json = `${JSON.stringify(summary, null, 2)}\n`;
    requirePlan(typeof env.GITHUB_STEP_SUMMARY === "string" && env.GITHUB_STEP_SUMMARY.length > 0, "output");
    await atStage("output", () => append(env.GITHUB_STEP_SUMMARY, `## Read-only infrastructure planning\n\n\`\`\`json\n${json}\`\`\`\n`));
    output(json);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    process.stdout.on("error", () => {
        process.exitCode = 1;
        process.stderr.write(`${new PlanError("output").message}\n`);
    });
    process.stderr.on("error", () => { process.exitCode = 1; });
    try {
        await main(process.argv.slice(2));
    } catch (error) {
        process.exitCode = 1;
        const safe = error instanceof PlanError ? error : new PlanError("context");
        process.stderr.write(`${safe.message}\n`);
    }
}
