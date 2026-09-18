import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import test from "node:test";

const templatePath = process.env.SIDEQUEST_APPLICATION_ARM_TEMPLATE;
if (!templatePath) throw new Error("Compile application.bicep with Bicep 0.47.16 and set SIDEQUEST_APPLICATION_ARM_TEMPLATE.");
const template = JSON.parse(readFileSync(templatePath, "utf8"));
const script = fileURLToPath(new URL("./identity.ps1", import.meta.url)).replaceAll("'", "''");
const resources = template.resources;
const one = type => {
    const matches = resources.filter(resource => resource.type === type);
    assert.equal(matches.length, 1, type);
    return matches[0];
};
const ps = command => spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command",
    `$ErrorActionPreference='Stop'; . '${script}'; ${command}`], { encoding: "utf8" });

test("Compiled application is disabled B1 Linux with two identities and protected stateful hosting", () => {
    assert.equal(template.variables.location, "westus3");
    assert.deepEqual(one("Microsoft.Web/serverfarms").sku, { name: "B1", tier: "Basic", capacity: 1 });
    const site = one("Microsoft.Web/sites");
    assert.equal(site.properties.enabled, false);
    assert.equal(site.identity.type, "SystemAssigned, UserAssigned");
    assert.match(JSON.stringify(site.identity.userAssignedIdentities), /sidequest-app/);
    assert.equal(site.properties.httpsOnly, true);
    assert.equal(site.properties.clientAffinityEnabled, true);
    assert.equal(site.properties.siteConfig.alwaysOn, true);
    assert.equal(site.properties.siteConfig.webSocketsEnabled, true);
    assert.equal(site.properties.siteConfig.minTlsVersion, "1.2");
    assert.equal(site.properties.siteConfig.ftpsState, "Disabled");
    assert.equal(site.properties.siteConfig.appCommandLine, "dotnet /home/site/wwwroot/Sidequest.Web.dll");
    assert.equal(resources.some(r => r.type.startsWith("Microsoft.Sql/") || r.type.startsWith("Microsoft.Network/")), false);
    assert.ok(resources.filter(r => r.type.endsWith("/basicPublishingCredentialsPolicies")).every(r => r.properties.allow === false));
});

test("Compiled settings require single-tenant owner-only Entra and SQL client-ID authentication", () => {
    const settings = one("Microsoft.Web/sites/config").properties;
    assert.equal(settings.ASPNETCORE_ENVIRONMENT, "Staging");
    assert.equal(settings.Authentication__Mode, "Entra");
    assert.equal(settings.WEBSITE_RUN_FROM_PACKAGE, "1");
    assert.equal(settings.SCM_DO_BUILD_DURING_DEPLOYMENT, "false");
    assert.equal(settings.Authentication__AdmissionPolicy, "hackathon-assigned-users");
    assert.equal(settings.Authentication__HackathonParticipants__0, "[parameters('ownerObjectId')]");
    assert.deepEqual(template.parameters.ownerObjectId.allowedValues, ["1250fe10-b814-4735-801f-ea5a0a4c1219"]);
    assert.equal(settings.AzureAd__TenantId, "[variables('tenantId')]");
    assert.equal(template.variables.tenantId, "99e674a6-6773-4f53-90a3-e3ab8c37c856");
    assert.match(settings.ConnectionStrings__Sidequest, /Authentication=Active Directory Managed Identity;User Id=/);
    assert.match(settings.ConnectionStrings__Sidequest, /reference\(resourceId\('Microsoft.ManagedIdentity\/userAssignedIdentities', 'sidequest-app'\), '2023-01-31'\)\.clientId/);
    assert.match(settings.ConnectionStrings__Sidequest, /Encrypt=True;TrustServerCertificate=False/);
    assert.match(settings.AzureAd__ClientSecret, /@Microsoft.KeyVault/);
    assert.equal(settings.AzureAd__ClientSecret, settings.Directory__Credentials__ClientSecret);
    assert.equal(Object.keys(settings).some(key => key.includes("WorkforcePolicyApproved")), false);
});

test("LRS blobs remain private and key wrapping grants have resource-level scopes", () => {
    const storage = one("Microsoft.Storage/storageAccounts");
    assert.equal(storage.sku.name, "Standard_LRS");
    assert.equal(storage.properties.allowBlobPublicAccess, false);
    assert.equal(storage.properties.allowSharedKeyAccess, false);
    assert.equal(storage.properties.supportsHttpsTrafficOnly, true);
    assert.equal(storage.properties.publicNetworkAccess, "Enabled");
    const containers = resources.filter(r => r.type.endsWith("/containers"));
    assert.equal(containers.length, 2);
    assert.ok(containers.every(r => r.properties.publicAccess === "None"));
    const vault = one("Microsoft.KeyVault/vaults");
    assert.equal(vault.properties.enableRbacAuthorization, true);
    assert.equal(vault.properties.enablePurgeProtection, true);
    assert.deepEqual(one("Microsoft.KeyVault/vaults/keys").properties.keyOps, ["wrapKey", "unwrapKey"]);
    assert.ok(resources.filter(r => r.type.endsWith("/roleAssignments")).every(r => typeof r.scope === "string"));
});

test("ACS uses Azure-managed domain, no tracking and only documented read-write authorization", () => {
    const domain = one("Microsoft.Communication/emailServices/domains");
    assert.equal(domain.properties.domainManagement, "AzureManaged");
    assert.equal(domain.properties.userEngagementTracking, "Disabled");
    assert.equal(domain.location, "global");
    const acs = one("Microsoft.Communication/communicationServices");
    assert.equal(acs.properties.dataLocation, "United States");
    assert.equal(acs.properties.linkedDomains.length, 1);
    assert.deepEqual(one("Microsoft.Authorization/roleDefinitions").properties.permissions, [{
        actions: ["Microsoft.Communication/CommunicationServices/Read", "Microsoft.Communication/CommunicationServices/Write"],
        notActions: [], dataActions: [], notDataActions: []
    }]);
    const settings = one("Microsoft.Web/sites/config").properties;
    assert.match(settings.Delivery__Email__SenderAddress, /DoNotReply@/);
    assert.equal(Object.hasOwn(settings, "Delivery__Email__ConnectionString"), false);
});

test("Metrics retain privacy opt-outs, MI authentication, bounded daily quota and short retention", () => {
    const workspace = one("Microsoft.OperationalInsights/workspaces").properties;
    assert.equal(workspace.retentionInDays, 30);
    assert.equal(workspace.workspaceCapping.dailyQuotaGb, "[json('0.1')]");
    assert.equal(one("Microsoft.Insights/components").properties.DisableLocalAuth, true);
    const settings = one("Microsoft.Web/sites/config").properties;
    assert.equal(settings.APPLICATIONINSIGHTS_STATSBEAT_DISABLED, "true");
    assert.equal(settings.APPLICATIONINSIGHTS_SDKSTATS_DISABLED, "true");
});

const site = addresses => JSON.stringify({
    location: "West US 3", kind: "app,linux", tags: { application: "Sidequest" },
    properties: { outboundIpAddresses: addresses, possibleOutboundIpAddresses: "1.1.1.1" }
});
test("Firewall guard selects actual observed public IPv4s, deduplicates, and ignores possible addresses", () => {
    const result = ps(`$addresses = @(Get-SidequestVerifiedOutboundAddresses ('${site("20.3.2.1,20.3.2.2,20.3.2.1")}' | ConvertFrom-Json)); if (($addresses -join ',') -cne '20.3.2.1,20.3.2.2') { throw 'Wrong actual egress set' }`);
    assert.equal(result.status, 0, result.stderr);
});
for (const value of ["", "0.0.0.0", "10.0.0.1", "127.0.0.1", "172.16.0.1", "192.168.0.1", "169.254.1.1", "255.255.255.255", "::1", "20.3.2.1/24", "20.3.2.1-20.3.2.5"]) {
    test(`Firewall guard rejects ${JSON.stringify(value)} without Azure calls`, () => {
        const result = ps(`Get-SidequestVerifiedOutboundAddresses ('${site(value)}' | ConvertFrom-Json)`);
        assert.notEqual(result.status, 0);
        assert.ok(result.stderr.length > 0);
    });
}
test("Publishing and permission consent cannot omit explicit owner acknowledgements", () => {
    for (const command of [
        "Publish-SidequestApplication -SourceCommit fake -ApplicationName fake -ZipPath absent -ManifestPath absent -IdentityAndProviderGatesVerified:$false",
        "New-SidequestHackathonRegistration -SourceCommit fake -ApplicationUrl https://example.invalid -ReadOnlyGraphConsentApproved:$false"
    ]) {
        const result = ps(`function az { throw 'Azure must not be contacted' }; function gh { throw 'GitHub must not be contacted' }; ${command}`);
        assert.notEqual(result.status, 0);
        assert.doesNotMatch(result.stderr, /must not be contacted/);
        assert.match(result.stderr, /required/);
    }
});
test("Credential HTTP guard refuses cross-host and insecure targets before token acquisition", () => {
    for (const url of ["http://graph.microsoft.com/v1.0/applications", "https://other.invalid/v1.0/applications", "https://graph.microsoft.com:444/v1.0/applications"]) {
        const result = ps(`function az { throw 'Azure must not be contacted' }; Invoke-SidequestPrivateRequest POST '${url}' 'https://graph.microsoft.com/' @{value='synthetic-test'}`);
        assert.notEqual(result.status, 0);
        assert.match(result.stderr, /exact approved native endpoint/);
        assert.doesNotMatch(result.stderr, /Azure must not be contacted/);
    }
});

test("Registration includes only the accepted localhost HTTPS sign-in and sign-out callbacks", () => {
    const result = ps("Get-SidequestLocalRedirectUris | ConvertTo-Json -Compress");
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(result.stdout), [
        "https://localhost:7193/signin-oidc",
        "https://localhost:7193/signout-callback-oidc"
    ]);
});

test("Registration enables the existing ID-token form-post flow without implicit access tokens", () => {
    const result = ps(`
        function Assert-SidequestApplicationSource { }
        function az { throw 'Azure must not be contacted' }
        function gh { throw 'GitHub must not be contacted' }
        $script:created = $null
        function Invoke-SidequestPrivateRequest {
            param($Method, [uri] $Uri, $Audience, $Body)
            if ($Method -ceq 'GET' -and $Uri.AbsolutePath -ceq '/v1.0/me') {
                return @{ id = '1250fe10-b814-4735-801f-ea5a0a4c1219' }
            }
            if ($Method -ceq 'GET' -and $Uri.AbsolutePath -ceq '/v1.0/applications') {
                return @{ value = @() }
            }
            if ($Method -ceq 'GET' -and $Uri.AbsolutePath -ceq '/v1.0/servicePrincipals') {
                return @{ value = @(@{ id = 'graph'; appRoles = @(
                    @{ id = 'user-read'; value = 'User.Read.All'; isEnabled = $true; allowedMemberTypes = @('Application') },
                    @{ id = 'group-read'; value = 'GroupMember.Read.All'; isEnabled = $true; allowedMemberTypes = @('Application') }
                ) }) }
            }
            if ($Method -ceq 'POST' -and $Uri.AbsolutePath -ceq '/v1.0/applications') {
                $script:created = $Body
                return @{ appId = 'client'; id = 'application' }
            }
            if ($Method -ceq 'POST' -and $Uri.AbsolutePath -ceq '/v1.0/servicePrincipals') {
                if ($Body.appRoleAssignmentRequired -ne $true) { throw 'Assignment is required' }
                return @{ id = 'service' }
            }
            if ($Method -ceq 'POST' -and $Uri.AbsolutePath -match '^/v1.0/servicePrincipals/service/appRoleAssign') {
                return @{}
            }
            throw 'Unexpected identity operation'
        }
        $null = New-SidequestHackathonRegistration -SourceCommit accepted-source -ApplicationUrl https://sidequest-hackathon-b7ljjkoqcaedc.azurewebsites.net/ -ReadOnlyGraphConsentApproved
        if ($script:created.signInAudience -cne 'AzureADMyOrg' -or
            $script:created.web.implicitGrantSettings.enableIdTokenIssuance -ne $true -or
            $script:created.web.implicitGrantSettings.enableAccessTokenIssuance -ne $false) {
            throw 'Registration does not match the ID-token-only sign-in contract'
        }
    `);
    assert.equal(result.status, 0, result.stderr);
});

const requestFailures = [
    ["HTTP 503", `
        $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::ServiceUnavailable)
        $response.Content = [Net.Http.StringContent]::new('private-body-sentinel')
        throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('private-message-sentinel', $response)
    `, "category=http; status=503"],
    ["HTTP 403", `
        throw [Net.Http.HttpRequestException]::new('private-message-sentinel', $null, [Net.HttpStatusCode]::Forbidden)
    `, "category=http; status=403"],
    ["network", "throw [Net.Http.HttpRequestException]::new('private-message-sentinel')", "category=network"],
    ["timeout", "throw [Threading.Tasks.TaskCanceledException]::new('private-message-sentinel')", "category=timeout"],
    ["web timeout", "throw [Net.WebException]::new('private-message-sentinel', [Net.WebExceptionStatus]::Timeout)", "category=timeout"]
];

const publishHarness = `
    function az { throw 'Live Azure must not be contacted' }
    function Assert-SidequestApplicationSource { }
    function Get-Content { '{"sourceCommit":"accepted-source","sha256":"verified-hash"}' }
    function Get-FileHash { [pscustomobject]@{ Hash = 'verified-hash' } }
    $script:stops = 0
    $script:attempts = 0
    $script:sleeps = 0
    $script:deploys = 0
    $script:lifecycle = [Collections.Generic.List[string]]::new()
    $script:deploymentFails = $false
    $script:restartFails = $false
    $script:packageMode = '1'
    $script:startupCommand = 'dotnet /home/site/wwwroot/Sidequest.Web.dll'
    $script:referenceResponse = [pscustomobject]@{
        nextLink = $null
        value = @(
            @{ name = 'AzureAd__ClientSecret'; properties = @{ status = 'Resolved' } }
            @{ name = 'Directory__Credentials__ClientSecret'; properties = @{ status = 'Resolved' } }
        )
    }
    function Start-Sleep { $script:sleeps++ }
    function Invoke-SidequestStagingAzure {
        param([string[]] $Arguments)
        if ($Arguments -contains 'appsettings') {
            return @(@{ name = 'WEBSITE_RUN_FROM_PACKAGE'; value = $script:packageMode })
        }
        if ($Arguments -contains 'show') {
            return @{ appCommandLine = $script:startupCommand }
        }
        if ($Arguments -contains 'stop') { $script:lifecycle.Add('stop'); $script:stops++; return }
        if ($Arguments -contains 'properties.enabled=true') { $script:lifecycle.Add('enable'); return }
        if ($Arguments -contains 'start') { $script:lifecycle.Add('start'); return }
        if ($Arguments -contains 'restart') {
            $script:lifecycle.Add('restart')
            if ($script:restartFails) { throw [InvalidOperationException]::new('restart-failed-sentinel') }
            return
        }
        if ($Arguments -contains 'deploy') {
            $script:lifecycle.Add('deploy')
            $script:deploys++
            if (($Arguments -join ' ') -notmatch '--restart false --track-status false') {
                throw 'Deployment must leave explicit restart and bounded readiness to the application helper'
            }
            if ($script:deploymentFails) { throw [InvalidOperationException]::new('deployment-failed-sentinel') }
            return
        }
        if (($Arguments -join ' ') -match '/config/configreferences/') {
            if ($Arguments -notcontains 'get' -or $Arguments -notcontains
                'https://management.azure.com/subscriptions/b75472bd-4174-4f66-b159-bae420212abc/resourceGroups/sidequest-rg/providers/Microsoft.Web/sites/sidequest-hackathon-b7ljjkoqcaedc/config/configreferences/appsettings?api-version=2022-03-01') {
                throw 'Expected the live GET reference collection contract'
            }
            return $script:referenceResponse
        }
        if ($Arguments -contains 'get') {
            return [pscustomobject]@{ properties = @{ defaultHostName = 'sidequest-hackathon-b7ljjkoqcaedc.azurewebsites.net' } }
        }
    }
`;
const publishInvocation = "Publish-SidequestApplication -SourceCommit accepted-source -ApplicationName sidequest-hackathon-b7ljjkoqcaedc -ZipPath unused -ManifestPath unused -IdentityAndProviderGatesVerified";

for (const [name, mutation] of [
    ["mutable content", "$script:packageMode = '0'"],
    ["remote package", "$script:packageMode = 'https://example.invalid/package.zip'"],
    ["write-dependent startup", "$script:startupCommand = 'chmod +x /home/site/wwwroot/Sidequest.Web'"]
]) {
    test(`Publishing rejects ${name} before activation or upload`, () => {
        const result = ps(`${publishHarness}
            ${mutation}
            $caught = $false
            try { $null = ${publishInvocation} }
            catch {
                if ($_.Exception.Message -notmatch 'Immutable local-package hosting') { throw }
                $caught = $true
            }
            if (-not $caught -or $script:lifecycle.Count -ne 0) {
                throw 'Mutable or incompatible hosting reached deployment'
            }
        `);
        assert.equal(result.status, 0, result.stderr);
    });
}

test("Publishing enables SCM before upload and restarts only the validated artifact", () => {
    const result = ps(`${publishHarness}
        function Invoke-WebRequest { [pscustomobject]@{ StatusCode = 200 } }
        $result = ${publishInvocation}
        if (($script:lifecycle -join ',') -cne 'enable,start,deploy,restart' -or $result.readiness -cne 'Healthy') {
            throw 'Incorrect stopped-site deployment sequence'
        }
    `);
    assert.equal(result.status, 0, result.stderr);
});

test("Deployment failure after enabling SCM stops the host before readiness probing", () => {
    const result = ps(`${publishHarness}
        $script:deploymentFails = $true
        $caught = $false
        try { $null = ${publishInvocation} }
        catch {
            if ($_.Exception.Message -cne 'deployment-failed-sentinel') { throw }
            $caught = $true
        }
        if (-not $caught -or ($script:lifecycle -join ',') -cne 'enable,start,deploy,stop' -or
            $script:stops -ne 1 -or $script:attempts -ne 0) {
            throw 'Failed deployment did not close the activated hosting boundary'
        }
    `);
    assert.equal(result.status, 0, result.stderr);
});

test("Explicit restart failure stops the host before old-process readiness can be accepted", () => {
    const result = ps(`${publishHarness}
        $script:restartFails = $true
        $caught = $false
        try { $null = ${publishInvocation} }
        catch {
            if ($_.Exception.Message -cne 'restart-failed-sentinel') { throw }
            $caught = $true
        }
        if (-not $caught -or ($script:lifecycle -join ',') -cne 'enable,start,deploy,restart,stop' -or
            $script:stops -ne 1 -or $script:attempts -ne 0) {
            throw 'Failed restart reached readiness or left hosting enabled'
        }
    `);
    assert.equal(result.status, 0, result.stderr);
});

for (const [name, mutation] of [
    ["missing", "$script:referenceResponse.value = @($script:referenceResponse.value[0])"],
    ["duplicate", "$script:referenceResponse.value += $script:referenceResponse.value[0]"],
    ["unresolved", "$script:referenceResponse.value[1].properties.status = 'AccessToKeyVaultDenied'"],
    ["incomplete", "$script:referenceResponse.nextLink = 'https://management.azure.com/next'"]
]) {
    test(`Publishing rejects ${name} credential reference collections before deployment`, () => {
        const result = ps(`${publishHarness}
            ${mutation}
            $caught = $false
            try { $null = ${publishInvocation} }
            catch {
                if ($_.Exception.Message -notmatch 'credential reference') { throw }
                $caught = $true
            }
            if (-not $caught -or $script:deploys -ne 0 -or $script:attempts -ne 0 -or $script:lifecycle.Count -ne 0) {
                throw 'Invalid references reached deployment or activation'
            }
        `);
        assert.equal(result.status, 0, result.stderr);
    });
}

for (const [name, failure, category] of requestFailures) {
    test(`Readiness retries expected ${name} with bounded diagnostics and no response body`, () => {
        const result = ps(`${publishHarness}
            function Invoke-WebRequest {
                $script:attempts++
                if ($script:attempts -eq 1) { ${failure} }
                return [pscustomobject]@{ StatusCode = 200 }
            }
            $result = ${publishInvocation}
            if ($result.readiness -cne 'Healthy' -or $script:attempts -ne 2 -or $script:sleeps -ne 1 -or $script:stops -ne 0) {
                throw 'Wrong readiness retry or host lifecycle'
            }
        `);
        assert.equal(result.status, 0, result.stderr);
        assert.ok(result.stdout.includes(`Readiness attempt 1/18: ${category}.`), result.stdout);
        assert.doesNotMatch(result.stdout + result.stderr, /private-(body|message)-sentinel/);
    });
}

test("Unexpected readiness failures are not retried and reach the stop-host handler", () => {
    const result = ps(`${publishHarness}
        function Invoke-WebRequest { $script:attempts++; throw [InvalidOperationException]::new('unexpected-sentinel') }
        $caught = $false
        try { $null = ${publishInvocation} }
        catch {
            if ($_.Exception.Message -cne 'unexpected-sentinel') { throw 'Unexpected failure was replaced' }
            $caught = $true
        }
        if (-not $caught -or $script:attempts -ne 1 -or $script:stops -ne 1 -or $script:sleeps -ne 0) {
            throw 'Unexpected failure did not immediately stop the host'
        }
    `);
    assert.equal(result.status, 0, result.stderr);
    assert.doesNotMatch(result.stdout, /Readiness attempt/);
});

test("Readiness exhaustion reports bounded attempts and stops the host without successful activation", () => {
    const result = ps(`${publishHarness}
        function Invoke-WebRequest { $script:attempts++; throw [TimeoutException]::new('private-message-sentinel') }
        $caught = $false
        try { $null = ${publishInvocation} }
        catch {
            if ($_.Exception.Message -notmatch 'readiness did not become healthy') { throw 'Wrong exhaustion failure' }
            $caught = $true
        }
        if (-not $caught -or $script:attempts -ne 18 -or $script:stops -ne 1 -or $script:sleeps -ne 18) {
            throw 'Readiness exhaustion was not bounded or did not stop the host'
        }
    `);
    assert.equal(result.status, 0, result.stderr);
    assert.equal((result.stdout.match(/Readiness attempt \d+\/18: category=timeout\./g) ?? []).length, 18);
    assert.doesNotMatch(result.stdout + result.stderr, /private-message-sentinel/);
});

for (const [name, failure, category] of [
    ...requestFailures,
    ["unexpected", "throw [InvalidOperationException]::new('private-message-sentinel')", "category=unexpected"]
]) {
    test(`Private ${name} failures preserve only safe provider category and HTTP status`, () => {
        const result = ps(`
            function az {
                $global:LASTEXITCODE = 0
                '{"tenant":"99e674a6-6773-4f53-90a3-e3ab8c37c856","subscription":"b75472bd-4174-4f66-b159-bae420212abc","accessToken":"private-token-sentinel"}'
            }
            function Invoke-RestMethod { ${failure} }
            try {
                $null = Invoke-SidequestPrivateRequest POST 'https://graph.microsoft.com/v1.0/applications' 'https://graph.microsoft.com/' @{value='private-body-sentinel'}
                throw 'The provider failure unexpectedly succeeded'
            } catch {
                if ($_.Exception.Message -notlike '*explicitly scoped identity/secret operation failed*') { throw }
                Write-Output $_.Exception.Message
            }
        `);
        assert.equal(result.status, 0, result.stderr);
        assert.ok(result.stdout.includes(`provider-request; ${category}`), result.stdout);
        assert.doesNotMatch(result.stdout + result.stderr, /private-(body|message|token)-sentinel/);
    });
}
