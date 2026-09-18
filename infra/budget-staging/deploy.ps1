#requires -Version 7.4
Set-StrictMode -Version Latest

$script:Subscription = 'b75472bd-4174-4f66-b159-bae420212abc'
$script:Tenant = '99e674a6-6773-4f53-90a3-e3ab8c37c856'
$script:ResourceGroup = 'sidequest-rg'
$script:Location = 'westus3'

function Assert-SidequestStagingSubscription {
    param([Parameter(Mandatory)] $Account)

    if ($Account.subscriptionId -cne $script:Subscription -or
        $Account.tenantId -cne $script:Tenant -or $Account.state -cne 'Enabled') {
        throw 'Only the approved, enabled Sidequest staging subscription and tenant are permitted.'
    }
    if ($Account.subscriptionPolicies.spendingLimit -cne 'On' -or
        $Account.subscriptionPolicies.quotaId -cne 'MSDN_2014-09-01') {
        throw 'The approved dev/test offer and enabled spending limit must remain unchanged.'
    }
}

function Invoke-SidequestStagingAzure {
    param([Parameter(Mandatory)] [string[]] $Arguments)

    $result = & az @Arguments --subscription $script:Subscription --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) { throw 'The explicitly scoped Azure operation failed; no fallback is permitted.' }
    if ($result) { return ($result | ConvertFrom-Json -Depth 100) }
}

function Invoke-SidequestSqlDeployment {
    <#
    .SYNOPSIS
    Validates or deploys the accepted SQL-only budget staging source using the owner's CLI session.
    .DESCRIPTION
    WhatIf may create the approved empty resource group, but never SQL or managed identities.
    Apply requires the exact retained what-if record and recompiles the same accepted source.
    Neither mode opens a firewall, executes migrations, activates an app or changes subscription limits.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [ValidateSet('WhatIf', 'Apply')] [string] $Mode,
        [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $SourceCommit,
        [Parameter(Mandatory)] [guid] $AdministratorGroupObjectId,
        [Parameter(Mandatory)] [string] $BicepPath,
        [Parameter(Mandatory)] [string] $ReviewPath
    )

    $ErrorActionPreference = 'Stop'
    $root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $head = & git -C $root rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -cne $SourceCommit) { throw 'The working source does not match SourceCommit.' }
    $dirty = & git -C $root status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Use a clean worktree; keep artifacts outside the repository.' }
    $login = & gh api user --jq .login
    if ($LASTEXITCODE -ne 0 -or $login -cne 'vaclav-pekarek-microsoft') { throw 'The approved GitHub account is required.' }
    $accepted = & gh api repos/vaclav-pekarek-microsoft/sidequest/git/ref/heads/release-infrastructure --jq .object.sha
    if ($LASTEXITCODE -ne 0 -or $accepted -cne $SourceCommit) { throw 'SourceCommit must be the accepted release-infrastructure head.' }

    $account = Invoke-SidequestStagingAzure @('rest', '--method', 'get', '--url',
        "https://management.azure.com/subscriptions/$script:Subscription`?api-version=2022-12-01")
    Assert-SidequestStagingSubscription $account
    $group = Invoke-SidequestStagingAzure @('rest', '--method', 'get', '--url',
        "https://graph.microsoft.com/v1.0/groups/$AdministratorGroupObjectId")
    if ($group.id -ine $AdministratorGroupObjectId.ToString() -or
        $group.displayName -cne 'Sidequest SQL Administrators' -or $group.securityEnabled -ne $true) {
        throw 'The selected administrator must be the verified Sidequest SQL Administrators security group.'
    }
    $me = Invoke-SidequestStagingAzure @('rest', '--method', 'get', '--url', 'https://graph.microsoft.com/v1.0/me')
    $membership = Invoke-SidequestStagingAzure @('rest', '--method', 'get', '--url',
        "https://graph.microsoft.com/v1.0/groups/$AdministratorGroupObjectId/members/$($me.id)")
    if ($membership.id -ine $me.id) { throw 'The deployment owner must be a direct SQL administrator-group member.' }

    $compilerVersion = & $BicepPath --version
    if ($LASTEXITCODE -ne 0 -or $compilerVersion -notmatch '^Bicep CLI version 0\.47\.16 ') {
        throw 'Use the reviewed Bicep 0.47.16 compiler.'
    }
    $temporaryTemplate = Join-Path ([IO.Path]::GetTempPath()) "sidequest-$([guid]::NewGuid().ToString('N')).json"
    try {
        & $BicepPath build (Join-Path $PSScriptRoot 'sql.bicep') --outfile $temporaryTemplate
        if ($LASTEXITCODE -ne 0) { throw 'Budget SQL template compilation failed.' }
        $hash = (Get-FileHash $temporaryTemplate -Algorithm SHA256).Hash
        $parameters = @(
            "sqlAdministratorGroupObjectId=$AdministratorGroupObjectId",
            'sqlAdministratorGroupName=Sidequest SQL Administrators'
        )
        $record = [ordered]@{
            sourceCommit = $SourceCommit
            templateSha256 = $hash
            subscription = $script:Subscription
            tenant = $script:Tenant
            resourceGroup = $script:ResourceGroup
            location = $script:Location
            administratorGroupObjectId = $AdministratorGroupObjectId.ToString()
        }
        if ($Mode -eq 'Apply') {
            $review = Get-Content -LiteralPath $ReviewPath -Raw | ConvertFrom-Json
            foreach ($key in $record.Keys) {
                if ($review.$key -cne $record[$key]) { throw "The retained what-if review does not match: $key." }
            }
            if ($review.whatIfStatus -cne 'Succeeded') { throw 'A successful retained what-if is required.' }
        } elseif (Test-Path -LiteralPath $ReviewPath) {
            throw 'Choose a new review path; existing deployment evidence is not overwritten.'
        }

        $exists = Invoke-SidequestStagingAzure @('group', 'exists', '--name', $script:ResourceGroup)
        if (-not $exists) {
            if ($Mode -eq 'Apply') { throw 'The reviewed resource group no longer exists.' }
            $null = Invoke-SidequestStagingAzure @('group', 'create', '--name', $script:ResourceGroup,
                '--location', $script:Location, '--tags', 'application=Sidequest', 'environment=staging')
        }
        $resourceGroup = Invoke-SidequestStagingAzure @('group', 'show', '--name', $script:ResourceGroup)
        if ($resourceGroup.location -cne $script:Location -or $resourceGroup.tags.application -cne 'Sidequest') {
            throw 'The existing resource group is not the reviewed Sidequest staging group.'
        }
        $arguments = @('deployment', 'group', '--resource-group', $script:ResourceGroup,
            '--name', "sidequest-sql-$($SourceCommit.Substring(0, 12))",
            '--template-file', $temporaryTemplate, '--parameters') + $parameters
        if ($Mode -eq 'WhatIf') {
            $result = Invoke-SidequestStagingAzure ($arguments[0..1] + @('what-if', '--no-pretty-print') + $arguments[2..($arguments.Length - 1)])
            if ($result.status -cne 'Succeeded') { throw 'SQL staging what-if did not succeed.' }
            $record.whatIfStatus = $result.status
            $record.changes = $result.changes
            $record | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $ReviewPath -Encoding utf8
            return $record
        }
        $result = Invoke-SidequestStagingAzure ($arguments[0..1] + @('create') + $arguments[2..($arguments.Length - 1)])
        if ($result.properties.provisioningState -cne 'Succeeded') { throw 'SQL staging provisioning did not succeed.' }
        return $result.properties.outputs
    } finally {
        if (Test-Path -LiteralPath $temporaryTemplate) { Remove-Item -LiteralPath $temporaryTemplate }
    }
}
