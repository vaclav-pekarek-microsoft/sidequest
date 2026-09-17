import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import {
    ApplyError, applySettings, canonicalJson, executeApply, main, parseApproval, readLocalBytes, reviewFingerprints, verifyApproval,
} from "../apply-workflow.mjs";
import {
    APPLY_WORKFLOW, AUDIENCE, contextFromEnvironment, metadataGate, REPOSITORY, runtimeIo, validateOidcToken, WORKFLOW,
} from "../plan-workflow.mjs";
import { buildArmParameters } from "../parameter-validation.mjs";
import { PlanError } from "../plan-io.mjs";

const sourceSha = "9fbe907ba57c86e543947b6702ed9c414fd020cd";
const now = Date.parse("2026-09-16T09:10:00.000Z");
const tenant = "8ecdae60-49c0-4e23-91ab-5ae219764fea";
const subscription = "fe9367f6-38b1-4503-9f9b-1cf4f82e61d7";
const deploymentClient = "c6dc0937-0afb-4dc1-b629-21e4e01be3dd";
const planningClient = "621715c8-b2a0-4b0d-bb1a-a2500733b4f9";
const scope = `/subscriptions/${subscription}/resourceGroups/fixture-group`;
const sha = value => createHash("sha256").update(value).digest("hex");
const clone = value => structuredClone(value);
const template = Buffer.from('{"fixture":"compiled-template-with-contract-checked-by-fake-compiler"}\n');

function errorStage(expected, changed = undefined) {
    return error => {
        assert.ok(error instanceof ApplyError || error instanceof PlanError);
        assert.equal(error.stage, expected);
        assert.equal(error.message.includes("SECRET"), false);
        if (changed !== undefined) assert.equal(error.mayHaveChanged, changed);
        return true;
    };
}

function environment(overrides = {}) {
    return {
        GITHUB_REPOSITORY: REPOSITORY, GITHUB_REPOSITORY_ID: "123", GITHUB_EVENT_NAME: "workflow_dispatch",
        GITHUB_REF: "refs/heads/main", GITHUB_RUN_ATTEMPT: "1", GITHUB_RUN_ID: "456",
        GITHUB_SHA: sourceSha, GITHUB_WORKFLOW_SHA: sourceSha,
        GITHUB_WORKFLOW_REF: `${REPOSITORY}/${APPLY_WORKFLOW}@refs/heads/main`,
        PLAN_INPUTS: '{"environment":"staging"}', APPLY_ENABLED: "true", APPLY_ADMIN_CONTROLS_VERIFIED: "true",
        APPLY_TENANT_ID: tenant, APPLY_SUBSCRIPTION_ID: subscription, APPLY_CLIENT_ID: deploymentClient,
        APPLY_PLANNING_CLIENT_ID: planningClient, APPLY_RESOURCE_GROUP: "fixture-group",
        APPLY_PARAMETERS: JSON.stringify({
            location: "fixture-region", environmentName: "staging", operationalOwner: "Fixture owner",
            virtualNetworkAddressPrefix: "192.0.2.0/24", applicationSubnetAddressPrefix: "192.0.2.0/26",
            privateEndpointSubnetAddressPrefix: "192.0.2.64/26", workforceTenantId: tenant,
            workforceClientId: "a1b91109-bd9c-436d-a517-aac3e6ec7328", workforceRole: "Fixture.Workforce",
            bootstrapAdministratorObjectId: "35ba91b7-b5b6-442b-9398-95d025e43ca6",
            sqlAdministratorGroupName: "Fixture administrators", sqlAdministratorGroupObjectId: "cca0fa8b-4110-44c5-9d4f-7dd6b51e4ff2",
            blobRestoreDays: 7, sqlPointInTimeRetentionDays: 14, logRetentionDays: 30,
        }),
        GH_TOKEN: "synthetic-token-not-a-real-credential", GITHUB_OUTPUT: "virtual-output", GITHUB_STEP_SUMMARY: "virtual-summary",
        APPLY_EXPECTED_SOURCE_SHA: sourceSha, APPLY_EXPECTED_ENVIRONMENT_ID: "77", ...overrides,
    };
}
const context = contextFromEnvironment(environment(), APPLY_WORKFLOW);
function fullWhatIf() {
    return {
        status: "Succeeded", changes: [
            { resourceId: `${scope}/providers/Microsoft.Web/sites/fixture-site`, changeType: "Create", after: { properties: { enabled: false, privateValue: "SECRET" } } },
            { resourceId: `${scope}/providers/Microsoft.Network/virtualNetworks/fixture-network`, changeType: "Modify",
                before: { properties: { privateValue: "SECRET before" } }, after: { properties: { privateValue: "SECRET after" } } },
        ],
    };
}
async function fixture() {
    const env = environment();
    const settings = await applySettings(env, context);
    const parameters = buildArmParameters(settings.parameters, { subscriptionId: subscription, tenantId: tenant });
    const approval = {
        version: 1, purpose: "disabled-infrastructure-apply", applyRunId: "456", sourceSha,
        ...reviewFingerprints(template, parameters, fullWhatIf()), applicationArtifact: null,
        tenantId: tenant, subscriptionId: subscription, resourceGroup: "fixture-group",
        deploymentClientId: deploymentClient, planningClientId: planningClient, permissionScope: scope,
        leastPrivilegeReviewed: true, roleAssignmentsReviewed: true, reviewReference: "private-review-fixture",
        reviewedAt: "2026-09-16T09:00:00.000Z", expiresAt: "2026-09-16T09:30:00.000Z",
    };
    return { env: { ...env, APPLY_APPROVAL: JSON.stringify(approval) }, settings, parameters, approval };
}

function metadataFixture() {
    const api = `https://api.github.com/repos/${REPOSITORY}`;
    const repo = { id: 123, full_name: REPOSITORY, fork: false, default_branch: "main", archived: false, disabled: false };
    const user = { id: 99, type: "User", login: "fixture-owner" };
    const workflow = (name, id) => ({ id, path: `.github/workflows/${name}`, state: "active" });
    const run = (name, id) => ({
        id: 456, workflow_id: id, path: `.github/workflows/${name}`, head_sha: sourceSha, head_branch: "main",
        event: "push", run_number: 17, run_attempt: 1, status: "completed", conclusion: "success",
        repository: repo, head_repository: repo, actor: user, triggering_actor: user,
    });
    const data = new Map([
        [api, repo], [`${api}/git/ref/heads/main`, { ref: "refs/heads/main", object: { type: "commit", sha: sourceSha } }],
        [`${api}/actions/workflows/infrastructure-apply.yml`, workflow("infrastructure-apply.yml", 30)],
        [`${api}/actions/runs/456`, { ...run("infrastructure-apply.yml", 30), event: "workflow_dispatch", status: "in_progress", conclusion: null }],
        [`${api}/environments/sidequest-apply-staging`, {
            id: 77, node_id: "fixture-env", name: "sidequest-apply-staging", url: `${api}/environments/sidequest-apply-staging`,
            created_at: "2026-09-01T00:00:00Z", updated_at: "2026-09-01T00:00:00Z",
            deployment_branch_policy: { protected_branches: false, custom_branch_policies: true },
            protection_rules: [
                { id: 1, node_id: "reviews", type: "required_reviewers", prevent_self_review: true, reviewers: [{ type: "User", reviewer: user }] },
                { id: 2, node_id: "branch", type: "branch_policy" },
            ],
        }],
        [`${api}/environments/sidequest-apply-staging/deployment-branch-policies?per_page=100&page=1`,
            { total_count: 1, branch_policies: [{ id: 9, name: "main", type: "branch" }] }],
    ]);
    for (const [filename, id] of [["ci.yml", 10], ["infrastructure.yml", 20]]) {
        data.set(`${api}/actions/workflows/${filename}`, workflow(filename, id));
        data.set(`${api}/actions/workflows/${filename}/runs?head_sha=${sourceSha}&branch=main&per_page=100&page=1`,
            { total_count: 1, workflow_runs: [run(filename, id)] });
    }
    return { data, get: async url => { assert.ok(data.has(url)); return clone(data.get(url)); } };
}

function jwt(path = APPLY_WORKFLOW, name = context.environmentName) {
    const claims = {
        iss: "https://token.actions.githubusercontent.com", aud: AUDIENCE, repository: REPOSITORY, repository_id: "123",
        ref: "refs/heads/main", ref_type: "branch", sha: sourceSha, event_name: "workflow_dispatch",
        environment: name, run_id: "456", run_attempt: "1",
        workflow_ref: `${REPOSITORY}/${path}@refs/heads/main`, workflow_sha: sourceSha,
        exp: Math.floor(now / 1000) + 300, nbf: Math.floor(now / 1000) - 30,
    };
    return `${Buffer.from('{"alg":"RS256"}').toString("base64url")}.${Buffer.from(JSON.stringify(claims)).toString("base64url")}.synthetic-signature`;
}
function fakeIo() {
    const directory = join(fileURLToPath(new URL(".", import.meta.url)), "virtual-owned");
    const files = new Map([[join(directory, "template.json"), template]]);
    const events = [];
    const io = {
        now: () => now,
        createDirectory: async () => { events.push(["directory"]); return directory; },
        compile: async () => { events.push(["compile"]); },
        read: async path => { assert.ok(files.has(path)); return files.get(path); },
        write: async (path, value, mode) => { assert.equal(mode, 0o600); files.set(path, Buffer.from(value)); events.push(["write"]); },
        oidc: async () => { events.push(["oidc"]); return jwt(); },
        mask: token => { assert.equal(token, jwt()); events.push(["mask"]); },
        removeDirectory: async path => { assert.equal(path, directory); events.push(["remove"]); },
        run: async (file, args, stage, dir, templatePath, timeout) => {
            assert.equal(file, "az");
            assert.equal(dir, directory);
            events.push(["run", args, stage, timeout]);
            if (stage === "tooling") return '{"azure-cli":"2.76.0"}';
            if (stage === "account" && args[1] === "show") return JSON.stringify({
                id: subscription, tenantId: tenant, user: { type: "servicePrincipal", name: deploymentClient },
                state: "Enabled", isDefault: true, environmentName: "AzureCloud",
            });
            if (stage === "resourceGroup") return JSON.stringify({ id: scope, name: "fixture-group", properties: { provisioningState: "Succeeded" } });
            if (stage === "validate") return '{"properties":{"provisioningState":"Succeeded"},"error":null}';
            if (stage === "whatif") return JSON.stringify(fullWhatIf());
            if (stage === "create") return JSON.stringify({
                id: `${scope}/providers/Microsoft.Resources/deployments/sidequest-apply-456`,
                properties: { provisioningState: "Succeeded", outputs: { applicationIsEnabled: { type: "Bool", value: false } } },
            });
            return "";
        },
    };
    return { io, events, files, directory };
}

test("apply opt-in and D39 acknowledgement are separate from planning and default deny", () => {
    for (const value of [undefined, "", false, "false", "TRUE", " true", "SECRET"]) {
        assert.throws(() => contextFromEnvironment(environment({ APPLY_ENABLED: value, PLAN_ENABLED: "true" }), APPLY_WORKFLOW), errorStage("optin"));
        assert.throws(() => contextFromEnvironment(environment({ APPLY_ADMIN_CONTROLS_VERIFIED: value, PLAN_ADMIN_CONTROLS_VERIFIED: "true" }), APPLY_WORKFLOW), errorStage("acknowledgement"));
    }
    assert.equal(context.environmentName, "sidequest-apply-staging");
    assert.equal(contextFromEnvironment(environment({ PLAN_INPUTS: '{"environment":"production"}' }), APPLY_WORKFLOW).environmentName, "sidequest-apply-production");
    assert.throws(() => contextFromEnvironment(environment()), errorStage("context"));
    assert.throws(() => contextFromEnvironment(environment(), "SECRET"), errorStage("context"));
});

test("apply reuses exact main first-attempt repository CI and protected environment gates", async () => {
    const metadata = metadataFixture();
    assert.deepEqual(await metadataGate(context, metadata.get, sourceSha, APPLY_WORKFLOW), { ...context, environmentId: 77 });
    for (const override of [
        { GITHUB_REF: "refs/tags/main" }, { GITHUB_RUN_ATTEMPT: "2" }, { GITHUB_REPOSITORY: "other/repo" },
        { GITHUB_EVENT_NAME: "push" }, { GITHUB_WORKFLOW_REF: `${REPOSITORY}/${WORKFLOW}@refs/heads/main` },
        { GITHUB_WORKFLOW_SHA: "b".repeat(40) }, { PLAN_INPUTS: '{"environment":"staging","enableApplication":true}' },
    ]) assert.throws(() => contextFromEnvironment(environment(override), APPLY_WORKFLOW), errorStage("context"));
    await assert.rejects(metadataGate(context, metadata.get, "b".repeat(40), APPLY_WORKFLOW), errorStage("source"));
    const api = `https://api.github.com/repos/${REPOSITORY}`;
    for (const name of ["ci.yml", "infrastructure.yml"]) {
        const failedCi = metadataFixture();
        failedCi.data.get(`${api}/actions/workflows/${name}/runs?head_sha=${sourceSha}&branch=main&per_page=100&page=1`).workflow_runs[0].conclusion = "failure";
        await assert.rejects(metadataGate(context, failedCi.get, sourceSha, APPLY_WORKFLOW), errorStage("source"));
    }
    const missing = metadataFixture();
    missing.data.set(`${api}/environments/sidequest-apply-staging`, null);
    await assert.rejects(metadataGate(context, missing.get, sourceSha, APPLY_WORKFLOW), errorStage("environment"));
});

test("apply settings deny Reader identity reuse workload-client reuse activation and malformed scope", async () => {
    const { env } = await fixture();
    for (const override of [
        { APPLY_CLIENT_ID: planningClient }, { APPLY_CLIENT_ID: JSON.parse(env.APPLY_PARAMETERS).workforceClientId },
        { APPLY_CLIENT_ID: "" }, { APPLY_PLANNING_CLIENT_ID: "SECRET" }, { APPLY_RESOURCE_GROUP: "--subscription=SECRET" },
        { APPLY_PARAMETERS: JSON.stringify({ ...JSON.parse(env.APPLY_PARAMETERS), enableApplication: true }) },
        { APPLY_PARAMETERS: JSON.stringify({ ...JSON.parse(env.APPLY_PARAMETERS), environmentName: "production" }) },
    ]) await assert.rejects(applySettings({ ...env, ...override }, context), errorStage("settings"));
});

test("owner approval is exact run source identities scope privileges and no application artifact", async () => {
    const { approval, settings } = await fixture();
    assert.doesNotThrow(() => verifyApproval(approval, context, settings, now));
    for (const [key, value] of [
        ["version", 2], ["purpose", "read-only-planning"], ["applyRunId", "455"], ["sourceSha", "b".repeat(40)],
        ["applicationArtifact", "SECRET.zip"], ["leastPrivilegeReviewed", false], ["roleAssignmentsReviewed", false],
        ["tenantId", planningClient], ["subscriptionId", planningClient], ["resourceGroup", "other-group"],
        ["deploymentClientId", planningClient], ["planningClientId", deploymentClient], ["permissionScope", `/subscriptions/${subscription}`],
        ["templateSha256", "SECRET"], ["parametersSha256", "0".repeat(63)], ["whatIfSha256", "A".repeat(64)],
        ["reviewReference", ""], ["reviewReference", "SECRET\ninjection"],
    ]) assert.throws(() => verifyApproval({ ...approval, [key]: value }, context, settings, now), errorStage("approval"));
    for (const key of Object.keys(approval)) {
        const incomplete = { ...approval };
        delete incomplete[key];
        assert.throws(() => verifyApproval(incomplete, context, settings, now), errorStage("approval"));
    }
    assert.throws(() => verifyApproval({ ...approval, applicationPackage: "SECRET" }, context, settings, now), errorStage("approval"));
});

test("approval validity has exact current-time and maximum 24-hour boundaries", async () => {
    const { approval, settings } = await fixture();
    const start = Date.parse(approval.reviewedAt);
    const end = Date.parse(approval.expiresAt);
    assert.doesNotThrow(() => verifyApproval(approval, context, settings, start));
    assert.doesNotThrow(() => verifyApproval(approval, context, settings, end - 1));
    assert.throws(() => verifyApproval(approval, context, settings, start - 1), errorStage("approval"));
    assert.throws(() => verifyApproval(approval, context, settings, end), errorStage("approval"));
    const fullDay = { ...approval, expiresAt: new Date(start + 86400000).toISOString() };
    assert.doesNotThrow(() => verifyApproval(fullDay, context, settings, now));
    assert.throws(() => verifyApproval({ ...fullDay, expiresAt: new Date(start + 86400001).toISOString() }, context, settings, now), errorStage("approval"));
    assert.throws(() => verifyApproval({ ...approval, expiresAt: approval.reviewedAt }, context, settings, now), errorStage("approval"));
    assert.throws(() => verifyApproval({ ...approval, reviewedAt: "2026-09-16" }, context, settings, now), errorStage("approval"));
});

test("approval parser rejects unknown duplicate escaped duplicate nested and oversized input without echo", async () => {
    const { approval } = await fixture();
    assert.deepEqual(parseApproval(JSON.stringify(approval)), approval);
    for (const value of [
        "SECRET", "null", "[]", "{}", JSON.stringify({ ...approval, SECRET: "SECRET" }),
        JSON.stringify({ ...approval, applicationArtifact: { sha256: "SECRET" } }),
        JSON.stringify(approval).replace('"version":1', '"version":1,"version":1'),
        JSON.stringify(approval).replace('"version":1', '"version":1,"ver\\u0073ion":1'),
        " ".repeat(16385),
    ]) assert.throws(() => parseApproval(value), errorStage("approval"));
});

test("fingerprints preserve all content and array order but ignore object-key ordering", () => {
    const sample = { b: [1, true, null], a: { z: "SECRET", n: 0 } };
    assert.equal(canonicalJson(sample), '{"a":{"n":0,"z":"SECRET"},"b":[1,true,null]}');
    assert.equal(canonicalJson({ a: { n: 0, z: "SECRET" }, b: sample.b }), canonicalJson(sample));
    assert.notEqual(canonicalJson({ ...sample, b: [...sample.b].reverse() }), canonicalJson(sample));
    const parameters = { parameters: { enableApplication: { value: false } } };
    const changes = { status: "Succeeded", changes: [] };
    assert.deepEqual(reviewFingerprints(template, parameters, changes), {
        templateSha256: sha(template),
        parametersSha256: sha('{"parameters":{"enableApplication":{"value":false}}}'),
        whatIfSha256: sha('{"changes":[],"status":"Succeeded"}'),
    });
    assert.throws(() => canonicalJson({ value: undefined }), errorStage("fingerprints"));
    assert.throws(() => canonicalJson({ value: Infinity }), errorStage("fingerprints"));
    const circular = {}; circular.value = circular;
    assert.throws(() => canonicalJson(circular), errorStage("fingerprints"));
    let reads = 0;
    assert.throws(() => canonicalJson({ get SECRET() { reads++; return "SECRET"; } }), errorStage("fingerprints"));
    assert.equal(reads, 0);
    assert.throws(() => canonicalJson(Array(1)), errorStage("fingerprints"));
    const array = [1];
    Object.defineProperty(array, "0", { get() { reads++; return 1; } });
    assert.throws(() => canonicalJson(array), errorStage("fingerprints"));
    assert.equal(reads, 0);
});

test("full review rejects ResourceIdOnly unresolved missing payload duplicate and partial evidence", async () => {
    const { parameters } = await fixture();
    for (const change of [
        { resourceId: `${scope}/providers/type/name`, changeType: "Deploy" },
        { resourceId: `${scope}/providers/type/name`, changeType: "Create" },
        { resourceId: `${scope}/providers/type/name`, changeType: "Modify", after: {} },
        { resourceId: `${scope}/providers/type/name`, changeType: "Delete" },
        { resourceId: "SECRET", changeType: "NoChange" },
    ]) assert.throws(() => reviewFingerprints(template, parameters, { status: "Succeeded", changes: [change] }), errorStage("fingerprints"));
    for (const type of ["Ignore", "Unsupported", "SECRET"]) {
        assert.throws(() => reviewFingerprints(template, parameters, { status: "Succeeded", changes: [{ changeType: type }] }), errorStage("whatif"));
    }
    assert.throws(() => reviewFingerprints(template, parameters, { status: "Succeeded", changes: [fullWhatIf().changes[0], fullWhatIf().changes[0]] }), errorStage("fingerprints"));
    assert.throws(() => reviewFingerprints(template, parameters, { status: "Succeeded" }), errorStage("whatif"));
});

test("planning OIDC cannot substitute for apply workflow and apply environment federation", () => {
    assert.equal(validateOidcToken(jwt(), context, Math.floor(now / 1000), APPLY_WORKFLOW), jwt());
    assert.throws(() => validateOidcToken(jwt(WORKFLOW), context, Math.floor(now / 1000), APPLY_WORKFLOW), errorStage("oidc"));
    assert.throws(() => validateOidcToken(jwt(APPLY_WORKFLOW, "sidequest-staging"), context, Math.floor(now / 1000), APPLY_WORKFLOW), errorStage("oidc"));
    assert.throws(() => validateOidcToken(jwt(), context, Math.floor(now / 1000)), errorStage("oidc"));
});

test("apply executes one exact Provider-validated create using reviewed bytes and disabled parameters", async () => {
    const { settings, approval, parameters } = await fixture();
    const fake = fakeIo();
    const result = await executeApply(context, settings, approval, fake.io);
    const commands = fake.events.filter(event => event[0] === "run");
    const common = [
        "--resource-group", "fixture-group", "--subscription", subscription, "--name", "sidequest-apply-456",
        "--template-file", join(fake.directory, "template.json"), "--parameters", `@${join(fake.directory, "parameters.json")}`,
        "--mode", "Incremental", "--validation-level", "Provider", "--no-prompt", "true", "--output", "json", "--only-show-errors",
    ];
    assert.deepEqual(commands.map(event => event[1]), [
        ["version", "--output", "json"],
        ["login", "--service-principal", "--username", deploymentClient, "--tenant", tenant, "--federated-token", jwt(), "--output", "none", "--only-show-errors"],
        ["account", "set", "--subscription", subscription],
        ["account", "show", "--subscription", subscription, "--output", "json", "--only-show-errors"],
        ["group", "show", "--name", "fixture-group", "--subscription", subscription, "--output", "json", "--only-show-errors"],
        ["deployment", "group", "validate", ...common],
        ["deployment", "group", "what-if", ...common, "--result-format", "FullResourcePayloads", "--no-pretty-print"],
        ["deployment", "group", "create", ...common],
        ["account", "clear", "--only-show-errors"],
    ]);
    assert.equal(commands.find(event => event[2] === "create")[3], 1800000);
    assert.deepEqual(JSON.parse(fake.files.get(join(fake.directory, "parameters.json"))), parameters);
    assert.deepEqual(result, {
        sourceSha, environment: "sidequest-apply-staging", deploymentName: "sidequest-apply-456",
        ...reviewFingerprints(template, parameters, fullWhatIf()), infrastructureProvisioning: "Succeeded",
        enableApplication: false, applicationArtifact: null,
        limitations: "Infrastructure only. No SQL bootstrap, migrations, application artifact deployment, activation or provider/device-data/recovery acceptance.",
    });
    assert.equal(JSON.stringify(result).includes("SECRET"), false);
    assert.equal(JSON.stringify(result).includes(jwt()), false);
    assert.deepEqual(fake.events.at(-1), ["remove"]);
});

test("unapproved template or parameter fingerprints stop before any OIDC or Azure command", async () => {
    const { settings, approval } = await fixture();
    for (const key of ["templateSha256", "parametersSha256"]) {
        const fake = fakeIo();
        await assert.rejects(executeApply(context, settings, { ...approval, [key]: "0".repeat(64) }, fake.io), errorStage("fingerprints", false));
        assert.equal(fake.events.some(event => event[0] === "oidc" || event[0] === "run"), false);
        assert.deepEqual(fake.events.at(-1), ["remove"]);
    }
    const fake = fakeIo();
    await assert.rejects(executeApply(context, settings, { ...approval, applicationArtifact: "SECRET.zip" }, fake.io), errorStage("approval", false));
    assert.deepEqual(fake.events, []);
});

test("changed private what-if or expiry during validation never reaches create", async () => {
    const { settings, approval } = await fixture();
    for (const mode of ["hash", "scope", "expiry"]) {
        const fake = fakeIo();
        const run = fake.io.run;
        let current = now;
        fake.io.now = () => current;
        fake.io.run = async (...args) => {
            const value = await run(...args);
            if (args[2] === "whatif") {
                if (mode === "expiry") current = Date.parse(approval.expiresAt);
                if (mode === "hash") {
                    const changed = fullWhatIf(); changed.changes[0].after.properties.privateValue = "changed SECRET";
                    return JSON.stringify(changed);
                }
                if (mode === "scope") {
                    const changed = fullWhatIf(); changed.changes[0].resourceId = changed.changes[0].resourceId.replace("fixture-group", "other-group");
                    return JSON.stringify(changed);
                }
            }
            return value;
        };
        await assert.rejects(executeApply(context, settings, approval, fake.io), errorStage(mode === "expiry" ? "approval" : "whatif", false));
        assert.equal(fake.events.some(event => event[2] === "create"), false);
        assert.deepEqual(fake.events.at(-1), ["remove"]);
    }
});

test("approval expiring during final file verification never reaches create", async () => {
    const { settings, approval } = await fixture();
    const fake = fakeIo();
    const read = fake.io.read;
    let current = now;
    let finalParametersRead = false;
    fake.io.now = () => current;
    fake.io.read = async path => {
        const bytes = await read(path);
        if (path === join(fake.directory, "parameters.json")) {
            finalParametersRead = true;
            current = Date.parse(approval.expiresAt);
        }
        return bytes;
    };

    await assert.rejects(executeApply(context, settings, approval, fake.io), errorStage("approval", false));
    assert.equal(finalParametersRead, true);
    assert.equal(fake.events.some(event => event[2] === "create"), false);
    assert.deepEqual(fake.events.at(-1), ["remove"]);
});

test("authenticated account mismatch and changed local bytes deny create", async () => {
    const { settings, approval } = await fixture();
    for (const mode of ["account", "template", "parameters"]) {
        const fake = fakeIo();
        const run = fake.io.run;
        fake.io.run = async (...args) => {
            const value = await run(...args);
            if (mode === "account" && args[2] === "account" && args[1][1] === "show") {
                return JSON.stringify({ ...JSON.parse(value), tenantId: planningClient });
            }
            if (args[2] === "whatif") {
                if (mode === "template") fake.files.set(join(fake.directory, "template.json"), Buffer.from("SECRET"));
                if (mode === "parameters") {
                    const doc = JSON.parse(fake.files.get(join(fake.directory, "parameters.json")));
                    doc.parameters.enableApplication.value = true;
                    fake.files.set(join(fake.directory, "parameters.json"), Buffer.from(JSON.stringify(doc)));
                }
            }
            return value;
        };
        await assert.rejects(executeApply(context, settings, approval, fake.io), errorStage(mode === "account" ? "account" : "fingerprints", false));
        assert.equal(fake.events.some(event => event[2] === "create"), false);
    }
});

test("every command failure is redacted stage-specific and cleaned without automatic retry", async () => {
    const { settings, approval } = await fixture();
    for (const failing of ["tooling", "login", "account", "resourceGroup", "validate", "whatif", "create", "cleanup"]) {
        const fake = fakeIo(); const run = fake.io.run;
        let attempts = 0;
        fake.io.run = async (...args) => {
            if (args[2] === failing) { attempts++; throw new Error("SECRET provider failure"); }
            return run(...args);
        };
        await assert.rejects(executeApply(context, settings, approval, fake.io), errorStage(failing, ["create", "cleanup"].includes(failing)));
        assert.equal(attempts, 1);
        assert.deepEqual(fake.events.at(-1), ["remove"]);
    }
});

test("compiler filesystem OIDC and masking failures stop before unsafe continuation", async () => {
    const { settings, approval } = await fixture();
    for (const [boundary, expected] of [
        ["createDirectory", "compiler"], ["compile", "compiler"], ["read", "fingerprints"],
        ["oidc", "oidc"], ["mask", "oidc"], ["write", "settings"],
    ]) {
        const fake = fakeIo();
        fake.io[boundary] = async () => { throw new Error("SECRET boundary"); };
        await assert.rejects(executeApply(context, settings, approval, fake.io), errorStage(expected, false));
        assert.equal(fake.events.some(event => event[2] === "create"), false);
        if (boundary !== "createDirectory") assert.deepEqual(fake.events.at(-1), ["remove"]);
        if (boundary === "mask") assert.equal(fake.events.some(event => event[2] === "login"), false);
    }
});

test("apply rejects unsupported tools wrong resource group and incomplete ARM validation", async () => {
    const { settings, approval } = await fixture();
    for (const [boundary, responses] of [
        ["tooling", [{ "azure-cli": "2.75.9" }, { "azure-cli": "3.0.0" }, { "azure-cli": "SECRET" }, {}]],
        ["resourceGroup", [{ id: `${scope}OTHER`, name: "fixture-group", properties: { provisioningState: "Succeeded" } }, {}]],
        ["validate", [{}, { properties: { provisioningState: "Failed" } }, { error: "SECRET", properties: { provisioningState: "Succeeded" } },
            { properties: { provisioningState: "Succeeded", diagnostics: [{ message: "SECRET" }] } }]],
    ]) {
        for (const response of responses) {
            const fake = fakeIo(); const run = fake.io.run;
            fake.io.run = (...args) => args[2] === boundary ? JSON.stringify(response) : run(...args);
            await assert.rejects(executeApply(context, settings, approval, fake.io), errorStage(boundary, false));
            assert.equal(fake.events.some(event => event[2] === "create"), false);
        }
    }
});

test("fingerprint approval and file-read bounds accept exact limits and reject the adjacent byte", async () => {
    const { approval } = await fixture();
    const json = JSON.stringify(approval);
    assert.deepEqual(parseApproval(json.padEnd(16384)), approval);
    assert.throws(() => parseApproval(json.padEnd(16385)), errorStage("approval"));
    const parameters = { parameters: { enableApplication: { value: false } }, padding: "" };
    parameters.padding = "x".repeat(32768 - Buffer.byteLength(canonicalJson(parameters)));
    const whatIf = { status: "Succeeded", changes: [] };
    assert.doesNotThrow(() => reviewFingerprints(Buffer.alloc(8 * 1024 * 1024), parameters, whatIf));
    assert.throws(() => reviewFingerprints(Buffer.alloc(8 * 1024 * 1024 + 1), parameters, whatIf), errorStage("fingerprints"));
    assert.throws(() => reviewFingerprints(template, { ...parameters, padding: `${parameters.padding}x` }, whatIf), errorStage("fingerprints"));
    const full = { ...whatIf, padding: "" };
    full.padding = "x".repeat(8 * 1024 * 1024 - Buffer.byteLength(canonicalJson(full)));
    assert.doesNotThrow(() => reviewFingerprints(template, parameters, full));
    assert.throws(() => reviewFingerprints(template, parameters, { ...full, padding: `${full.padding}x` }), errorStage("fingerprints"));
    const path = fileURLToPath(new URL("../apply-workflow.mjs", import.meta.url));
    const bytes = readFileSync(path);
    assert.deepEqual(await readLocalBytes(path, bytes.length), bytes);
    await assert.rejects(readLocalBytes(path, bytes.length - 1), errorStage("fingerprints"));
    await assert.rejects(readLocalBytes(fileURLToPath(new URL(".", import.meta.url))), errorStage("fingerprints"));
});

test("runtime child process strips apply approval identity and parameter configuration", async () => {
    const io = runtimeIo({ ...process.env, APPLY_APPROVAL: "SECRET", APPLY_PARAMETERS: "SECRET", APPLY_CLIENT_ID: "SECRET" });
    const output = await io.run(process.execPath, ["-e",
        'process.stdout.write(JSON.stringify(["APPLY_APPROVAL","APPLY_PARAMETERS","APPLY_CLIENT_ID"].map(key => process.env[key] ?? null)))',
    ], "tooling", "virtual-owned");
    assert.deepEqual(JSON.parse(output), [null, null, null]);
});

test("create failure enabled output malformed completion and post-apply cleanup report possible changes", async () => {
    const { settings, approval } = await fixture();
    for (const response of ["SECRET", "{}", '{"properties":{"provisioningState":"Failed"}}',
        JSON.stringify({ id: `${scope}/providers/Microsoft.Resources/deployments/sidequest-apply-456`, properties: { provisioningState: "Succeeded", outputs: { applicationIsEnabled: { value: true } } } })]) {
        const fake = fakeIo(); const run = fake.io.run;
        fake.io.run = (...args) => args[2] === "create" ? response : run(...args);
        await assert.rejects(executeApply(context, settings, approval, fake.io), errorStage("create", true));
        assert.deepEqual(fake.events.at(-1), ["remove"]);
    }
    const fake = fakeIo();
    fake.io.removeDirectory = async () => { throw new Error("SECRET path"); };
    await assert.rejects(executeApply(context, settings, approval, fake.io), errorStage("cleanup", true));
});

test("CLI gate and allow path use exact-source checks output only hashes and never reuse planning approval", async () => {
    const { env, approval } = await fixture();
    const fake = fakeIo(); const writes = [];
    const deps = {
        readInputs: async runtime => runtime.PLAN_INPUTS, get: metadataFixture().get,
        run: async (file, args) => { assert.equal(file, "git"); assert.deepEqual(args, ["rev-parse", "HEAD"]); return `${sourceSha}\n`; },
        append: async (...args) => writes.push(args), output: text => writes.push(["stdout", text]), io: fake.io,
    };
    await main(["gate"], env, deps);
    assert.deepEqual(writes, [["virtual-output", `environment=sidequest-apply-staging\nenvironment-id=77\nsource-sha=${sourceSha}\n`]]);
    assert.deepEqual(fake.events, []);
    await assert.rejects(main(["apply"], { ...env, APPLY_APPROVAL: undefined }, deps), errorStage("approval"));
    await assert.rejects(main(["apply"], { ...env, APPLY_EXPECTED_ENVIRONMENT_ID: "78" }, deps), errorStage("environment"));
    assert.deepEqual(fake.events, []);
    await main(["apply"], env, deps);
    assert.equal(writes[1][0], "virtual-summary");
    assert.equal(JSON.parse(writes[2][1]).applicationArtifact, null);
    assert.equal(JSON.stringify(writes).includes("SECRET"), false);
    assert.equal(JSON.stringify(writes).includes(approval.reviewReference), false);
    assert.equal(JSON.stringify(writes).includes(jwt()), false);
    await assert.rejects(main(["apply"], env, { ...deps, io: fakeIo().io, append: async () => { throw new Error("SECRET"); } }), errorStage("output", true));
});

test("offline fingerprint CLI allow and denial paths never authorize or echo private data", async () => {
    const { parameters } = await fixture();
    const data = { template, parameters: Buffer.from(JSON.stringify(parameters)), whatif: Buffer.from(JSON.stringify(fullWhatIf())) };
    const lines = [];
    await main(["fingerprints", "template", "parameters", "whatif"], {}, {
        read: async path => data[path], output: text => lines.push(text),
    });
    assert.deepEqual(JSON.parse(lines[0]), reviewFingerprints(template, parameters, fullWhatIf()));
    assert.equal(lines[0].includes("approved"), false);
    assert.equal(lines[0].includes("SECRET"), false);
    await assert.rejects(main(["fingerprints", "SECRET"], {}, {}), errorStage("usage"));
    await assert.rejects(main(["fingerprints", "template", "parameters", "whatif"], {}, {
        read: async () => { throw new Error("SECRET path"); },
    }), errorStage("fingerprints"));
});

test("actual apply CLI returns fixed nonzero denials and safe help without external operations", () => {
    const path = fileURLToPath(new URL("../apply-workflow.mjs", import.meta.url));
    for (const [args, code, stage] of [
        [["--help"], 0, null], [[], 1, "usage"], [["apply", "SECRET"], 1, "usage"],
        [["gate"], 1, "context"], [["apply"], 1, "context"], [["fingerprints", "SECRET"], 1, "usage"],
    ]) {
        const result = spawnSync(process.execPath, [path, ...args], {
            encoding: "utf8", timeout: 5000, windowsHide: true,
            env: { ...process.env, GITHUB_EVENT_PATH: "", APPLY_ENABLED: "false" },
        });
        assert.equal(result.error, undefined);
        assert.equal(result.status, code);
        if (stage) {
            assert.equal(result.stdout, "");
            assert.equal(result.stderr, `${new ApplyError(stage).message}\n`);
        } else {
            assert.equal(result.stderr, "");
            assert.match(result.stdout, /Fingerprints are not approval/);
        }
        assert.equal(result.stderr.includes("SECRET"), false);
    }
});

test("actual apply workflow is manual first-attempt exact-source separately opted-in and environment gated", () => {
    const text = readFileSync(new URL("../../../.github/workflows/infrastructure-apply.yml", import.meta.url), "utf8").replaceAll("\r\n", "\n");
    const gate = text.slice(text.indexOf("  metadata:\n"), text.indexOf("\n  apply:\n"));
    const apply = text.slice(text.indexOf("\n  apply:\n"));
    assert.match(text, /^on:\n  workflow_dispatch:\n    inputs:\n      environment:/m);
    assert.match(text, /options: \[staging, production\]/);
    assert.doesNotMatch(text, /^\s*(?:push|pull_request|workflow_run|workflow_call|schedule):/m);
    assert.match(text, /^permissions: \{\}$/m);
    assert.doesNotMatch(gate, /^    environment:|id-token:|secrets\./m);
    assert.ok(gate.indexOf('"$GITHUB_RUN_ATTEMPT" != "1"') < gate.indexOf("actions/checkout@"));
    assert.match(gate, /\$GITHUB_REF" != "refs\/heads\/main"/);
    assert.match(gate, /\$GITHUB_WORKFLOW_SHA" != "\$GITHUB_SHA"/);
    assert.match(gate, /SIDEQUEST_INFRASTRUCTURE_APPLY_ENABLED/);
    assert.match(gate, /SIDEQUEST_APPLY_ADMIN_CONTROLS_VERIFIED/);
    assert.doesNotMatch(text, /SIDEQUEST_PLANNING_ENABLED|SIDEQUEST_PLANNING_ADMIN_CONTROLS_VERIFIED/);
    assert.match(apply, /needs: metadata\n    if: \$\{\{ needs\.metadata\.result == 'success' \}\}/);
    assert.match(apply, /environment:\n      name: \$\{\{ needs\.metadata\.outputs\.environment \}\}/);
    assert.equal((text.match(/id-token: write/g) ?? []).length, 1);
    assert.deepEqual([...text.matchAll(/uses: ([^\s#]+)/g)].map(match => match[1]),
        Array(2).fill("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1"));
    assert.equal((text.match(/ref: \$\{\{ github.sha \}\}/g) ?? []).length, 2);
    assert.match(apply, /secrets\.AZURE_APPLY_CLIENT_ID/);
    assert.match(apply, /secrets\.AZURE_PLANNING_CLIENT_ID/);
    assert.match(apply, /secrets\.SIDEQUEST_APPLY_APPROVAL/);
    assert.doesNotMatch(text, /continue-on-error|always\(\)|upload-artifact|azure\/login|AZURE_CLIENT_SECRET|run:.*\$\{\{/);
    assert.match(text, /cancel-in-progress: false/);
    const planning = readFileSync(new URL("../../../.github/workflows/infrastructure-plan.yml", import.meta.url), "utf8");
    assert.doesNotMatch(planning, /apply-workflow|AZURE_APPLY_CLIENT_ID|SIDEQUEST_APPLY_APPROVAL/);
    const ci = readFileSync(new URL("../../../.github/workflows/infrastructure.yml", import.meta.url), "utf8");
    assert.match(ci, /\.github\/workflows\/infrastructure-apply\.yml/);
});
