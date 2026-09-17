import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import test from "node:test";

const templatePath = process.env.SIDEQUEST_BUDGET_ARM_TEMPLATE;
if (!templatePath) throw new Error("Compile budget-staging/sql.bicep and set SIDEQUEST_BUDGET_ARM_TEMPLATE.");
const template = JSON.parse(readFileSync(templatePath, "utf8"));
const resources = template.resources;
const deploymentScript = fileURLToPath(new URL("./deploy.ps1", import.meta.url)).replaceAll("'", "''");

function resource(type) {
    const matches = resources.filter(item => item.type === type);
    assert.equal(matches.length, 1, `Expected one ${type}.`);
    return matches[0];
}

function powershell(command) {
    const result = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command",
        `$ErrorActionPreference = 'Stop'; . '${deploymentScript}'; ${command}`], { encoding: "utf8" });
    assert.equal(result.error, undefined);
    return result;
}

test("SQL staging contains only Basic SQL, free identities and retention", () => {
    assert.deepEqual(resources.map(item => item.type).sort(), [
        "Microsoft.ManagedIdentity/userAssignedIdentities", "Microsoft.ManagedIdentity/userAssignedIdentities",
        "Microsoft.Sql/servers", "Microsoft.Sql/servers/databases",
        "Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies"
    ].sort());
    assert.equal(template.variables.location, "westus3");
    const database = resource("Microsoft.Sql/servers/databases");
    assert.deepEqual(database.sku, { name: "Basic", tier: "Basic", capacity: 5 });
    assert.equal(database.properties.maxSizeBytes, 2147483648);
    assert.equal(database.properties.requestedBackupStorageRedundancy, "Local");
    assert.equal(database.properties.zoneRedundant, false);
    assert.equal(resource("Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies").properties.retentionDays, 7);
    assert.deepEqual(resources.filter(item => item.type.startsWith("Microsoft.ManagedIdentity/")).map(item => item.name),
        ["sidequest-app", "sidequest-migration"]);
});

test("Public SQL requires Entra group authentication and TLS without any template firewall openings", () => {
    const sql = resource("Microsoft.Sql/servers").properties;
    assert.equal(sql.publicNetworkAccess, "Enabled");
    assert.equal(sql.minimalTlsVersion, "1.2");
    assert.equal(sql.restrictOutboundNetworkAccess, "Enabled");
    assert.deepEqual(sql.administrators, {
        administratorType: "ActiveDirectory", azureADOnlyAuthentication: true, principalType: "Group",
        login: "[parameters('sqlAdministratorGroupName')]",
        sid: "[parameters('sqlAdministratorGroupObjectId')]",
        tenantId: "[subscription().tenantId]"
    });
    assert.equal(Object.hasOwn(sql, "administratorLoginPassword"), false);
    assert.equal(Object.hasOwn(sql, "administratorLogin"), false);
    assert.equal(resources.some(item => item.type.endsWith("/firewallRules")), false);
});

const validAccount = {
    subscriptionId: "b75472bd-4174-4f66-b159-bae420212abc",
    tenantId: "99e674a6-6773-4f53-90a3-e3ab8c37c856",
    state: "Enabled",
    subscriptionPolicies: { spendingLimit: "On", quotaId: "MSDN_2014-09-01" }
};

test("Subscription guard accepts only the approved enabled dev-test subscription with its spending limit", () => {
    const result = powershell(`Assert-SidequestStagingSubscription ('${JSON.stringify(validAccount)}' | ConvertFrom-Json)`);
    assert.equal(result.status, 0, result.stderr);
});

for (const [name, account] of [
    ["another subscription", { ...validAccount, subscriptionId: "00000000-0000-0000-0000-000000000001" }],
    ["another tenant", { ...validAccount, tenantId: "00000000-0000-0000-0000-000000000001" }],
    ["a disabled subscription", { ...validAccount, state: "Disabled" }],
    ["a removed spending limit", { ...validAccount, subscriptionPolicies: { ...validAccount.subscriptionPolicies, spendingLimit: "Off" } }],
    ["an unapproved subscription offer", { ...validAccount, subscriptionPolicies: { ...validAccount.subscriptionPolicies, quotaId: "PayAsYouGo" } }],
    ["missing subscription data", {}]
]) {
    test(`Subscription guard rejects ${name} before cloud mutation`, () => {
        const result = powershell(`Assert-SidequestStagingSubscription ('${JSON.stringify(account)}' | ConvertFrom-Json)`);
        assert.notEqual(result.status, 0);
        assert.ok(result.stderr.length > 0);
    });
}

test("Azure adapter explicitly supplies the approved subscription independent of CLI defaults", () => {
    const result = powershell(`
        function az {
            $script:captured = $args
            $global:LASTEXITCODE = 0
            '{"value":42}'
        }
        $response = Invoke-SidequestStagingAzure @('group', 'exists', '--name', 'sidequest-rg')
        if ($response.value -ne 42) { throw 'JSON result lost' }
        if (($script:captured -join '|') -cne 'group|exists|--name|sidequest-rg|--subscription|b75472bd-4174-4f66-b159-bae420212abc|--only-show-errors|--output|json') {
            throw 'Unexpected Azure command scope'
        }`);
    assert.equal(result.status, 0, result.stderr);
});

test("Azure adapter fails explicitly on CLI errors instead of returning success-shaped data", () => {
    const result = powershell(`
        function az { $global:LASTEXITCODE = 1; '{"value":"invalid"}' }
        Invoke-SidequestStagingAzure @('group', 'exists', '--name', 'sidequest-rg')`);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /explicitly scoped Azure operation failed/);
});
