#requires -Version 7.4
<#
.SYNOPSIS
Applies a reviewed EF SQL artifact and bounded runtime permissions to approved dev/test SQL.
.DESCRIPTION
Run only after PR acceptance and operator authorization of restricted firewall access.
Uses the signed-in Entra operator, not the migration managed identity. No resources,
firewalls, directory grants, retries or migration-identity permissions are created.
Tokens remain in process memory; do not run under a debugger or PowerShell tracing.
After any failure, stop and inspect the database before explicitly authorizing a rerun.
.PARAMETER ServerName
The provisioned sidequest-sql-<suffix> logical server, without a DNS suffix.
.PARAMETER MigrationSqlPath
Exact local path to the reviewed UTF-8 idempotent EF migrations SQL artifact.
.PARAMETER MigrationSqlSha256
Independently reviewed SHA-256 of the artifact's exact bytes.
.PARAMETER ValidateOnly
Checks local inputs and SQL batch syntax only. Makes no Azure or SQL calls.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ServerName,
    [Parameter(Mandatory)][string] $MigrationSqlPath,
    [Parameter(Mandatory)][string] $MigrationSqlSha256,
    [switch] $ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$subscription = 'b75472bd-4174-4f66-b159-bae420212abc'
$tenant = '99e674a6-6773-4f53-90a3-e3ab8c37c856'
$resourceGroup = 'sidequest-rg'
$database = 'sidequest'
$phase = 'local-preflight'
$connection = $null
$tokenResponse = $null

function Split-ReviewedSql {
    param([string] $Sql)

    # EF uses GO, including dynamic SQL strings that can span lines. Never split a
    # quoted GO, interpret SQLCMD directives, or support GO repeat counts.
    $batches = [System.Collections.Generic.List[string]]::new()
    $batch = [System.Text.StringBuilder]::new()
    $quote = [char]0
    $commentDepth = 0
    foreach ($line in ($Sql -split '\r?\n')) {
        if ($quote -eq [char]0 -and $commentDepth -eq 0) {
            if ($line -match '^\s*GO\s*(?:--.*)?$') {
                if ($batch.ToString().Trim().Length -gt 0) { $batches.Add($batch.ToString()) }
                [void] $batch.Clear()
                continue
            }
            if ($line -match '^\s*(?:GO\b|USE\b|:|!!)' -or $line -match '\$\(') {
                throw 'Unsupported artifact directive.'
            }
        }
        for ($i = 0; $i -lt $line.Length; $i++) {
            $character = $line[$i]
            $next = if ($i + 1 -lt $line.Length) { $line[$i + 1] } else { [char]0 }
            if ($commentDepth -gt 0) {
                if ($character -eq '/' -and $next -eq '*') { $commentDepth++; $i++ }
                elseif ($character -eq '*' -and $next -eq '/') { $commentDepth--; $i++ }
            }
            elseif ($quote -ne [char]0) {
                if ($character -eq $quote) {
                    if ($next -eq $quote) { $i++ } else { $quote = [char]0 }
                }
            }
            elseif ($character -eq '-' -and $next -eq '-') { break }
            elseif ($character -eq '/' -and $next -eq '*') { $commentDepth++; $i++ }
            elseif ($character -eq "'" -or $character -eq '"') { $quote = $character }
            elseif ($character -eq '[') { $quote = ']' }
        }
        [void] $batch.AppendLine($line)
    }
    if ($quote -ne [char]0 -or $commentDepth -ne 0) { throw 'Unterminated artifact token.' }
    if ($batch.ToString().Trim().Length -gt 0) { $batches.Add($batch.ToString()) }
    if ($batches.Count -eq 0) { throw 'Empty artifact.' }
    return $batches.ToArray()
}

function Invoke-PinnedAz {
    param([string[]] $Arguments)
    $output = & az @Arguments --subscription $subscription --only-show-errors --output json 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Azure preflight failed.' }
    return ($output -join "`n" | ConvertFrom-Json)
}

function Invoke-SqlBatch {
    param([string] $Sql, [hashtable] $Parameters = @{})
    $command = $connection.CreateCommand()
    try {
        $command.CommandTimeout = 120
        $command.CommandText = $Sql
        foreach ($name in $Parameters.Keys) {
            $parameter = $command.Parameters.Add($name, [System.Data.SqlDbType]::UniqueIdentifier)
            $parameter.Value = $Parameters[$name]
        }
        [void] $command.ExecuteNonQuery()
    }
    finally { $command.Dispose() }
}

try {
    if ($ServerName -cnotmatch '^sidequest-sql-[a-z0-9](?:[a-z0-9-]{0,45}[a-z0-9])?$') {
        throw 'Unexpected server name.'
    }
    if ($MigrationSqlSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid artifact hash.' }
    $artifact = Get-Item -LiteralPath $MigrationSqlPath
    if ($artifact.PSIsContainer -or $artifact.Length -eq 0 -or $artifact.Length -gt 10MB) {
        throw 'Invalid artifact size.'
    }
    # Hash and execute the same in-memory bytes, eliminating a file-change window.
    $bytes = [System.IO.File]::ReadAllBytes($artifact.FullName)
    $actualHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
    if ($actualHash -ine $MigrationSqlSha256) { throw 'Artifact hash mismatch.' }
    $sql = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes).TrimStart([char]0xFEFF)
    $batches = @(Split-ReviewedSql $sql)
    if ($sql -notmatch '__EFMigrationsHistory' -or $sql -notmatch 'IF NOT EXISTS') {
        throw 'Expected an idempotent EF migration artifact.'
    }
    $permissionsSql = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'runtime-permissions.sql'))
    if ($ValidateOnly) {
        Write-Output "Local artifact validated; batches=$($batches.Count); sha256=$actualHash. No Azure or SQL calls."
        exit 0
    }

    $phase = 'azure-target-preflight'
    $account = Invoke-PinnedAz -Arguments @('account', 'show')
    if ($account.id -ine $subscription -or $account.tenantId -ine $tenant -or
        $account.state -ne 'Enabled' -or $account.user.type -ne 'user') {
        throw 'Expected the approved tenant and an interactive operator identity.'
    }
    $serverId = "/subscriptions/$subscription/resourceGroups/$resourceGroup/providers/Microsoft.Sql/servers/$ServerName"
    $server = Invoke-PinnedAz -Arguments @('sql', 'server', 'show', '--name', $ServerName, '--resource-group', $resourceGroup)
    if ($server.id -ine $serverId -or $server.name -cne $ServerName -or
        $server.location -ine 'westus3' -or $server.fullyQualifiedDomainName -ine "$ServerName.database.windows.net" -or
        $server.minimalTlsVersion -ne '1.2') {
        throw 'SQL server target mismatch.'
    }
    # The CLI ad-admin list projection omits principalType; inspect the ARM contract.
    $armServer = Invoke-PinnedAz -Arguments @('rest', '--method', 'get', '--url',
        "https://management.azure.com/$($serverId.TrimStart('/'))?api-version=2023-08-01")
    $admins = @($armServer.properties.administrators)
    $entraOnly = Invoke-PinnedAz -Arguments @('sql', 'server', 'ad-only-auth', 'get', '--name', $ServerName, '--resource-group', $resourceGroup)
    if ($admins.Count -ne 1 -or $admins[0].tenantId -ine $tenant -or
        $admins[0].principalType -ne 'Group' -or $entraOnly.azureAdOnlyAuthentication -ne $true) {
        throw 'Expected Entra-only group administration.'
    }
    $db = Invoke-PinnedAz -Arguments @('sql', 'db', 'show', '--server', $ServerName, '--name', $database, '--resource-group', $resourceGroup)
    if ($db.id -ine "$serverId/databases/$database" -or $db.name -cne $database -or
        $db.location -ine 'westus3' -or $db.sku.name -ne 'Basic' -or
        $db.sku.tier -ne 'Basic' -or $db.sku.capacity -ne 5 -or [long]$db.maxSizeBytes -ne 2147483648 -or
        $db.status -ne 'Online') {
        throw 'Database target or bounded SKU mismatch.'
    }
    $identities = @{}
    foreach ($identityName in @('sidequest-app', 'sidequest-migration')) {
        $identity = Invoke-PinnedAz -Arguments @('identity', 'show', '--name', $identityName, '--resource-group', $resourceGroup)
        $expectedId = "/subscriptions/$subscription/resourceGroups/$resourceGroup/providers/Microsoft.ManagedIdentity/userAssignedIdentities/$identityName"
        if ($identity.id -ine $expectedId -or $identity.name -cne $identityName -or
            $identity.tenantId -ine $tenant -or $identity.location -ine 'westus3' -or
            [guid]$identity.principalId -eq [guid]::Empty -or [guid]$identity.clientId -eq [guid]::Empty) {
            throw 'Managed identity target mismatch.'
        }
        $identities[$identityName] = $identity
    }
    if ($identities['sidequest-app'].principalId -eq $identities['sidequest-migration'].principalId -or
        $identities['sidequest-app'].clientId -eq $identities['sidequest-migration'].clientId) {
        throw 'Managed identities must be distinct.'
    }

    $phase = 'sql-token'
    $tokenResponse = Invoke-PinnedAz -Arguments @('account', 'get-access-token', '--resource', 'https://database.windows.net/')
    if ($tokenResponse.tenant -ine $tenant -or $tokenResponse.subscription -ine $subscription -or
        [string]::IsNullOrWhiteSpace($tokenResponse.accessToken)) {
        throw 'Unexpected token metadata.'
    }
    $phase = 'sql-connect'
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $builder.DataSource = "tcp:$ServerName.database.windows.net,1433"
    $builder.InitialCatalog = $database
    $builder.Encrypt = $true
    $builder.TrustServerCertificate = $false
    $builder.ConnectTimeout = 30
    $builder.ConnectRetryCount = 0
    $builder.Pooling = $false
    $builder.ApplicationName = 'Sidequest.BudgetStaging.Bootstrap'
    $connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    $connection.AccessToken = $tokenResponse.accessToken
    $tokenResponse = $null
    $connection.Open()
    $phase = 'sql-context'
    Invoke-SqlBatch "IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'sidequest' OR USER_NAME() <> N'dbo' THROW 51000, 'Expected approved database administrator context.', 1;"
    $batchNumber = 0
    foreach ($batch in $batches) {
        $batchNumber++
        $phase = "migration-batch-$batchNumber"
        Invoke-SqlBatch $batch
    }
    $phase = 'migration-completion'
    Invoke-SqlBatch "IF @@TRANCOUNT <> 0 OR DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'sidequest' THROW 51001, 'Migration context or transaction mismatch.', 1;"
    $phase = 'runtime-permissions-and-verification'
    Invoke-SqlBatch $permissionsSql @{ '@RuntimeObjectId' = [guid]$identities['sidequest-app'].principalId }
    Write-Output 'Bootstrap completed using the Entra operator. Runtime database impersonation checks passed.'
    Write-Output 'Migration managed identity was ARM-verified only: no SQL user/grants or actual MI authentication/execution were proved.'
}
catch {
    $exception = $_.Exception
    $sqlNumber = 'none'
    while ($null -ne $exception) {
        if ($exception -is [System.Data.SqlClient.SqlException]) {
            $sqlNumber = [string]$exception.Number
            break
        }
        $exception = $exception.InnerException
    }
    # Never emit the original exception, SQL text, connection string or token.
    [Console]::Error.WriteLine("Bootstrap failed; phase=$phase; sql-number=$sqlNumber. Stop and inspect partial state; no automatic retry or rollback.")
    exit 1
}
finally {
    $tokenResponse = $null
    if ($null -ne $connection) { $connection.Dispose() }
}
