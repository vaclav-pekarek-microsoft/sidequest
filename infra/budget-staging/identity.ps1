#requires -Version 7.4
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'application.ps1')

function Get-SidequestLocalRedirectUris {
    $launchPath = Join-Path $PSScriptRoot '..\..\src\Sidequest.Web\Properties\launchSettings.json'
    $launch = Get-Content -LiteralPath $launchPath -Raw | ConvertFrom-Json
    $origins = @($launch.profiles.https.applicationUrl.Split(';') | Where-Object {
        $candidate = $null
        [uri]::TryCreate($_, [UriKind]::Absolute, [ref] $candidate) -and
        $candidate.Scheme -ceq 'https' -and $candidate.Host -ceq 'localhost' -and
        $candidate.IsLoopback -and $candidate.AbsolutePath -ceq '/' -and
        -not $candidate.UserInfo -and -not $candidate.Query -and -not $candidate.Fragment
    })
    if ($origins.Count -ne 1) { throw 'The accepted HTTPS launch profile must identify one unambiguous localhost origin.' }
    $root = $origins[0].TrimEnd('/')
    return @("$root/signin-oidc", "$root/signout-callback-oidc")
}

function Invoke-SidequestPrivateRequest {
    param(
        [Parameter(Mandatory)] [ValidateSet('GET', 'POST', 'PUT')] [string] $Method,
        [Parameter(Mandatory)] [uri] $Uri,
        [Parameter(Mandatory)] [ValidateSet('https://graph.microsoft.com/', 'https://vault.azure.net')] [string] $Audience,
        [object] $Body
    )
    if ($Uri.Scheme -cne 'https' -or -not $Uri.IsDefaultPort -or $Uri.UserInfo -or $Uri.Fragment -or
        ($Audience -eq 'https://graph.microsoft.com/' -and $Uri.Host -cne 'graph.microsoft.com') -or
        ($Audience -eq 'https://vault.azure.net' -and $Uri.Host -cnotmatch '^sqh-[a-z0-9]{13}\.vault\.azure\.net$')) {
        throw 'Private credential operations require the exact approved native endpoint.'
    }
    $token = $null
    $json = $null
    $stage = 'token-acquisition'
    try {
        $tokenJson = & az account get-access-token --subscription $script:Subscription --resource $Audience --output json --only-show-errors
        if ($LASTEXITCODE -ne 0) { throw 'Token acquisition failed.' }
        $stage = 'token-response'
        $token = $tokenJson | ConvertFrom-Json
        $stage = 'token-scope'
        if ($token.tenant -cne $script:Tenant -or $token.subscription -cne $script:Subscription) {
            throw 'Token does not match the approved tenant/subscription.'
        }
        $parameters = @{
            Method = $Method; Uri = $Uri; Authentication = 'Bearer'
            Token = ConvertTo-SecureString $token.accessToken -AsPlainText -Force
            ContentType = 'application/json'; ErrorAction = 'Stop'
        }
        if ($null -ne $Body) {
            $stage = 'request-serialization'
            $json = $Body | ConvertTo-Json -Depth 20 -Compress
            $parameters.Body = $json
        }
        $stage = 'provider-request'
        return Invoke-RestMethod @parameters
    } catch {
        $failure = Get-SidequestSafeRequestFailure $_.Exception
        throw [InvalidOperationException]::new("The explicitly scoped identity/secret operation failed ($stage; $failure).")
    } finally {
        $token = $null
        $tokenJson = $null
        $json = $null
    }
}

function New-SidequestHackathonRegistration {
    <#
    .SYNOPSIS
    Creates the owner-only single-tenant hackathon registration and explicitly consented read-only Graph grants.
    .DESCRIPTION
    This is an owner-executed directory mutation in the approved tenant, not a resource-group resource.
    Requires separate approval of User.Read.All and GroupMember.Read.All application permissions.
    Existing same-name registrations cause a stop instead of silent adoption or privilege expansion.
    No password, workforce policy, first-user administration or subscription role is created.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $SourceCommit,
        [Parameter(Mandatory)] [uri] $ApplicationUrl,
        [Parameter(Mandatory)] [switch] $ReadOnlyGraphConsentApproved
    )
    $ErrorActionPreference = 'Stop'
    if (-not $ReadOnlyGraphConsentApproved) { throw 'Explicit approval of both read-only Graph application permissions is required.' }
    if ($ApplicationUrl.Scheme -cne 'https' -or -not $ApplicationUrl.IsDefaultPort -or
        $ApplicationUrl.Host -cnotmatch '^sidequest-hackathon-[a-z0-9]{13}\.azurewebsites\.net$' -or
        $ApplicationUrl.AbsolutePath -cne '/' -or $ApplicationUrl.Query -or $ApplicationUrl.Fragment -or $ApplicationUrl.UserInfo) {
        throw 'Use the exact HTTPS hackathon App Service root URL.'
    }
    Assert-SidequestApplicationSource $SourceCommit
    $owner = '1250fe10-b814-4735-801f-ea5a0a4c1219'
    $me = Invoke-SidequestPrivateRequest GET 'https://graph.microsoft.com/v1.0/me?$select=id' 'https://graph.microsoft.com/'
    if ($me.id -cne $owner) { throw 'Only the approved tenant owner may execute this initial registration.' }
    $existing = Invoke-SidequestPrivateRequest GET "https://graph.microsoft.com/v1.0/applications?`$filter=displayName%20eq%20%27Sidequest%20Hackathon%27&`$select=id" 'https://graph.microsoft.com/'
    if (@($existing.value).Count -ne 0) { throw 'A matching registration already exists; verify and manage its exact object ID manually.' }
    $graph = Invoke-SidequestPrivateRequest GET "https://graph.microsoft.com/v1.0/servicePrincipals?`$filter=appId%20eq%20%2700000003-0000-0000-c000-000000000000%27&`$select=id,appRoles" 'https://graph.microsoft.com/'
    if (@($graph.value).Count -ne 1) { throw 'The tenant Microsoft Graph service principal is not uniquely verified.' }
    $permissions = foreach ($value in @('User.Read.All', 'GroupMember.Read.All')) {
        $role = @($graph.value[0].appRoles | Where-Object { $_.value -ceq $value -and $_.isEnabled -and 'Application' -cin $_.allowedMemberTypes })
        if ($role.Count -ne 1) { throw 'A required read-only Graph application role could not be verified.' }
        @{ id = $role[0].id; type = 'Role' }
    }
    $roleId = [guid]::NewGuid().ToString()
    $root = $ApplicationUrl.AbsoluteUri.TrimEnd('/')
    $application = Invoke-SidequestPrivateRequest POST 'https://graph.microsoft.com/v1.0/applications' 'https://graph.microsoft.com/' @{
        displayName = 'Sidequest Hackathon'
        signInAudience = 'AzureADMyOrg'
        web = @{
            redirectUris = @("$root/signin-oidc", "$root/signout-callback-oidc") + @(Get-SidequestLocalRedirectUris)
            logoutUrl = "$root/signout-oidc"
        }
        appRoles = @(@{
            id = $roleId; allowedMemberTypes = @('User'); displayName = 'Hackathon participant'
            description = 'Explicitly assigned approved hackathon participants, including the approved owner guest.'
            isEnabled = $true; value = 'Sidequest.Hackathon.Participant'
        })
        requiredResourceAccess = @(@{ resourceAppId = '00000003-0000-0000-c000-000000000000'; resourceAccess = @($permissions) })
    }
    $service = Invoke-SidequestPrivateRequest POST 'https://graph.microsoft.com/v1.0/servicePrincipals' 'https://graph.microsoft.com/' @{
        appId = $application.appId; appRoleAssignmentRequired = $true
    }
    $null = Invoke-SidequestPrivateRequest POST "https://graph.microsoft.com/v1.0/servicePrincipals/$($service.id)/appRoleAssignedTo" 'https://graph.microsoft.com/' @{
        principalId = $owner; resourceId = $service.id; appRoleId = $roleId
    }
    foreach ($permission in $permissions) {
        $null = Invoke-SidequestPrivateRequest POST "https://graph.microsoft.com/v1.0/servicePrincipals/$($service.id)/appRoleAssignments" 'https://graph.microsoft.com/' @{
            principalId = $service.id; resourceId = $graph.value[0].id; appRoleId = $permission.id
        }
    }
    return @{ clientId = $application.appId; applicationObjectId = $application.id; servicePrincipalId = $service.id; participantRoleId = $roleId }
}

function Set-SidequestHackathonCredential {
    <#
    .SYNOPSIS
    Creates one 90-day application credential and immediately stores it in the dedicated staging Key Vault.
    .DESCRIPTION
    Secret values and access tokens remain in memory and never appear in CLI arguments, files or returned objects.
    Requires existing narrowly scoped Key Vault secret-set permission for the operator; does not grant that permission.
    A failed vault write may leave an unused credential; inspect/remove that specific credential before retrying.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $SourceCommit,
        [Parameter(Mandatory)] [guid] $ApplicationObjectId,
        [Parameter(Mandatory)] [guid] $ClientId,
        [Parameter(Mandatory)] [ValidatePattern('^sqh-[a-z0-9]{13}$')] [string] $VaultName
    )
    $ErrorActionPreference = 'Stop'
    Assert-SidequestApplicationSource $SourceCommit
    $vault = Invoke-SidequestStagingAzure @('keyvault', 'show', '--resource-group', $script:ResourceGroup, '--name', $VaultName)
    if ($vault.location -cne $script:Location -or $vault.properties.tenantId -cne $script:Tenant -or
        $vault.tags.application -cne 'Sidequest') { throw 'Secret target is not the approved staging vault.' }
    $application = Invoke-SidequestPrivateRequest GET "https://graph.microsoft.com/v1.0/applications/$ApplicationObjectId`?`$select=appId,displayName,signInAudience" 'https://graph.microsoft.com/'
    if ($application.appId -ine $ClientId.ToString() -or $application.displayName -cne 'Sidequest Hackathon' -or
        $application.signInAudience -cne 'AzureADMyOrg') { throw 'Credential target is not the verified single-tenant hackathon registration.' }
    $credential = $null
    try {
        $credential = Invoke-SidequestPrivateRequest POST "https://graph.microsoft.com/v1.0/applications/$ApplicationObjectId/addPassword" 'https://graph.microsoft.com/' @{
            passwordCredential = @{ displayName = 'Sidequest hackathon Key Vault'; endDateTime = [DateTimeOffset]::UtcNow.AddDays(90).ToString('o') }
        }
        if ([string]::IsNullOrWhiteSpace($credential.secretText)) { throw 'Entra did not return the new credential.' }
        $secret = Invoke-SidequestPrivateRequest PUT "https://$VaultName.vault.azure.net/secrets/entra-client-secret?api-version=7.4" 'https://vault.azure.net' @{
            value = $credential.secretText
            attributes = @{ enabled = $true; exp = [DateTimeOffset]::Parse($credential.endDateTime).ToUnixTimeSeconds() }
        }
        return @{ secretId = $secret.id; credentialKeyId = $credential.keyId; expires = $credential.endDateTime }
    } finally {
        $credential = $null
        $secret = $null
    }
}
