import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import test from "node:test";

const script = fileURLToPath(new URL("./deploy.ps1", import.meta.url)).replaceAll("'", "''");
const fixture = `
    $ErrorActionPreference = 'Stop'
    . '${script}'
    $source = '0123456789012345678901234567890123456789'
    $groupId = '00000000-0000-0000-0000-000000000002'
    $reviewPath = Join-Path ([IO.Path]::GetTempPath()) "sidequest-review-$([guid]::NewGuid().ToString('N')).json"
    $script:calls = [Collections.Generic.List[string]]::new()
    $script:dirty = $false
    $script:login = 'vaclav-pekarek-microsoft'
    $script:accepted = $source
    $script:groupName = 'Sidequest SQL Administrators'
    $script:memberId = 'owner'
    $script:exists = $false
    $script:fixtureLocation = 'westus3'
    $script:whatIfStatus = 'Succeeded'
    $script:provisioningState = 'Succeeded'
    $script:compiled = '{"fixture":1}'
    $script:temporaryTemplate = $null
    function git {
        $global:LASTEXITCODE = 0
        if ($args -contains 'rev-parse') { return $source }
        if ($args -contains 'status') { if ($script:dirty) { return ' M changed.txt' }; return }
        throw 'Unexpected git command'
    }
    function gh {
        $global:LASTEXITCODE = 0
        if ($args[1] -eq 'user') { return $script:login }
        return $script:accepted
    }
    function Invoke-TestCompiler {
        $global:LASTEXITCODE = 0
        if ($args[0] -eq '--version') { return 'Bicep CLI version 0.47.16 (fixture)' }
        $script:temporaryTemplate = $args[3]
        Set-Content -LiteralPath $script:temporaryTemplate -Value $script:compiled
    }
    function Invoke-SidequestStagingAzure {
        param([string[]] $Arguments)
        $command = $Arguments -join '|'
        $script:calls.Add($command)
        switch -Wildcard ($command) {
            '*management.azure.com/subscriptions/*' {
                return [pscustomobject]@{
                    subscriptionId = 'b75472bd-4174-4f66-b159-bae420212abc'
                    tenantId = '99e674a6-6773-4f53-90a3-e3ab8c37c856'
                    state = 'Enabled'
                    subscriptionPolicies = @{ spendingLimit = 'On'; quotaId = 'MSDN_2014-09-01' }
                }
            }
            '*groups/*/members/*' { return [pscustomobject]@{ id = $script:memberId } }
            '*groups/*' {
                return [pscustomobject]@{ id = $groupId; displayName = $script:groupName; securityEnabled = $true }
            }
            '*graph.microsoft.com/v1.0/me' { return [pscustomobject]@{ id = 'owner' } }
            'group|exists|*' { return $script:exists }
            'group|create|*' { $script:exists = $true; return }
            'group|show|*' { return [pscustomobject]@{ location = $script:fixtureLocation; tags = @{ application = 'Sidequest' } } }
            'deployment|group|what-if|*' {
                return [pscustomobject]@{ status = $script:whatIfStatus; changes = @(@{ changeType = 'Create'; resourceId = '/reviewed' }) }
            }
            'deployment|group|create|*' {
                return [pscustomobject]@{ properties = @{
                    provisioningState = $script:provisioningState
                    outputs = @{ serverName = @{ value = 'fixture-server' } }
                } }
            }
            default { throw "Unexpected Azure command: $command" }
        }
    }
    function Invoke-Fixture([string] $Mode) {
        Invoke-SidequestSqlDeployment -Mode $Mode -SourceCommit $source -AdministratorGroupObjectId $groupId -BicepPath Invoke-TestCompiler -ReviewPath $reviewPath
    }
    function Assert-NoDeployment {
        if (@($script:calls | Where-Object { $_ -like 'deployment|*' }).Count -ne 0) {
            throw 'A rejected operation reached ARM deployment'
        }
    }
    function Assert-Rejected([scriptblock] $Action, [string] $Expected) {
        $rejected = $false
        try { & $Action } catch {
            if ($_.Exception.Message -notlike "*$Expected*") { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw 'Unsafe operation unexpectedly succeeded' }
    }
`;

function run(body) {
    const result = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command",
        `${fixture}
        try {
            ${body}
            if ($script:temporaryTemplate -and (Test-Path -LiteralPath $script:temporaryTemplate)) {
                throw 'Compiled temporary template leaked'
            }
        } finally {
            if (Test-Path -LiteralPath $reviewPath) { Remove-Item -LiteralPath $reviewPath }
        }`], { encoding: "utf8" });
    assert.equal(result.error, undefined);
    assert.equal(result.status, 0, result.stdout + result.stderr);
}

test("WhatIf retains exact change evidence and Apply provisions once without budget email or month prerequisites", () => {
    run(`
        $review = Invoke-Fixture WhatIf
        $saved = Get-Content -LiteralPath $reviewPath -Raw | ConvertFrom-Json
        if ($saved.sourceCommit -cne $source -or $saved.changes[0].resourceId -cne '/reviewed') { throw 'Review evidence lost' }
        if ($saved.templateSha256 -notmatch '^[A-F0-9]{64}$') { throw 'Template hash missing' }
        if (@($script:calls | Where-Object { $_ -like 'group|create|*' }).Count -ne 1) { throw 'Expected one empty-group creation' }
        if (@($script:calls | Where-Object { $_ -like 'deployment|group|create|*' }).Count -ne 0) { throw 'WhatIf provisioned SQL' }
        $script:calls.Clear()
        $outputs = Invoke-Fixture Apply
        if ($outputs.serverName.value -cne 'fixture-server') { throw 'ARM outputs lost' }
        if (@($script:calls | Where-Object { $_ -like 'group|create|*' }).Count -ne 0) { throw 'Apply recreated resource group' }
        $deployments = @($script:calls | Where-Object { $_ -like 'deployment|group|create|*' })
        if ($deployments.Count -ne 1 -or $deployments[0] -notlike '*--resource-group|sidequest-rg|*') {
            throw 'Expected one correctly scoped SQL deployment'
        }
        if ($deployments[0] -match 'budgetContactEmail|budgetStartDate') { throw 'Strict budget prerequisite restored' }
    `);
});

for (const [name, setup, message] of [
    ["unaccepted source", "$script:accepted = 'f' * 40", "accepted release-infrastructure head"],
    ["dirty source", "$script:dirty = $true", "clean worktree"],
    ["another GitHub account", "$script:login = 'other'", "approved GitHub account"],
    ["another administrator group", "$script:groupName = 'Other group'", "verified Sidequest SQL Administrators"],
    ["missing administrator membership", "$script:memberId = 'other'", "direct SQL administrator-group member"],
    ["another resource-group region", "$script:fixtureLocation = 'eastus'; $script:exists = $true", "reviewed Sidequest staging group"]
]) {
    test(`Deployment orchestration rejects ${name} without reaching ARM deployment`, () => {
        run(`${setup}; Assert-Rejected { Invoke-Fixture WhatIf } '${message}'; Assert-NoDeployment`);
    });
}

for (const [name, setup, message] of [
    ["changed template", "$script:compiled = '{\"fixture\":2}'", "does not match: templateSha256"],
    ["changed source binding", "$saved.sourceCommit = 'f' * 40", "does not match: sourceCommit"],
    ["changed subscription binding", "$saved.subscription = 'other'", "does not match: subscription"],
    ["failed what-if evidence", "$saved.whatIfStatus = 'Failed'", "successful retained what-if"],
    ["missing resource group", "$script:exists = $false", "resource group no longer exists"]
]) {
    test(`Apply rejects ${name} before resource writes`, () => {
        run(`
            $null = Invoke-Fixture WhatIf
            $saved = Get-Content -LiteralPath $reviewPath -Raw | ConvertFrom-Json
            ${setup}
            $saved | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $reviewPath
            $script:calls.Clear()
            Assert-Rejected { Invoke-Fixture Apply } '${message}'
            Assert-NoDeployment
            if (@($script:calls | Where-Object { $_ -like 'group|create|*' }).Count -ne 0) { throw 'Rejected Apply wrote a group' }
        `);
    });
}

test("WhatIf preserves an existing review instead of overwriting deployment evidence", () => {
    run(`
        $null = Invoke-Fixture WhatIf
        $before = (Get-FileHash -LiteralPath $reviewPath).Hash
        $script:calls.Clear()
        Assert-Rejected { Invoke-Fixture WhatIf } 'existing deployment evidence is not overwritten'
        Assert-NoDeployment
        if ((Get-FileHash -LiteralPath $reviewPath).Hash -cne $before) { throw 'Existing review changed' }
    `);
});

test("Failed ARM what-if does not persist success-shaped evidence", () => {
    run(`
        $script:whatIfStatus = 'Failed'
        Assert-Rejected { Invoke-Fixture WhatIf } 'what-if did not succeed'
        if (Test-Path -LiteralPath $reviewPath) { throw 'Failed what-if produced a review' }
    `);
});

test("Failed provisioning throws after a single write attempt without automatic retry or rollback", () => {
    run(`
        $null = Invoke-Fixture WhatIf
        $script:provisioningState = 'Failed'
        $script:calls.Clear()
        Assert-Rejected { Invoke-Fixture Apply } 'provisioning did not succeed'
        if (@($script:calls | Where-Object { $_ -like 'deployment|group|create|*' }).Count -ne 1) {
            throw 'Expected exactly one failed write attempt'
        }
    `);
});
