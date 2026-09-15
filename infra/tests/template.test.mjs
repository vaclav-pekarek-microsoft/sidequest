import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const path = process.env.SIDEQUEST_ARM_TEMPLATE;
if (!path) throw new Error("Compile infra/main.bicep and set SIDEQUEST_ARM_TEMPLATE to its output before running these checks.");
const template = JSON.parse(readFileSync(path, "utf8"));
const resources = template.resources;
assert.ok(Array.isArray(resources), "The compiled template must contain ARM resource declarations.");

function resource(type) {
    const matches = resources.filter(item => item.type === type);
    assert.equal(matches.length, 1, `Expected exactly one ${type} declaration.`);
    return matches[0];
}

test("Application starts disabled with one Linux instance, secure transport and circuit support", () => {
    assert.equal(template.parameters.enableApplication.defaultValue, false);
    const plan = resource("Microsoft.Web/serverfarms");
    assert.equal(plan.sku.capacity, 1);
    assert.equal(plan.properties.reserved, true);
    const app = resource("Microsoft.Web/sites");
    assert.equal(app.properties.enabled, "[parameters('enableApplication')]");
    assert.equal(app.properties.httpsOnly, true);
    assert.equal(app.properties.clientAffinityEnabled, true);
    assert.equal(app.identity.type, "SystemAssigned");
    const config = app.properties.siteConfig;
    assert.equal(config.linuxFxVersion, "DOTNETCORE|10.0");
    assert.equal(config.alwaysOn, true);
    assert.equal(config.webSocketsEnabled, true);
    assert.equal(config.vnetRouteAllEnabled, true);
    assert.equal(config.healthCheckPath, "/health/ready");
    assert.equal(config.minTlsVersion, "1.2");
    assert.equal(config.scmMinTlsVersion, "1.2");
    assert.equal(config.ftpsState, "Disabled");
    const publishing = resources.filter(item => item.type === "Microsoft.Web/sites/basicPublishingCredentialsPolicies");
    assert.equal(publishing.length, 2);
    assert.ok(publishing.every(item => item.properties.allow === false));
});

test("Storage is private, keyless and configured for caller-approved recovery rather than content deletion", () => {
    const storage = resource("Microsoft.Storage/storageAccounts").properties;
    assert.equal(storage.publicNetworkAccess, "Disabled");
    assert.equal(storage.allowBlobPublicAccess, false);
    assert.equal(storage.allowSharedKeyAccess, false);
    assert.equal(storage.supportsHttpsTrafficOnly, true);
    assert.equal(storage.minimumTlsVersion, "TLS1_2");
    assert.deepEqual(storage.networkAcls, { defaultAction: "Deny", bypass: "None" });
    assert.equal(storage.encryption.requireInfrastructureEncryption, true);
    const blobs = resource("Microsoft.Storage/storageAccounts/blobServices").properties;
    assert.equal(blobs.isVersioningEnabled, true);
    assert.equal(blobs.changeFeed.enabled, true);
    assert.deepEqual(blobs.restorePolicy, { enabled: true, days: "[parameters('blobRestoreDays')]" });
    assert.deepEqual(blobs.deleteRetentionPolicy, { enabled: true, days: "[add(parameters('blobRestoreDays'), 1)]" });
    const containers = resources.filter(item => item.type === "Microsoft.Storage/storageAccounts/blobServices/containers");
    assert.equal(containers.length, 2);
    assert.ok(containers.every(item => item.properties.publicAccess === "None"));
    assert.equal(resources.filter(item => item.type.endsWith("/managementPolicies")).length, 0);
});

test("SQL has no public ingress or password administrator and uses an approved Entra group", () => {
    const sql = resource("Microsoft.Sql/servers").properties;
    assert.equal(sql.publicNetworkAccess, "Disabled");
    assert.equal(sql.minimalTlsVersion, "1.2");
    assert.equal(sql.administrators.azureADOnlyAuthentication, true);
    assert.equal(sql.administrators.principalType, "Group");
    assert.equal(sql.administrators.sid, "[parameters('sqlAdministratorGroupObjectId')]");
    assert.equal(Object.hasOwn(sql, "administratorLoginPassword"), false);
    assert.equal(Object.hasOwn(sql, "administratorLogin"), false);
    assert.equal(resources.filter(item => item.type.endsWith("/firewallRules")).length, 0);
    assert.equal(resource("Microsoft.Sql/servers/databases").properties.requestedBackupStorageRedundancy, "Zone");
    assert.equal(resource("Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies").properties.retentionDays,
        "[parameters('sqlPointInTimeRetentionDays')]");
});

test("Vault and cryptographic material are private, RBAC-protected and purge-protected", () => {
    const vault = resource("Microsoft.KeyVault/vaults").properties;
    assert.equal(vault.publicNetworkAccess, "Disabled");
    assert.equal(vault.enableRbacAuthorization, true);
    assert.equal(vault.enablePurgeProtection, true);
    assert.equal(vault.enableSoftDelete, true);
    assert.deepEqual(vault.networkAcls, { defaultAction: "Deny", bypass: "None" });
    const key = resource("Microsoft.KeyVault/vaults/keys").properties;
    assert.equal(key.kty, "RSA");
    assert.equal(key.keySize, 3072);
    assert.deepEqual(key.keyOps, ["wrapKey", "unwrapKey"]);
    assert.equal(resources.filter(item => item.type === "Microsoft.KeyVault/vaults/secrets").length, 0);
    assert.equal(resource("Microsoft.Insights/components").properties.DisableLocalAuth, true);
});

test("Network wiring separates delegated application traffic from private data endpoints", () => {
    const subnets = resource("Microsoft.Network/virtualNetworks").properties.subnets;
    assert.equal(subnets.length, 2);
    assert.equal(subnets[0].name, "application");
    assert.equal(subnets[0].properties.delegations[0].properties.serviceName, "Microsoft.Web/serverFarms");
    assert.ok(subnets[0].properties.natGateway.id.includes("Microsoft.Network/natGateways"));
    assert.equal(subnets[1].name, "private-endpoints");
    const services = template.variables.privateServices;
    assert.deepEqual(services.map(service => service.groupId), ["blob", "sqlServer", "vault"]);
    assert.equal(resource("Microsoft.Network/privateEndpoints").copy.count, "[length(variables('privateServices'))]");
    assert.equal(resource("Microsoft.Network/privateEndpoints/privateDnsZoneGroups").copy.count, "[length(variables('privateServices'))]");
    assert.equal(resource("Microsoft.Network/privateDnsZones/virtualNetworkLinks").properties.registrationEnabled, false);
});

test("Deployment identities and approved retention or address ranges have no invented defaults", () => {
    for (const name of [
        "location", "operationalOwner", "virtualNetworkAddressPrefix", "applicationSubnetAddressPrefix",
        "privateEndpointSubnetAddressPrefix", "workforceTenantId", "workforceClientId", "workforceRole",
        "bootstrapAdministratorObjectId", "sqlAdministratorGroupName", "sqlAdministratorGroupObjectId",
        "blobRestoreDays", "sqlPointInTimeRetentionDays", "logRetentionDays"
    ]) {
        assert.ok(Object.hasOwn(template.parameters, name), `Missing required parameter ${name}.`);
        assert.equal(Object.hasOwn(template.parameters[name], "defaultValue"), false, `${name} must be explicitly approved.`);
    }
    const settings = resource("Microsoft.Web/sites/config").properties;
    assert.equal(settings.Authentication__Mode, "Entra");
    assert.equal(settings.ASPNETCORE_ENVIRONMENT, "Production");
    assert.ok(settings.AzureAd__ClientSecret.includes("@Microsoft.KeyVault(SecretUri="));
    assert.ok(settings.ConnectionStrings__Sidequest.includes("Authentication=Active Directory Managed Identity;"));
    assert.ok(settings.ConnectionStrings__Sidequest.includes("Encrypt=True;TrustServerCertificate=False;"));
    assert.equal(settings.Media__Storage__ContainerName, "covers");
    assert.equal(settings.Hosting__Azure__Enabled, "true");
    assert.equal(settings.APPLICATIONINSIGHTS_STATSBEAT_DISABLED, "true");
    assert.equal(settings.APPLICATIONINSIGHTS_SDKSTATS_DISABLED, "true");
    assert.equal(settings.Operations__Monitoring__Enabled, "true");
});
