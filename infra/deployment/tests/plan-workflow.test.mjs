import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import {
    AUDIENCE, BICEP_SHA256, bindAccount, compileTemplate, contextFromEnvironment, executePlan,
    main, metadataGate, oidcRequest, REPOSITORY, runtimeIo, summarizeWhatIf, validateOidcToken,
    validateSettings, verifyCi, verifyEnvironment, WORKFLOW,
} from "../plan-workflow.mjs";
import { getJson, parseJson, PlanError, readResponse, runCaptured } from "../plan-io.mjs";

const sha = "a17b8562dff3793f747c60b10cc1dd30201c83ec";
const api = `https://api.github.com/repos/${REPOSITORY}`;
const tenant = "8ecdae60-49c0-4e23-91ab-5ae219764fea";
const subscription = "fe9367f6-38b1-4503-9f9b-1cf4f82e61d7";
const client = "c6dc0937-0afb-4dc1-b629-21e4e01be3dd";
const reviewer = { id: 17, type: "User", login: "reviewer-fixture" };
const clone = value => structuredClone(value);
const stageError = stage => error => {
    assert.ok(error instanceof PlanError);
    assert.equal(error.stage, stage);
    assert.equal(error.message, new PlanError(stage).message);
    assert.equal(error.message.includes("SECRET"), false);
    return true;
};

function env(overrides = {}) {
    return {
        GITHUB_REPOSITORY: REPOSITORY, GITHUB_REPOSITORY_ID: "123", GITHUB_EVENT_NAME: "workflow_dispatch",
        GITHUB_REF: "refs/heads/main", GITHUB_RUN_ATTEMPT: "1", GITHUB_RUN_ID: "456",
        GITHUB_SHA: sha, GITHUB_WORKFLOW_SHA: sha, GITHUB_WORKFLOW_REF: `${REPOSITORY}/${WORKFLOW}@refs/heads/main`,
        PLAN_INPUTS: '{"environment":"staging"}', PLAN_ENABLED: "true", PLAN_ADMIN_CONTROLS_VERIFIED: "true",
        GH_TOKEN: "synthetic-github-token-not-a-credential",
        GITHUB_OUTPUT: "virtual-output", GITHUB_STEP_SUMMARY: "virtual-summary",
        PLAN_EXPECTED_ENVIRONMENT_ID: "77", PLAN_EXPECTED_SOURCE_SHA: sha,
        ...overrides,
    };
}
const context = contextFromEnvironment(env());
const repository = { id: 123, full_name: REPOSITORY, fork: false, default_branch: "main", archived: false, disabled: false };

function environment() {
    return {
        id: 77, node_id: "environment-node", name: "sidequest-staging", url: `${api}/environments/sidequest-staging`,
        created_at: "2026-09-01T00:00:00Z", updated_at: "2026-09-01T00:00:00Z",
        deployment_branch_policy: { protected_branches: false, custom_branch_policies: true },
        protection_rules: [
            { id: 1, node_id: "review-node", type: "required_reviewers", prevent_self_review: true, reviewers: [{ type: "User", reviewer }] },
            { id: 2, node_id: "branch-node", type: "branch_policy" },
        ],
    };
}
function policies() { return { total_count: 1, branch_policies: [{ id: 9, node_id: "policy-node", name: "main", type: "branch" }] }; }
function workflow(filename = "ci.yml", id = 10) { return { id, path: `.github/workflows/${filename}`, state: "active" }; }
function run(filename = "ci.yml", id = 10) {
    return {
        id: 900, workflow_id: id, path: `.github/workflows/${filename}`, head_sha: sha, head_branch: "main",
        event: "push", run_number: 20, run_attempt: 1, status: "completed", conclusion: "success",
        repository: clone(repository), head_repository: clone(repository),
        actor: { ...reviewer, id: 99 }, triggering_actor: { ...reviewer, id: 99 },
    };
}
function ciRuns(value = run()) { return { total_count: 1, workflow_runs: [value] }; }
function metadata() {
    return new Map([
        [api, clone(repository)],
        [`${api}/git/ref/heads/main`, { ref: "refs/heads/main", object: { type: "commit", sha } }],
        [`${api}/actions/workflows/infrastructure-plan.yml`, workflow("infrastructure-plan.yml", 30)],
        [`${api}/actions/runs/456`, { ...run("infrastructure-plan.yml", 30), id: 456, event: "workflow_dispatch", status: "in_progress", conclusion: null }],
        [`${api}/actions/workflows/ci.yml`, workflow()],
        [`${api}/actions/workflows/ci.yml/runs?head_sha=${sha}&branch=main&per_page=100&page=1`, ciRuns()],
        [`${api}/actions/workflows/infrastructure.yml`, workflow("infrastructure.yml", 20)],
        [`${api}/actions/workflows/infrastructure.yml/runs?head_sha=${sha}&branch=main&per_page=100&page=1`, ciRuns(run("infrastructure.yml", 20))],
        [`${api}/environments/sidequest-staging`, environment()],
        [`${api}/environments/sidequest-staging/deployment-branch-policies?per_page=100&page=1`, policies()],
    ]);
}
function getter(data, calls = []) {
    return async url => {
        calls.push(url);
        assert.ok(data.has(url), "Only exact expected read-only metadata routes may be used.");
        return clone(data.get(url));
    };
}

test("manual opt-in and owner acknowledgement are explicit independent fail-closed assertions", () => {
    for (const value of [undefined, "", "false", false, true, "TRUE", " true", "SECRET"]) {
        assert.throws(() => contextFromEnvironment(env({ PLAN_ENABLED: value })), stageError("optin"));
        assert.throws(() => contextFromEnvironment(env({ PLAN_ADMIN_CONTROLS_VERIFIED: value })), stageError("acknowledgement"));
    }
    assert.deepEqual(contextFromEnvironment(env()), context);
    assert.equal(contextFromEnvironment(env({ PLAN_INPUTS: '{"environment":"production"}' })).environmentName, "sidequest-production");
});

test("dispatch context rejects repository event ref SHA attempt and unexpected-input injection", () => {
    const invalid = {
        GITHUB_REPOSITORY: ["other/sidequest", "", undefined],
        GITHUB_EVENT_NAME: ["push", "pull_request"],
        GITHUB_REF: ["refs/tags/main", "refs/heads/topic", "main"],
        GITHUB_RUN_ATTEMPT: ["2", "0", 1, undefined],
        GITHUB_RUN_ID: ["0", "-1", "1; SECRET", "9007199254740992"],
        GITHUB_REPOSITORY_ID: ["0", undefined],
        GITHUB_SHA: ["SECRET", sha.toUpperCase(), `${sha}\n`],
        GITHUB_WORKFLOW_SHA: ["b".repeat(40), undefined],
        GITHUB_WORKFLOW_REF: [`${REPOSITORY}/${WORKFLOW}@refs/heads/topic`, undefined],
        PLAN_INPUTS: ["null", "[]", "{}", '{"environment":"staging","SECRET":"SECRET"}', '{"environment":"staging;SECRET"}', '{"environment":true}', "SECRET"],
    };
    for (const [key, values] of Object.entries(invalid)) {
        for (const value of values) assert.throws(() => contextFromEnvironment(env({ [key]: value })), stageError("context"));
    }
});

test("metadata gate binds exact checkout main repository current run CI and environment", async () => {
    const calls = [];
    assert.deepEqual(await metadataGate(context, getter(metadata(), calls), sha), { ...context, environmentId: 77 });
    assert.equal(calls.length, 10);
    assert.ok(calls.every(url => url.startsWith(api)));
});

test("metadata gate rejects source repository current-run and first-attempt mismatches", async () => {
    await assert.rejects(metadataGate(context, getter(metadata()), "b".repeat(40)), stageError("source"));
    for (const [url, key, value] of [
        [api, "id", 321], [api, "full_name", "SECRET/other"], [api, "fork", true],
        [api, "default_branch", "develop"], [api, "archived", true], [api, "disabled", true],
        [`${api}/git/ref/heads/main`, "object", { type: "commit", sha: "b".repeat(40) }],
        [`${api}/git/ref/heads/main`, "object", { type: "tag", sha }],
        [`${api}/actions/runs/456`, "run_attempt", 2],
        [`${api}/actions/runs/456`, "event", "pull_request"],
        [`${api}/actions/runs/456`, "head_sha", "b".repeat(40)],
        [`${api}/actions/runs/456`, "id", 457],
        [`${api}/actions/runs/456`, "status", "completed"],
        [`${api}/actions/runs/456`, "actor", { id: 99, type: "Bot", login: "bot" }],
        [`${api}/actions/runs/456`, "triggering_actor", { ...reviewer, id: 100 }],
        [`${api}/actions/workflows/infrastructure-plan.yml`, "state", "disabled_manually"],
    ]) {
        const data = metadata();
        data.get(url)[key] = value;
        await assert.rejects(metadataGate(context, getter(data), sha), stageError("source"));
    }
});

test("CI requires complete same-source accepted workflow evidence and latest success", () => {
    for (const [key, value] of [
        ["head_sha", "b".repeat(40)], ["head_branch", "other"], ["path", ".github/workflows/fake.yml"],
        ["workflow_id", 999], ["event", "pull_request"], ["repository", { ...repository, id: 999 }],
        ["head_repository", null], ["run_attempt", 0], ["run_number", 0], ["status", undefined],
        ["conclusion", "skipped"], ["conclusion", "failure"], ["conclusion", null],
    ]) assert.throws(() => verifyCi(workflow(), ciRuns({ ...run(), [key]: value }), context, "ci.yml"), stageError("source"));
    for (const value of [null, [], {}, { total_count: 0, workflow_runs: [] },
        { total_count: 2, workflow_runs: [run()] }, { total_count: 100, workflow_runs: Array(100).fill(run()) },
        { total_count: 2, workflow_runs: [run(), run()] }]) {
        assert.throws(() => verifyCi(workflow(), value, context, "ci.yml"), stageError("source"));
    }
    assert.throws(() => verifyCi({ ...workflow(), state: "disabled_manually" }, ciRuns(), context, "ci.yml"), stageError("source"));
    const earlier = { ...run(), id: 899, run_number: 19, conclusion: "failure" };
    assert.doesNotThrow(() => verifyCi(workflow(), { total_count: 2, workflow_runs: [run(), earlier] }, context, "ci.yml"));
    for (const latest of [
        { ...run(), id: 901, run_number: 21, conclusion: "failure" },
        { ...run(), id: 901, run_number: 21, status: "in_progress", conclusion: null },
    ]) assert.throws(() => verifyCi(workflow(), { total_count: 2, workflow_runs: [latest, run()] }, context, "ci.yml"), stageError("source"));
    assert.doesNotThrow(() => verifyCi(workflow("infrastructure.yml", 20),
        ciRuns({ ...run("infrastructure.yml", 20), event: "workflow_dispatch" }), context, "infrastructure.yml"));
});

test("environment requires existing exact identity explicit User reviewers and self-review prevention", () => {
    for (const [key, value] of [
        ["id", 0], ["name", "sidequest-production"], ["url", "https://example.invalid/SECRET"],
        ["node_id", ""], ["created_at", undefined], ["updated_at", "SECRET"], ["protection_rules", []],
        ["protection_rules", null], ["deployment_branch_policy", null],
    ]) assert.throws(() => verifyEnvironment({ ...environment(), [key]: value }, policies(), context), stageError("environment"));
    for (const value of [null, [], {}, { type: "User", reviewer: { ...reviewer, type: "Bot" } },
        { type: "Team", reviewer: { id: 20, slug: "team" } }, { type: "User", reviewer: { ...reviewer, id: 0 } },
        { type: "User", reviewer: { ...reviewer, login: "bot[bot]" } }]) {
        const data = environment();
        data.protection_rules[0].reviewers = [value];
        assert.throws(() => verifyEnvironment(data, policies(), context), stageError("environment"));
    }
    for (const value of [false, undefined, "true"]) {
        const data = environment();
        data.protection_rules[0].prevent_self_review = value;
        assert.throws(() => verifyEnvironment(data, policies(), context), stageError("environment"));
    }
    for (const size of [0, 1, 6, 7]) {
        const data = environment();
        data.protection_rules[0].reviewers = Array.from({ length: size }, (_, index) => ({ type: "User", reviewer: { ...reviewer, id: index + 1 } }));
        if (size === 1 || size === 6) assert.equal(verifyEnvironment(data, policies(), context), 77);
        else assert.throws(() => verifyEnvironment(data, policies(), context), stageError("environment"));
    }
    const duplicate = environment();
    duplicate.protection_rules[0].reviewers.push(clone(duplicate.protection_rules[0].reviewers[0]));
    assert.throws(() => verifyEnvironment(duplicate, policies(), context), stageError("environment"));
});

test("environment rejects protected-all tag wildcard multiple and unknown control rules", () => {
    for (const value of [
        { protected_branches: true, custom_branch_policies: false },
        { protected_branches: false, custom_branch_policies: false },
        { protected_branches: false, custom_branch_policies: "true" }, {},
    ]) assert.throws(() => verifyEnvironment({ ...environment(), deployment_branch_policy: value }, policies(), context), stageError("environment"));
    for (const branch of [{ id: 1, name: "main", type: "tag" }, { id: 1, name: "*", type: "branch" },
        { id: 1, name: "main*", type: "branch" }, { id: 1, name: "refs/heads/main", type: "branch" },
        { id: 1, name: "main" }, null]) {
        assert.throws(() => verifyEnvironment(environment(), { total_count: 1, branch_policies: [branch] }, context), stageError("environment"));
    }
    for (const policy of [{ total_count: 0, branch_policies: [] }, { total_count: 2, branch_policies: policies().branch_policies }, {},
        { total_count: 2, branch_policies: [...policies().branch_policies, { id: 2, name: "*", type: "tag" }] }]) {
        assert.throws(() => verifyEnvironment(environment(), policy, context), stageError("environment"));
    }
    for (const extra of [{ id: 4, node_id: "rule", type: "unknown" }, environment().protection_rules[0]]) {
        const data = environment();
        data.protection_rules.push(extra);
        assert.throws(() => verifyEnvironment(data, policies(), context), stageError("environment"));
    }
    for (const value of [0, 43200, -1, 43201, "30"]) {
        const data = environment();
        data.protection_rules.push({ id: 4, node_id: "wait", type: "wait_timer", wait_timer: value });
        if (value === 0 || value === 43200) assert.equal(verifyEnvironment(data, policies(), context), 77);
        else assert.throws(() => verifyEnvironment(data, policies(), context), stageError("environment"));
    }
});

test("HTTP metadata rejects pagination HTTP failures redirects malformed JSON and oversized bodies without raw errors", async () => {
    const good = async (url, options) => {
        assert.equal(url, api);
        assert.equal(options.method, "GET");
        assert.equal(options.redirect, "error");
        assert.equal(options.headers["X-GitHub-Api-Version"], "2026-03-10");
        return Response.json(repository);
    };
    assert.deepEqual(await getJson(api, "synthetic-token", "metadata", good), repository);
    for (const response of [
        new Response("SECRET", { status: 403 }),
        new Response("SECRET", { status: 404 }),
        new Response("SECRET", { status: 302, headers: { location: "https://example.invalid" } }),
        Response.json(repository, { headers: { link: '<https://example.invalid>; rel="next"' } }),
        new Response("SECRET", { headers: { "content-type": "application/json" } }),
        new Response("{}", { headers: { "content-type": "text/html" } }),
        new Response("{}", { headers: { "content-type": "application/json", "content-length": "2097153" } }),
        new Response(Buffer.from([0xff]), { headers: { "content-type": "application/json" } }),
    ]) await assert.rejects(getJson(api, "token", "metadata", async () => response), stageError("metadata"));
    await assert.rejects(getJson(api, "token", "metadata", async () => { throw new Error("SECRET"); }), stageError("metadata"));
    assert.equal((await readResponse(new Response("1234"), "metadata", 4)).toString(), "1234");
    await assert.rejects(readResponse(new Response("12345"), "metadata", 4), stageError("metadata"));
    assert.deepEqual(parseJson('{"a":1}', "metadata", 7), { a: 1 });
    assert.throws(() => parseJson('{"a":1} ', "metadata", 7), stageError("metadata"));
});

// All identifiers, region and network values below are deliberately fictional.
function settings(overrides = {}) {
    return {
        tenantId: tenant, subscriptionId: subscription, clientId: client, resourceGroup: "fixture-group",
        parametersJson: JSON.stringify({
            location: "fixture-region", environmentName: "staging", operationalOwner: "Fixture owner",
            virtualNetworkAddressPrefix: "192.0.2.0/24", applicationSubnetAddressPrefix: "192.0.2.0/26",
            privateEndpointSubnetAddressPrefix: "192.0.2.64/26", workforceTenantId: tenant,
            workforceClientId: "621715c8-b2a0-4b0d-bb1a-a2500733b4f9", workforceRole: "Fixture.Workforce",
            bootstrapAdministratorObjectId: "35ba91b7-b5b6-442b-9398-95d025e43ca6", sqlAdministratorGroupName: "Fixture group",
            sqlAdministratorGroupObjectId: "cca0fa8b-4110-44c5-9d4f-7dd6b51e4ff2",
            blobRestoreDays: 7, sqlPointInTimeRetentionDays: 14, logRetentionDays: 30,
        }),
        ...overrides,
    };
}
function account() {
    return { id: subscription, tenantId: tenant, environmentName: "AzureCloud", state: "Enabled", isDefault: true, user: { type: "servicePrincipal", name: client } };
}
function jwt(overrides = {}) {
    const payload = {
        iss: "https://token.actions.githubusercontent.com", aud: AUDIENCE, repository: REPOSITORY,
        repository_id: "123", ref: "refs/heads/main", ref_type: "branch", sha, event_name: "workflow_dispatch",
        environment: "sidequest-staging", run_id: "456", run_attempt: "1",
        workflow_ref: `${REPOSITORY}/${WORKFLOW}@refs/heads/main`, workflow_sha: sha,
        exp: Math.floor(Date.now() / 1000) + 600, nbf: 0, ...overrides,
    };
    return `${Buffer.from('{"alg":"RS256"}').toString("base64url")}.${Buffer.from(JSON.stringify(payload)).toString("base64url")}.synthetic-signature`;
}

test("approved settings bind selected environment deployment identities and disabled-only existing preflight", async () => {
    const good = await validateSettings(settings(), context);
    assert.equal(good.parameters.environmentName, "staging");
    assert.equal(bindAccount(account(), good).parameters.enableApplication.value, false);
    for (const [key, value] of [
        ["tenantId", client], ["subscriptionId", "SECRET"], ["clientId", "00000000-0000-0000-0000-000000000000"],
        ["clientId", "11111111-1111-4111-8111-111111111111"], ["resourceGroup", "--subscription=SECRET"],
        ["resourceGroup", "x;SECRET"], ["resourceGroup", "../other"], ["resourceGroup", "name."],
        ["resourceGroup", "a".repeat(91)], ["parametersJson", "SECRET"],
        ["parametersJson", '{},"accountContext":{},"parameters":{}'],
    ]) await assert.rejects(validateSettings(settings({ [key]: value }), context), stageError("settings"));
    for (const key of Object.keys(settings())) {
        const incomplete = settings();
        delete incomplete[key];
        await assert.rejects(validateSettings(incomplete, context), stageError("settings"));
    }
    await assert.rejects(validateSettings(settings({ SECRET: "SECRET" }), context), stageError("settings"));
    for (const override of [{ enableApplication: true }, { environmentName: "production" }, { SECRET: "SECRET" }, { blobRestoreDays: 365 }]) {
        const parameters = { ...JSON.parse(settings().parametersJson), ...override };
        await assert.rejects(validateSettings(settings({ parametersJson: JSON.stringify(parameters) }), context), stageError("settings"));
    }
});

test("OIDC endpoint audience and token claims reject injection drift expired tokens and wrong jobs", () => {
    const url = "https://pipelines.actions.githubusercontent.com/example/idtoken?api-version=2.0";
    assert.equal(new URL(oidcRequest(url, "synthetic-request-token")).searchParams.get("audience"), AUDIENCE);
    for (const value of [
        "http://pipelines.actions.githubusercontent.com/idtoken", "https://actions.githubusercontent.com/idtoken",
        "https://pipelines.actions.githubusercontent.com.evil.invalid/idtoken", "https://evil.invalid/idtoken",
        "https://user:SECRET@pipelines.actions.githubusercontent.com/idtoken",
        "https://pipelines.actions.githubusercontent.com:444/idtoken",
        `${url}#SECRET`, `${url}&audience=SECRET`, `${url}&api-version=SECRET`, "SECRET",
    ]) assert.throws(() => oidcRequest(value, "synthetic-request-token"), stageError("oidc"));
    for (const value of ["", undefined, "SECRET\r\nHeader: SECRET"]) assert.throws(() => oidcRequest(url, value), stageError("oidc"));
    assert.equal(validateOidcToken(jwt(), context).split(".").length, 3);
    for (const [key, value] of [
        ["iss", "https://evil.invalid"], ["aud", "SECRET"], ["repository", "other/repo"], ["repository_id", "999"],
        ["ref", "refs/tags/main"], ["ref_type", "tag"], ["sha", "b".repeat(40)], ["event_name", "push"],
        ["environment", "sidequest-production"], ["run_id", "457"], ["run_attempt", "2"],
        ["workflow_ref", "SECRET"], ["workflow_sha", "b".repeat(40)], ["exp", 0], ["exp", "9999999999"], ["nbf", 9999999999],
    ]) assert.throws(() => validateOidcToken(jwt({ [key]: value }), context), stageError("oidc"));
    for (const value of ["SECRET", "a.b.c", `${jwt()}\n`, "a".repeat(32769)]) {
        assert.throws(() => validateOidcToken(value, context), stageError("oidc"));
    }
});

test("OIDC transport uses only validated runtime GET without redirects and bounded captured JSON", async () => {
    const runtime = {
        ACTIONS_ID_TOKEN_REQUEST_URL: "https://pipelines.actions.githubusercontent.com/example/idtoken?api-version=2.0",
        ACTIONS_ID_TOKEN_REQUEST_TOKEN: "synthetic-request-token",
    };
    const value = jwt();
    const io = runtimeIo(runtime, async (url, options) => {
        assert.equal(new URL(url).searchParams.get("audience"), AUDIENCE);
        assert.equal(options.method, "GET");
        assert.equal(options.redirect, "error");
        assert.equal(options.headers.Authorization, "Bearer synthetic-request-token");
        return Response.json({ value });
    });
    assert.equal(await io.oidc(), value);
    await assert.rejects(runtimeIo(runtime, async () => new Response("SECRET", { status: 401 })).oidc(), stageError("oidc"));
    await assert.rejects(runtimeIo(runtime, async () => Response.json({ SECRET: "SECRET" })).oidc(), stageError("oidc"));
    await assert.rejects(runtimeIo(runtime, async () => new Response(JSON.stringify({ value }), {
        headers: { "content-type": "text/html" },
    })).oidc(), stageError("oidc"));
});

function fakeIo(overrides = {}) {
    const events = [];
    const writes = [];
    const token = jwt();
    const directory = join(fileURLToPath(new URL(".", import.meta.url)), "virtual-owned-plan-directory");
    const io = {
        createDirectory: async () => { events.push(["directory"]); return directory; },
        compile: async () => { events.push(["compile"]); },
        oidc: async () => { events.push(["oidc"]); return token; },
        mask: value => { events.push(["mask", value]); },
        write: async (...args) => { writes.push(args); events.push(["write"]); },
        removeDirectory: async path => { assert.equal(path, directory); events.push(["remove"]); },
        run: async (file, args, stage) => {
            events.push(["run", file, args, stage]);
            if (stage === "tooling") return '{"azure-cli":"2.76.0"}';
            if (stage === "account" && args[1] === "show") return JSON.stringify(account());
            if (stage === "resourceGroup") return JSON.stringify({
                id: `/subscriptions/${subscription}/resourceGroups/fixture-group`, name: "fixture-group", properties: { provisioningState: "Succeeded" },
            });
            if (stage === "validate") return '{"properties":{"provisioningState":"Succeeded","diagnostics":null},"error":null}';
            if (stage === "whatif") return JSON.stringify({ status: "Succeeded", changes: [
                { changeType: "Create", resourceId: "SECRET", after: { password: "SECRET" } },
                { changeType: "Modify" }, { changeType: "Create" }, { changeType: "NoChange" },
            ] });
            return "";
        },
        ...overrides,
    };
    return { io, events, writes, token, directory };
}

test("planning executes exact read-only command sequence and emits only the bounded safe summary", async () => {
    const configuration = await validateSettings(settings(), context);
    const fake = fakeIo();
    const summary = await executePlan(context, configuration, fake.io);
    assert.deepEqual(summary, {
        sourceSha: sha, environment: "sidequest-staging", enableApplication: false,
        changeTypeCounts: { Create: 2, Delete: 0, Deploy: 0, Ignore: 0, Modify: 1, NoChange: 1, Unsupported: 0 },
        limitations: [
            "Read-only planning, not deployment authorization or full change review.",
            "GitHub environment review relies on owner-verified administrator controls.",
            "No identity/workforce, private routing, runtime, recovery or load acceptance is established.",
        ],
    });
    assert.equal(JSON.stringify(summary).includes("SECRET"), false);
    assert.ok(JSON.stringify(summary).length < 1024);
    const args = fake.events.filter(event => event[0] === "run").map(event => event[2]);
    const common = [
        "--resource-group", "fixture-group", "--subscription", subscription, "--name", "sidequest-plan-456",
        "--template-file", join(fake.directory, "template.json"), "--parameters", `@${join(fake.directory, "parameters.json")}`,
        "--mode", "Incremental", "--validation-level", "ProviderNoRbac", "--no-prompt", "true", "--output", "json", "--only-show-errors",
    ];
    assert.deepEqual(args, [
        ["version", "--output", "json"],
        ["login", "--service-principal", "--username", client, "--tenant", tenant, "--federated-token", fake.token, "--output", "none", "--only-show-errors"],
        ["account", "set", "--subscription", subscription],
        ["account", "show", "--subscription", subscription, "--output", "json", "--only-show-errors"],
        ["group", "show", "--name", "fixture-group", "--subscription", subscription, "--output", "json", "--only-show-errors"],
        ["deployment", "group", "validate", ...common],
        ["deployment", "group", "what-if", ...common, "--result-format", "ResourceIdOnly", "--no-pretty-print"],
        ["account", "clear", "--only-show-errors"],
    ]);
    assert.deepEqual(fake.events.slice(0, 6).map(event => event[0]), ["directory", "compile", "run", "oidc", "mask", "run"]);
    assert.equal(fake.writes.length, 1);
    const [path, parameters, mode] = fake.writes[0];
    assert.equal(path, join(fake.directory, "parameters.json"));
    assert.equal(mode, 0o600);
    assert.equal(JSON.parse(parameters).parameters.enableApplication.value, false);
    assert.deepEqual(fake.events.at(-1), ["remove"]);
});

test("wrong account client tenant subscription cloud or state prevents all resource planning", async () => {
    const configuration = await validateSettings(settings(), context);
    for (const override of [{ id: client }, { tenantId: client }, { environmentName: "AzureChinaCloud" },
        { state: "Disabled" }, { isDefault: false }, { user: { type: "user", name: client } },
        { user: { type: "servicePrincipal", name: tenant } }, { user: null }]) {
        const fake = fakeIo();
        const base = fake.io.run;
        fake.io.run = (file, args, stage) => stage === "account" && args[1] === "show"
            ? JSON.stringify({ ...account(), ...override }) : base(file, args, stage);
        await assert.rejects(executePlan(context, configuration, fake.io), stageError("account"));
        assert.equal(fake.events.some(event => event[3] === "resourceGroup"), false);
        assert.deepEqual(fake.events.at(-1), ["remove"]);
    }
});

test("command failures are stage-specific redacted non-success and always clean owned state", async () => {
    const configuration = await validateSettings(settings(), context);
    for (const stage of ["tooling", "login", "account", "resourceGroup", "validate", "whatif", "cleanup"]) {
        const fake = fakeIo();
        const base = fake.io.run;
        fake.io.run = (file, args, current) => {
            if (current === stage) throw new Error("SECRET provider body");
            return base(file, args, current);
        };
        await assert.rejects(executePlan(context, configuration, fake.io), stageError(stage));
        assert.deepEqual(fake.events.at(-1), ["remove"]);
        if (stage === "tooling") assert.equal(fake.events.some(event => event[0] === "oidc"), false);
    }
    for (const stage of ["compiler", "oidc"]) {
        const fake = fakeIo({ [stage === "compiler" ? "compile" : "oidc"]: async () => { throw new Error("SECRET"); } });
        await assert.rejects(executePlan(context, configuration, fake.io), stageError(stage));
        assert.deepEqual(fake.events.at(-1), ["remove"]);
    }
    const fake = fakeIo({ removeDirectory: async () => { throw new Error("SECRET path"); } });
    await assert.rejects(executePlan(context, configuration, fake.io), stageError("cleanup"));
});

test("tool version resource-group and ARM incomplete responses fail rather than fabricating success", async () => {
    const configuration = await validateSettings(settings(), context);
    for (const [stage, responses] of [
        ["tooling", [{ "azure-cli": "2.75.9" }, { "azure-cli": "3.0.0" }, {}, null]],
        ["resourceGroup", [{ id: "SECRET", name: "fixture-group", properties: { provisioningState: "Succeeded" } }, {}]],
        ["validate", [{}, { properties: { provisioningState: "Failed" } }, { error: { message: "SECRET" } },
            { properties: { provisioningState: "Succeeded", diagnostics: [{ message: "SECRET" }] } }]],
        ["whatif", [{}, { status: "Failed", changes: [] }, { status: "Succeeded", changes: [{ changeType: "Ignore" }] },
            { status: "Succeeded", changes: [{ changeType: "Unsupported" }] }, { status: "Succeeded", changes: [{ changeType: "SECRET" }] }]],
    ]) {
        for (const response of responses) {
            const fake = fakeIo();
            const base = fake.io.run;
            fake.io.run = (file, args, current) => current === stage ? JSON.stringify(response) : base(file, args, current);
            await assert.rejects(executePlan(context, configuration, fake.io), stageError(stage));
            assert.deepEqual(fake.events.at(-1), ["remove"]);
        }
    }
    assert.equal(summarizeWhatIf({ status: "Succeeded", changes: [] }, context).changeTypeCounts.Create, 0);
    const all = ["Create", "Delete", "Deploy", "Modify", "NoChange"].map(changeType => ({ changeType }));
    assert.deepEqual(summarizeWhatIf({ status: "Succeeded", changes: all }, context).changeTypeCounts,
        { Create: 1, Delete: 1, Deploy: 1, Ignore: 0, Modify: 1, NoChange: 1, Unsupported: 0 });
    assert.throws(() => summarizeWhatIf({ status: "Succeeded", changes: Array(10001).fill({ changeType: "Create" }) }, context), stageError("whatif"));
});

test("compiler hash mismatch stops execution before writing or running unverified bytes", async () => {
    let effects = 0;
    await assert.rejects(compileTemplate("virtual-owned", "template.json", {
        compilerBytes: async () => Buffer.from("not the pinned compiler"),
        write: async () => { effects++; }, run: async () => { effects++; },
    }), stageError("compiler"));
    assert.equal(effects, 0);
    assert.match(BICEP_SHA256, /^[a-f0-9]{64}$/);
});

test("real process boundary captures stderr limits output handles invalid UTF-8 and fails nonzero or timeout", async () => {
    const options = { stage: "validate", timeout: 5000, limit: 16 };
    assert.equal(await runCaptured(process.execPath, ["-e", "process.stdout.write('1234567890123456')"], options), "1234567890123456");
    assert.equal(await runCaptured(process.execPath, ["-e", "process.stderr.write('SECRET'); process.stdout.write('ok')"], options), "ok");
    for (const script of [
        "process.stderr.write('SECRET'); process.exit(7)",
        "process.stdout.write('12345678901234567')",
        "process.stderr.write('12345678901234567')",
        "process.stdout.write(Buffer.from([255]))",
    ]) await assert.rejects(runCaptured(process.execPath, ["-e", script], options), stageError("validate"));
    await assert.rejects(runCaptured(process.execPath, ["-e", "setInterval(()=>{},1000)"], { ...options, timeout: 300 }), stageError("validate"));
    await assert.rejects(runCaptured("nonexistent-sidequest-SECRET-command", [], options), stageError("validate"));
});

test("runtime child environment excludes GitHub OIDC and parameter values and isolates the Azure profile", async () => {
    const runtime = runtimeIo({
        ...process.env, GH_TOKEN: "SECRET", GITHUB_TOKEN: "SECRET", ACTIONS_ID_TOKEN_REQUEST_TOKEN: "SECRET",
        PLAN_PARAMETERS: "SECRET", SIDEQUEST_SECRET: "SECRET", AZURE_CLIENT_SECRET: "SECRET",
    });
    const result = JSON.parse(await runtime.run(process.execPath, ["-e", `
        const keys = ["GH_TOKEN", "GITHUB_TOKEN", "ACTIONS_ID_TOKEN_REQUEST_TOKEN", "PLAN_PARAMETERS",
            "SIDEQUEST_SECRET", "AZURE_CLIENT_SECRET", "AZURE_CONFIG_DIR", "AZURE_CORE_COLLECT_TELEMETRY",
            "AZURE_EXTENSION_USE_DYNAMIC_INSTALL"];
        process.stdout.write(JSON.stringify(Object.fromEntries(keys.map(key => [key, process.env[key] ?? null]))));
    `], "tooling", "virtual-owned"));
    assert.deepEqual(result, {
        GH_TOKEN: null, GITHUB_TOKEN: null, ACTIONS_ID_TOKEN_REQUEST_TOKEN: null, PLAN_PARAMETERS: null,
        SIDEQUEST_SECRET: null, AZURE_CLIENT_SECRET: null, AZURE_CONFIG_DIR: join("virtual-owned", "azure"),
        AZURE_CORE_COLLECT_TELEMETRY: "false", AZURE_EXTENSION_USE_DYNAMIC_INSTALL: "no",
    });
});

test("workflow entrypoint wires gate outputs then rechecks environment identity before any OIDC", async () => {
    const writes = [];
    const fake = fakeIo();
    const configuration = settings();
    const environmentValues = {
        PLAN_TENANT_ID: tenant, PLAN_SUBSCRIPTION_ID: subscription, PLAN_CLIENT_ID: client,
        PLAN_RESOURCE_GROUP: configuration.resourceGroup, PLAN_PARAMETERS: configuration.parametersJson,
    };
    const dependencies = {
        readInputs: async runtime => runtime.PLAN_INPUTS,
        get: getter(metadata()), run: async (file, args) => { assert.equal(file, "git"); assert.deepEqual(args, ["rev-parse", "HEAD"]); return `${sha}\n`; },
        append: async (...args) => { writes.push(args); }, output: text => { writes.push(["stdout", text]); }, io: fake.io,
    };
    for (const [key, stage] of [["PLAN_ENABLED", "optin"], ["PLAN_ADMIN_CONTROLS_VERIFIED", "acknowledgement"]]) {
        await assert.rejects(main(["gate"], env({ [key]: "false" }), {
            ...dependencies,
            get: async () => assert.fail("A disabled gate must not read remote metadata."),
            run: async () => assert.fail("A disabled gate must not run processes."),
        }), stageError(stage));
        assert.deepEqual(writes, []);
    }
    await main(["gate"], env(), dependencies);
    assert.deepEqual(writes, [["virtual-output", `environment=sidequest-staging\nenvironment-id=77\nsource-sha=${sha}\n`]]);
    assert.deepEqual(fake.events, []);
    await assert.rejects(main(["plan"], env({ ...environmentValues, PLAN_EXPECTED_ENVIRONMENT_ID: "78" }), dependencies), stageError("environment"));
    await assert.rejects(main(["plan"], env({ ...environmentValues, PLAN_EXPECTED_SOURCE_SHA: "b".repeat(40) }), dependencies), stageError("environment"));
    assert.deepEqual(fake.events, []);
    await main(["plan"], env(environmentValues), dependencies);
    assert.equal(writes[1][0], "virtual-summary");
    assert.equal(writes[2][0], "stdout");
    assert.equal(JSON.parse(writes[2][1]).enableApplication, false);
    assert.equal(JSON.stringify(writes).includes(fake.token), false);
    assert.equal(JSON.stringify(writes).includes("SECRET"), false);
    assert.equal(JSON.stringify(writes).includes(configuration.parametersJson), false);
});

test("real CLI help and invalid invocation fail safely before network or process planning", () => {
    const path = fileURLToPath(new URL("../plan-workflow.mjs", import.meta.url));
    for (const [args, status, expectedStage] of [
        [["--help"], 0, null], [[], 1, "usage"], [["gate", "SECRET"], 1, "usage"],
        [["SECRET"], 1, "usage"], [["plan"], 1, "context"],
    ]) {
        const result = spawnSync(process.execPath, [path, ...args], {
            encoding: "utf8", timeout: 5000, env: { ...process.env, PLAN_INPUTS: "SECRET" }, windowsHide: true,
        });
        assert.equal(result.error, undefined);
        assert.equal(result.status, status);
        if (expectedStage) {
            assert.equal(result.stdout, "");
            assert.equal(result.stderr, `${new PlanError(expectedStage).message}\n`);
        } else {
            assert.equal(result.stderr, "");
            assert.match(result.stdout, /owner assertion of manually verified administrator controls/);
        }
        assert.equal(result.stderr.includes("SECRET"), false);
    }
});
