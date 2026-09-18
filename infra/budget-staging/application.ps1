#requires -Version 7.4
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'deploy.ps1')

function Get-SidequestSafeRequestFailure {
    param([Parameter(Mandatory)] [Exception] $Exception)

    if ($Exception -is [Microsoft.PowerShell.Commands.HttpResponseException]) {
        return "category=http; status=$([int] $Exception.Response.StatusCode)"
    }
    if ($Exception -is [Net.Http.HttpRequestException]) {
        if ($null -ne $Exception.StatusCode) { return "category=http; status=$([int] $Exception.StatusCode)" }
        return 'category=network'
    }
    if ($Exception -is [OperationCanceledException] -or $Exception -is [TimeoutException]) {
        return 'category=timeout'
    }
    if ($Exception -is [Net.WebException]) {
        return $(if ($Exception.Status -eq [Net.WebExceptionStatus]::Timeout) { 'category=timeout' } else { 'category=network' })
    }
    return 'category=unexpected'
}

function Assert-SidequestApplicationSource {
    param([Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $SourceCommit)
    $root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $head = & git -C $root rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -cne $SourceCommit) { throw 'SourceCommit does not match the worktree.' }
    $dirty = & git -C $root status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Use a clean accepted worktree and keep deployment artifacts in its Git metadata directory.' }
    $login = & gh api user --jq .login
    if ($LASTEXITCODE -ne 0 -or $login -cne 'vaclav-pekarek-microsoft') { throw 'The approved GitHub account is required.' }
    $accepted = & gh api repos/vaclav-pekarek-microsoft/sidequest/git/ref/heads/release-infrastructure --jq .object.sha
    if ($LASTEXITCODE -ne 0 -or $accepted -cne $SourceCommit) { throw 'Use the accepted release-infrastructure head.' }
    $account = Invoke-SidequestStagingAzure @('rest', '--method', 'get', '--url',
        "https://management.azure.com/subscriptions/$script:Subscription`?api-version=2022-12-01")
    Assert-SidequestStagingSubscription $account
    $group = Invoke-SidequestStagingAzure @('group', 'show', '--name', $script:ResourceGroup)
    if ($group.location -cne $script:Location -or $group.tags.application -cne 'Sidequest') {
        throw 'Only the existing approved West US 3 staging resource group is permitted.'
    }
}

function Get-SidequestApplicationPath {
    param([Parameter(Mandatory)] [string] $ApplicationName)
    if ($ApplicationName -cnotmatch '^sidequest-hackathon-[a-z0-9]{13}$') {
        throw 'Only a bounded Sidequest hackathon application name is permitted.'
    }
    return "https://management.azure.com/subscriptions/$script:Subscription/resourceGroups/$script:ResourceGroup/providers/Microsoft.Web/sites/$ApplicationName"
}

function Invoke-SidequestApplicationDeployment {
    <#
    .SYNOPSIS
    Reviews or applies only the source-pinned disabled hackathon application infrastructure.
    .DESCRIPTION
    Apply recompiles the accepted source and verifies every retained review field.
    Never creates SQL, opens SQL firewall rules, creates credentials, migrates schema or activates the host.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [ValidateSet('WhatIf', 'Apply')] [string] $Mode,
        [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $SourceCommit,
        [Parameter(Mandatory)] [guid] $ClientId,
        [Parameter(Mandatory)] [string] $BicepPath,
        [Parameter(Mandatory)] [string] $ReviewPath
    )
    $ErrorActionPreference = 'Stop'
    if ($ClientId -eq [guid]::Empty) { throw 'A real single-tenant client ID is required.' }
    Assert-SidequestApplicationSource $SourceCommit
    $version = & $BicepPath --version
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^Bicep CLI version 0\.47\.16 ') { throw 'Use Bicep 0.47.16.' }
    $metadata = & git rev-parse --path-format=absolute --git-path sidequest-hackathon
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the worktree artifact directory.' }
    $null = New-Item -ItemType Directory -Path $metadata -Force
    $template = Join-Path $metadata "application-$([guid]::NewGuid().ToString('N')).json"
    try {
        & $BicepPath build (Join-Path $PSScriptRoot 'application.bicep') --outfile $template
        if ($LASTEXITCODE -ne 0) { throw 'Application template compilation failed.' }
        $record = [ordered]@{
            sourceCommit = $SourceCommit
            templateSha256 = (Get-FileHash $template -Algorithm SHA256).Hash
            subscription = $script:Subscription
            tenant = $script:Tenant
            resourceGroup = $script:ResourceGroup
            clientId = $ClientId.ToString()
        }
        if ($Mode -eq 'Apply') {
            $review = Get-Content -LiteralPath $ReviewPath -Raw | ConvertFrom-Json
            foreach ($key in $record.Keys) {
                if ($review.$key -cne $record[$key]) { throw "The application review does not match: $key." }
            }
            if ($review.whatIfStatus -cne 'Succeeded') { throw 'A successful application what-if is required.' }
        } elseif (Test-Path -LiteralPath $ReviewPath) { throw 'Do not overwrite an existing review.' }
        $verb = if ($Mode -eq 'WhatIf') { 'what-if' } else { 'create' }
        $arguments = @('deployment', 'group', $verb, '--resource-group', $script:ResourceGroup,
            '--name', "sidequest-app-$($SourceCommit.Substring(0, 12))", '--template-file', $template,
            '--parameters', "clientId=$ClientId")
        if ($Mode -eq 'WhatIf') {
            $result = Invoke-SidequestStagingAzure ($arguments + @('--no-pretty-print'))
            if ($result.status -cne 'Succeeded') { throw 'Application what-if failed.' }
            $record.whatIfStatus = $result.status
            $record.changes = $result.changes
            $record | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $ReviewPath -Encoding utf8
            return $record
        }
        $result = Invoke-SidequestStagingAzure $arguments
        if ($result.properties.provisioningState -cne 'Succeeded') { throw 'Application deployment failed.' }
        return $result.properties.outputs
    } finally {
        if (Test-Path -LiteralPath $template) { Remove-Item -LiteralPath $template }
    }
}

function Get-SidequestVerifiedOutboundAddresses {
    param([Parameter(Mandatory)] $Site)
    if ($Site.location.Replace(' ', '').ToLowerInvariant() -cne $script:Location -or
        $Site.kind -notmatch 'linux' -or $Site.tags.application -cne 'Sidequest') {
        throw 'The host is not the reviewed Linux staging application.'
    }
    $addresses = @($Site.properties.outboundIpAddresses.Split(',') | Sort-Object -Unique)
    if ($addresses.Count -lt 1 -or $addresses.Count -gt 32) { throw 'Unexpected outbound address count.' }
    foreach ($address in $addresses) {
        $ip = $null
        if (-not [Net.IPAddress]::TryParse($address, [ref] $ip) -or
            $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $ip.ToString() -cne $address) {
            throw 'Every outbound rule must contain one actual canonical IPv4 address.'
        }
        $bytes = $ip.GetAddressBytes()
        if ($bytes[0] -in @(0, 10, 127) -or $bytes[0] -ge 224 -or
            ($bytes[0] -eq 169 -and $bytes[1] -eq 254) -or
            ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
            ($bytes[0] -eq 192 -and $bytes[1] -eq 168)) { throw 'Outbound SQL rules must be public IPv4 addresses.' }
    }
    return $addresses
}

function Set-SidequestApplicationFirewall {
    <#
    .SYNOPSIS
    Reviews or applies exact observed App Service SQL egress addresses, never possible addresses or ranges.
    .DESCRIPTION
    Revalidates the live address set against the retained review. Deletes only stale sq-hackathon-* rules.
    Unrelated existing rules cause an explicit stop; this helper does not rewrite SQL administrators or execute SQL.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [ValidateSet('WhatIf', 'Apply')] [string] $Mode,
        [Parameter(Mandatory)] [string] $SourceCommit,
        [Parameter(Mandatory)] [string] $ApplicationName,
        [Parameter(Mandatory)] [string] $ReviewPath
    )
    $ErrorActionPreference = 'Stop'
    Assert-SidequestApplicationSource $SourceCommit
    $path = Get-SidequestApplicationPath $ApplicationName
    $site = Invoke-SidequestStagingAzure @('rest', '--method', 'get', '--url', "$path`?api-version=2024-04-01")
    $addresses = @(Get-SidequestVerifiedOutboundAddresses $site)
    $server = 'sidequest-sql-b7ljjkoqcaedc'
    $rules = @(Invoke-SidequestStagingAzure @('sql', 'server', 'firewall-rule', 'list', '--resource-group',
        $script:ResourceGroup, '--server', $server))
    if (@($rules | Where-Object { $_.name -cnotmatch '^sq-hackathon-\d{1,3}(-\d{1,3}){3}$' }).Count -ne 0) {
        throw 'Unexpected SQL firewall rules require separate owner review.'
    }
    $record = [ordered]@{ sourceCommit = $SourceCommit; application = $ApplicationName; addresses = $addresses }
    if ($Mode -eq 'WhatIf') {
        if (Test-Path -LiteralPath $ReviewPath) { throw 'Do not overwrite an existing firewall review.' }
        $record | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ReviewPath -Encoding utf8
        return $record
    }
    $review = Get-Content -LiteralPath $ReviewPath -Raw | ConvertFrom-Json
    if ($review.sourceCommit -cne $SourceCommit -or $review.application -cne $ApplicationName -or
        ($review.addresses -join ',') -cne ($addresses -join ',')) { throw 'Observed outbound addresses no longer match the firewall review.' }
    foreach ($address in $addresses) {
        $null = Invoke-SidequestStagingAzure @('sql', 'server', 'firewall-rule', 'create', '--resource-group',
            $script:ResourceGroup, '--server', $server, '--name', "sq-hackathon-$($address.Replace('.', '-'))",
            '--start-ip-address', $address, '--end-ip-address', $address)
    }
    foreach ($rule in $rules) {
        if ($rule.name -cnotin @($addresses | ForEach-Object { "sq-hackathon-$($_.Replace('.', '-'))" })) {
            $null = Invoke-SidequestStagingAzure @('sql', 'server', 'firewall-rule', 'delete', '--resource-group',
                $script:ResourceGroup, '--server', $server, '--name', $rule.name)
        }
    }
    $actual = @(Invoke-SidequestStagingAzure @('sql', 'server', 'firewall-rule', 'list', '--resource-group',
        $script:ResourceGroup, '--server', $server))
    if ($actual.Count -ne $addresses.Count -or
        @($actual | Where-Object { $_.startIpAddress -cne $_.endIpAddress -or $_.startIpAddress -cnotin $addresses }).Count -ne 0) {
        throw 'SQL firewall verification failed.'
    }
}

function Publish-SidequestImmutablePackage {
    <#
    .SYNOPSIS
    Uses package-aware Kudu ZipDeploy and verifies the selected remote ZIP before host activation.
    .DESCRIPTION
    Entra credentials stay in memory and are sent only to the application's native SCM endpoint.
    Upload and polling are bounded. Neither deployment response bodies nor tokens enter failure diagnostics.
    #>
    param(
        [Parameter(Mandatory)] [string] $ApplicationName,
        [Parameter(Mandatory)] [string] $ZipPath,
        [Parameter(Mandatory)] [ValidatePattern('^[0-9A-Fa-f]{64}$')] [string] $Sha256
    )
    $ErrorActionPreference = 'Stop'
    $null = Get-SidequestApplicationPath $ApplicationName
    $scm = "https://$ApplicationName.scm.azurewebsites.net"
    $token = $null
    $authentication = $null
    $stage = 'token-acquisition'
    try {
        $token = Invoke-SidequestStagingAzure @('account', 'get-access-token', '--resource', 'https://management.azure.com/')
        $stage = 'token-scope'
        if ($token.tenant -cne $script:Tenant -or $token.subscription -cne $script:Subscription) {
            throw 'The SCM token must match the approved tenant and subscription.'
        }
        $authentication = @{
            Authentication = 'Bearer'
            Token = ConvertTo-SecureString $token.accessToken -AsPlainText -Force
            MaximumRedirection = 0
            ErrorAction = 'Stop'
        }
        $stage = 'zip-upload'
        $upload = Invoke-WebRequest @authentication -Method Post -Uri "$scm/api/zipdeploy?isAsync=true" `
            -InFile $ZipPath -ContentType 'application/octet-stream' -TimeoutSec 180
        $stage = 'deployment-location'
        $locations = @($upload.Headers.Location)
        $location = $null
        if ($upload.StatusCode -ne 202 -or $locations.Count -ne 1 -or
            -not [uri]::TryCreate($locations[0], [UriKind]::Absolute, [ref] $location) -or
            $location.GetLeftPart([UriPartial]::Authority) -cne $scm -or
            $location.UserInfo -or $location.Fragment -or
            $location.AbsolutePath -cnotmatch '^/api/deployments/(latest|[0-9a-f-]+)$') {
            throw 'ZipDeploy must return one same-origin deployment status endpoint.'
        }
        $stage = 'deployment-completion'
        $complete = $false
        for ($attempt = 0; $attempt -lt 24; $attempt++) {
            $deployment = Invoke-RestMethod @authentication -Method Get -Uri $location -TimeoutSec 15
            if ($deployment.status -eq 3) { throw 'ZipDeploy reported failure.' }
            if ($deployment.complete -eq $true) {
                if ($deployment.status -ne 4) { throw 'ZipDeploy did not report successful completion.' }
                $complete = $true
                break
            }
            if ($attempt -lt 23) { Start-Sleep -Seconds 5 }
        }
        if (-not $complete) { throw 'ZipDeploy did not complete within the bounded polling window.' }

        $stage = 'package-verification'
        $command = 'python3 -c "' + (@(
            'import pathlib,hashlib,json'
            "p=pathlib.Path('/home/data/SitePackages')"
            "n=(p/'packagename.txt').read_text().strip()"
            'q=p/n'
            'assert q.resolve().parent==p.resolve()'
            'h=hashlib.sha256()'
            "f=q.open('rb')"
            "[h.update(b) for b in iter(lambda:f.read(1048576),b'')]"
            'f.close()'
            "print(json.dumps({'package':n,'sha256':h.hexdigest()}))"
        ) -join '; ') + '"'
        $verification = Invoke-RestMethod @authentication -Method Post -Uri "$scm/api/command" `
            -ContentType 'application/json' -Body (@{ command = $command; dir = '/home' } | ConvertTo-Json) -TimeoutSec 60
        if ($verification.ExitCode -ne 0) { throw 'The selected immutable package could not be read.' }
        $actual = $verification.Output | ConvertFrom-Json
        if ($actual.package -cnotmatch '^[a-zA-Z0-9_-]+\.zip$' -or $actual.sha256 -ine $Sha256) {
            throw 'The selected remote package does not match the accepted artifact.'
        }
        return @{ deploymentId = $deployment.id; package = $actual.package; sha256 = $Sha256 }
    } catch {
        $failure = Get-SidequestSafeRequestFailure $_.Exception
        throw [InvalidOperationException]::new("Immutable package deployment failed ($stage; $failure).")
    } finally {
        if ($null -ne $authentication) { $authentication.Token.Dispose() }
        $token = $null
    }
}

function Publish-SidequestApplication {
    <#
    .SYNOPSIS
    Publishes a source-attested self-contained ZIP only after explicit operator identity/provider checks.
    .DESCRIPTION
    Requires a JSON manifest containing sourceCommit and sha256; it must accompany the accepted build.
    Checks both Key Vault references are resolved, then deploys with Entra-authenticated ZipDeploy (SCM basic auth remains off).
    Activation requires an explicit gate acknowledgement. A failed readiness check stops the app; no migration runs.
    The acknowledgement is owner evidence, not automated proof of Graph consent or recipient delivery.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $SourceCommit,
        [Parameter(Mandatory)] [string] $ApplicationName,
        [Parameter(Mandatory)] [string] $ZipPath,
        [Parameter(Mandatory)] [string] $ManifestPath,
        [Parameter(Mandatory)] [switch] $IdentityAndProviderGatesVerified
    )
    $ErrorActionPreference = 'Stop'
    if (-not $IdentityAndProviderGatesVerified) { throw 'Owner verification of the runbook identity/provider gates is required.' }
    Assert-SidequestApplicationSource $SourceCommit
    $path = Get-SidequestApplicationPath $ApplicationName
    $site = Invoke-SidequestStagingAzure @('rest', '--method', 'get', '--url', "$path`?api-version=2024-04-01")
    $hostname = $site.properties.defaultHostName
    if ($hostname -cnotmatch '^sidequest-hackathon-[a-z0-9-]+(\.[a-z0-9-]+)?\.azurewebsites\.net$') {
        throw 'The live application hostname is not a trusted native hackathon endpoint.'
    }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.sourceCommit -cne $SourceCommit -or
        $manifest.sha256 -cne (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash) {
        throw 'The publish artifact does not match its accepted source/hash manifest.'
    }
    $configuration = Invoke-SidequestStagingAzure @('webapp', 'config', 'show',
        '--resource-group', $script:ResourceGroup, '--name', $ApplicationName)
    $settings = @(Invoke-SidequestStagingAzure @('webapp', 'config', 'appsettings', 'list',
        '--resource-group', $script:ResourceGroup, '--name', $ApplicationName))
    $package = @($settings | Where-Object { $_.name -ceq 'WEBSITE_RUN_FROM_PACKAGE' })
    if ($configuration.appCommandLine -cne 'dotnet /home/site/wwwroot/Sidequest.Web.dll' -or
        $package.Count -ne 1 -or $package[0].value -cne '1') {
        throw 'Immutable local-package hosting and the read-only-compatible startup command are required.'
    }
    $references = Invoke-SidequestStagingAzure @('rest', '--method', 'get', '--url',
        "$path/config/configreferences/appsettings?api-version=2022-03-01")
    if ($references.nextLink) { throw 'Credential reference status is incomplete.' }
    foreach ($name in @('AzureAd__ClientSecret', 'Directory__Credentials__ClientSecret')) {
        $matching = @($references.value | Where-Object { $_.name -ceq $name })
        if ($matching.Count -ne 1 -or $matching[0].properties.status -cne 'Resolved') {
            throw 'Required Key Vault credential reference is not uniquely resolved.'
        }
    }
    try {
        $null = Invoke-SidequestStagingAzure @('resource', 'update', '--ids',
            $path.Replace('https://management.azure.com', ''), '--set', 'properties.enabled=true', '--api-version', '2024-04-01')
        $null = Invoke-SidequestStagingAzure @('webapp', 'stop', '--resource-group', $script:ResourceGroup, '--name', $ApplicationName)
        $published = Publish-SidequestImmutablePackage -ApplicationName $ApplicationName -ZipPath $ZipPath -Sha256 $manifest.sha256
        $null = Invoke-SidequestStagingAzure @('webapp', 'start', '--resource-group', $script:ResourceGroup, '--name', $ApplicationName)
        $ready = $false
        for ($attempt = 0; $attempt -lt 18; $attempt++) {
            try {
                $response = Invoke-WebRequest -Uri "https://$hostname/health/ready" -TimeoutSec 15
                if ($response.StatusCode -eq 200) { $ready = $true; break }
                Write-Information "Readiness attempt $($attempt + 1)/18: category=http; status=$([int] $response.StatusCode)." -InformationAction Continue
            } catch [Microsoft.PowerShell.Commands.HttpResponseException], [Net.Http.HttpRequestException],
                [Net.WebException], [OperationCanceledException], [TimeoutException] {
                $failure = Get-SidequestSafeRequestFailure $_.Exception
                Write-Information "Readiness attempt $($attempt + 1)/18: $failure." -InformationAction Continue
            }
            Start-Sleep -Seconds 10
        }
        if (-not $ready) { throw 'SQL-backed readiness did not become healthy; the host will be stopped.' }
        return @{
            application = $ApplicationName; readiness = 'Healthy'; sourceCommit = $SourceCommit
            deploymentId = $published.deploymentId; package = $published.package; sha256 = $published.sha256
        }
    } catch {
        $null = Invoke-SidequestStagingAzure @('webapp', 'stop', '--resource-group', $script:ResourceGroup, '--name', $ApplicationName)
        throw
    }
}
