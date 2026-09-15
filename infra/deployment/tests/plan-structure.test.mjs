import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { BICEP_SHA256, BICEP_URL } from "../plan-workflow.mjs";

const workflow = readFileSync(new URL("../../../.github/workflows/infrastructure-plan.yml", import.meta.url), "utf8").replaceAll("\r\n", "\n");
const existing = readFileSync(new URL("../../../.github/workflows/infrastructure.yml", import.meta.url), "utf8");
const source = readFileSync(new URL("../plan-workflow.mjs", import.meta.url), "utf8");
const io = readFileSync(new URL("../plan-io.mjs", import.meta.url), "utf8");
const jobs = workflow.slice(workflow.indexOf("\njobs:\n"));
const metadata = jobs.slice(jobs.indexOf("  metadata:\n"), jobs.indexOf("\n  plan:\n"));
const plan = jobs.slice(jobs.indexOf("  plan:\n"));

test("actual workflow trigger schema is manual-only with exactly the two environment choices", () => {
    assert.equal(workflow.slice(workflow.indexOf("\non:\n"), workflow.indexOf("\npermissions:")), `
on:
  workflow_dispatch:
    inputs:
      environment:
        description: Existing owner-approved environment to plan
        required: true
        type: choice
        options: [staging, production]
`);
    assert.match(workflow, /^permissions: \{\}$/m);
    assert.doesNotMatch(workflow, /^\s*(?:pull_request|push|workflow_call|schedule|workflow_run):/m);
    assert.deepEqual([...jobs.matchAll(/^  ([a-z-]+):$/gm)].map(match => match[1]), ["metadata", "plan"]);
});

test("actual metadata job cannot associate an environment or obtain OIDC and rejects before checkout", () => {
    assert.doesNotMatch(metadata, /^    environment:/m);
    assert.doesNotMatch(metadata, /id-token:|ACTIONS_ID_TOKEN_REQUEST|secrets\./);
    assert.match(metadata, /permissions:\n      contents: read\n      actions: read\n      deployments: read/);
    assert.ok(metadata.indexOf("if [[") < metadata.indexOf("actions/checkout@"));
    for (const guard of [
        '"$GITHUB_REPOSITORY" != "vaclav-pekarek-microsoft/sidequest"',
        '"$GITHUB_EVENT_NAME" != "workflow_dispatch"', '"$GITHUB_REF" != "refs/heads/main"',
        '"$GITHUB_RUN_ATTEMPT" != "1"', '"$GITHUB_WORKFLOW_SHA" != "$GITHUB_SHA"',
        '"$GITHUB_WORKFLOW_REF" != "vaclav-pekarek-microsoft/sidequest/.github/workflows/infrastructure-plan.yml@refs/heads/main"',
    ]) assert.ok(metadata.includes(guard), guard);
    assert.match(metadata, /exit 1/);
    assert.match(metadata, /PLAN_ENABLED: \$\{\{ vars\.SIDEQUEST_PLANNING_ENABLED \}\}/);
    assert.match(metadata, /PLAN_ADMIN_CONTROLS_VERIFIED: \$\{\{ vars\.SIDEQUEST_PLANNING_ADMIN_CONTROLS_VERIFIED \}\}/);
    assert.match(metadata, /run: node infra\/deployment\/plan-workflow\.mjs gate/);
});

test("actual protected job depends on a successful gate with the exact checked-out source and environment identity", () => {
    assert.match(plan, /^    needs: metadata$/m);
    assert.match(plan, /^    if: \$\{\{ needs\.metadata\.result == 'success' \}\}$/m);
    assert.match(plan, /^    environment:\n      name: \$\{\{ needs\.metadata\.outputs\.environment \}\}$/m);
    assert.match(plan, /^      id-token: write$/m);
    assert.equal((workflow.match(/id-token: write/g) ?? []).length, 1);
    assert.match(plan, /PLAN_EXPECTED_ENVIRONMENT_ID: \$\{\{ needs\.metadata\.outputs\.environment-id \}\}/);
    assert.match(plan, /PLAN_EXPECTED_SOURCE_SHA: \$\{\{ needs\.metadata\.outputs\.source-sha \}\}/);
    assert.match(plan, /run: node infra\/deployment\/plan-workflow\.mjs plan/);
    assert.doesNotMatch(workflow, /continue-on-error|always\(\)|failure\(\)|azure\/login|upload-artifact|AZURE_CLIENT_SECRET/);
    assert.match(workflow, /cancel-in-progress: false/);
    for (const job of [metadata, plan]) {
        assert.match(job, /ref: \$\{\{ github\.sha \}\}/);
        assert.match(job, /persist-credentials: false/);
        assert.match(job, /runs-on: ubuntu-24\.04/);
        assert.match(job, /timeout-minutes: [1-9][0-9]*/);
    }
});

test("actual workflow pins actions and compiler and never interpolates parameters into shell commands", () => {
    assert.deepEqual([...workflow.matchAll(/uses: ([^\s#]+)/g)].map(match => match[1]), [
        "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
        "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
    ]);
    assert.ok(existing.includes(BICEP_URL));
    assert.ok(existing.includes(BICEP_SHA256));
    assert.ok(existing.includes("node --test infra/deployment/tests/*.test.mjs"));
    for (const name of ["AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID", "AZURE_CLIENT_ID", "AZURE_RESOURCE_GROUP", "SIDEQUEST_PLAN_PARAMETERS"]) {
        assert.ok(plan.includes(`secrets.${name}`));
    }
    assert.doesNotMatch(workflow, /PLAN_INPUTS:|toJSON\(inputs\)|vars\.SIDEQUEST_PLAN_PARAMETERS/);
    assert.match(source, /open\(runtime\.GITHUB_EVENT_PATH, "r"\)/);
    assert.doesNotMatch(workflow, /run:.*\$\{\{/);
    assert.doesNotMatch(workflow, /\baz\b|deployment group create|group create|--confirm-with-what-if|--proceed-if-no-change/);
    assert.match(io, /shell: false/);
    assert.doesNotMatch(source, /"deployment", "group", "(?:create|delete|cancel)"|"group", "(?:create|delete|update)"|"ad",|"role",|\/approvals|can_admins_bypass/);
    assert.match(source, /"deployment", "group", "validate"/);
    assert.match(source, /"deployment", "group", "what-if"/);
    assert.match(source, /"ResourceIdOnly"/);
    assert.match(source, /AZURE_CONFIG_DIR: join\(directory, "azure"\)/);
    assert.match(source, /AZURE_EXTENSION_USE_DYNAMIC_INSTALL: "no"/);
});
