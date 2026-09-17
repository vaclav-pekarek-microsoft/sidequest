import assert from "node:assert/strict";
import { createHash, randomUUID } from "node:crypto";
import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import test, { after } from "node:test";

const directory = dirname(fileURLToPath(import.meta.url));
const bootstrap = join(directory, "bootstrap.ps1");
const source = readFileSync(bootstrap, "utf8");
const permissions = readFileSync(join(directory, "runtime-permissions.sql"), "utf8");
const scratch = join(directory, `.bootstrap-tests-${randomUUID()}`);
mkdirSync(scratch);
after(() => rmSync(scratch, { recursive: true, force: true }));
const literal = value => `'${value.replaceAll("'", "''")}'`;
const migration = "IF NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory])\nSELECT 1;\nGO\nSELECT 2;\n";

function powershell(command) {
    const result = spawnSync("pwsh", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
        { encoding: "utf8", timeout: 30_000 });
    assert.ifError(result.error);
    return result;
}

function validate(sql = migration, overrides = {}) {
    const path = join(scratch, `${randomUUID()}.sql`);
    writeFileSync(path, sql);
    const hash = overrides.hash ?? createHash("sha256").update(sql).digest("hex");
    return powershell(`& ${literal(bootstrap)} -ServerName ${literal(overrides.server ?? "sidequest-sql-test123")}
        -MigrationSqlPath ${literal(path)} -MigrationSqlSha256 ${literal(hash)} -ValidateOnly`.replaceAll("\n", " "));
}

function loadFunction(name) {
    return `
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(${literal(bootstrap)}, [ref]$null, [ref]$null)
        $function = $ast.Find({ param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq '${name}'
        }, $true)
        Invoke-Expression $function.Extent.Text
    `;
}

test("local validation accepts only the exact reviewed bytes without cloud access", () => {
    const result = validate();
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /batches=2;/);
    assert.match(result.stdout, /No Azure or SQL calls/);
    assert.match(result.stdout, new RegExp(createHash("sha256").update(migration).digest("hex"), "i"));
});

test("artifact hash, server bounds and EF structure failures exit sanitized", () => {
    for (const result of [
        validate(migration, { hash: "0".repeat(64) }),
        validate(migration, { hash: "not-a-hash" }),
        validate(migration, { server: "production-server" }),
        validate(migration, { server: "sidequest-sql-test.database.windows.net" }),
        validate(migration, { server: "sidequest-sql-test;Password=secret" }),
        validate("SELECT N'sensitive-content';"),
        validate(""),
        validate(Buffer.from([0xff, 0xfe, 0x61])),
    ]) {
        assert.equal(result.status, 1);
        assert.match(result.stderr, /phase=local-preflight; sql-number=none/);
        assert.doesNotMatch(result.stdout + result.stderr, /sensitive-content|Password=secret|not-a-hash/);
    }
});

test("batch parser preserves quoted GO, escaped delimiters and nested comments", () => {
    const sql = "SELECT N'first\nGO\nlast''quote';\n/* outer\n/* nested */\nGO\n*/\nSELECT [a]]b], \"c\"\"d\";\nGO -- boundary\nSELECT 2;";
    const result = powershell(`${loadFunction("Split-ReviewedSql")}
        $batches = @(Split-ReviewedSql ${literal(sql)})
        ConvertTo-Json -InputObject $batches -Compress`);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(result.stdout), [
        "SELECT N'first\nGO\nlast''quote';\n/* outer\n/* nested */\nGO\n*/\nSELECT [a]]b], \"c\"\"d\";\n".replaceAll("\n", "\r\n"),
        "SELECT 2;\r\n",
    ].map(value => process.platform === "win32" ? value : value.replaceAll("\r\n", "\n")));
});

test("batch parser rejects SQLCMD, replay counts, context switches and malformed tokens", () => {
    for (const suffix of [":r other.sql", "!! echo leak", "GO 2", "USE master;", "SELECT $(Input);",
        "SELECT 'unterminated", "/* unterminated", "SELECT [unterminated"]) {
        const result = validate(migration + suffix);
        assert.equal(result.status, 1, suffix);
        assert.match(result.stderr, /phase=local-preflight; sql-number=none/);
        assert.doesNotMatch(result.stdout, /Local artifact validated/);
    }
});

const subscription = "b75472bd-4174-4f66-b159-bae420212abc";
const tenant = "99e674a6-6773-4f53-90a3-e3ab8c37c856";
const serverId = `/subscriptions/${subscription}/resourceGroups/sidequest-rg/providers/Microsoft.Sql/servers/sidequest-sql-test123`;
const identities = {
    "sidequest-app": {
        principalId: "00000000-0000-0000-0000-000000000001",
        clientId: "00000000-0000-0000-0000-000000000002",
    },
    "sidequest-migration": {
        principalId: "00000000-0000-0000-0000-000000000003",
        clientId: "00000000-0000-0000-0000-000000000004",
    },
};

function preflight(overrides = {}) {
    const path = join(scratch, `${randomUUID()}.sql`);
    writeFileSync(path, migration);
    const responses = {
        account: { id: subscription, tenantId: tenant, state: "Enabled", user: { type: "user" } },
        server: { id: serverId, name: "sidequest-sql-test123", location: "westus3",
            fullyQualifiedDomainName: "sidequest-sql-test123.database.windows.net", minimalTlsVersion: "1.2" },
        admins: [{ tenantId: tenant, principalType: "Group" }],
        entraOnly: { azureAdOnlyAuthentication: true },
        db: { id: `${serverId}/databases/sidequest`, name: "sidequest", location: "westus3",
            sku: { name: "Basic", tier: "Basic", capacity: 5 }, maxSizeBytes: 2147483648, status: "Online" },
        ...Object.fromEntries(Object.entries(identities).map(([name, ids]) => [name, {
            id: `/subscriptions/${subscription}/resourceGroups/sidequest-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/${name}`,
            name, tenantId: tenant, location: "westus3", ...ids,
        }])),
    };
    for (const [key, value] of Object.entries(overrides)) Object.assign(responses[key], value);
    const command = `
        $responses = ConvertFrom-Json -AsHashtable ${literal(JSON.stringify(responses))}
        function global:az {
            $arguments = @($args)
            $subscriptionIndex = [Array]::IndexOf($arguments, '--subscription')
            if ($subscriptionIndex -lt 0 -or $arguments[$subscriptionIndex + 1] -ne '${subscription}') {
                throw 'Missing explicit subscription'
            }
            if ($arguments -contains 'get-access-token') {
                if ($arguments -contains '--tenant' -or
                    $arguments[[Array]::IndexOf($arguments, '--resource') + 1] -ne 'https://database.windows.net/') {
                    throw 'Unexpected token scope'
                }
                [Console]::Out.WriteLine('MOCK:token-boundary')
                # Deliberately invalid metadata: this test must NEVER reach SQL.
                return '{"tenant":"wrong-tenant","subscription":"${subscription}","accessToken":"DO-NOT-EMIT-MOCK-TOKEN"}'
            }
            $key = if ($arguments[0] -eq 'account') { 'account' }
                elseif ($arguments[0] -eq 'identity') { $arguments[[Array]::IndexOf($arguments, '--name') + 1] }
                elseif ($arguments[1] -eq 'db') { 'db' }
                elseif ($arguments[2] -eq 'ad-admin') { 'admins' }
                elseif ($arguments[2] -eq 'ad-only-auth') { 'entraOnly' }
                else { 'server' }
            [Console]::Out.WriteLine("MOCK:$key")
            $global:LASTEXITCODE = 0
            ConvertTo-Json -InputObject $responses[$key] -Depth 8 -Compress
        }
        & ${literal(bootstrap)} -ServerName sidequest-sql-test123 -MigrationSqlPath ${literal(path)}
            -MigrationSqlSha256 ${literal(createHash("sha256").update(migration).digest("hex"))}
    `.replace(/-MigrationSqlPath ([^\n]+)\n\s+-MigrationSqlSha256/, "-MigrationSqlPath $1 -MigrationSqlSha256");
    return powershell(command);
}

test("ARM preflight pins every read and validates both identities before acquiring a memory-only token", () => {
    const result = preflight();
    assert.equal(result.status, 1);
    assert.deepEqual(result.stdout.trim().split(/\r?\n/), [
        "MOCK:account", "MOCK:server", "MOCK:admins", "MOCK:entraOnly", "MOCK:db",
        "MOCK:sidequest-app", "MOCK:sidequest-migration", "MOCK:token-boundary",
    ]);
    assert.match(result.stderr, /phase=sql-token; sql-number=none/);
    assert.doesNotMatch(result.stdout + result.stderr, /DO-NOT-EMIT-MOCK-TOKEN/);
});

test("ARM tenant, account, server, database, SKU and identity drift stop before token acquisition", () => {
    const cases = [
        { account: { id: randomUUID() } },
        { account: { tenantId: randomUUID() } },
        { account: { user: { type: "servicePrincipal" } } },
        { server: { id: serverId.replace("sidequest-rg", "unapproved") } },
        { server: { location: "eastus" } },
        { server: { fullyQualifiedDomainName: "unapproved.database.windows.net" } },
        { entraOnly: { azureAdOnlyAuthentication: false } },
        { db: { name: "SidequestDevelopment" } },
        { db: { maxSizeBytes: 2147483649 } },
        { db: { sku: { name: "S0", tier: "Standard", capacity: 10 } } },
        { "sidequest-app": { principalId: "00000000-0000-0000-0000-000000000000" } },
        { "sidequest-app": { tenantId: randomUUID() } },
        { "sidequest-migration": { id: "wrong-resource-id" } },
        { "sidequest-migration": { principalId: identities["sidequest-app"].principalId } },
    ];
    for (const overrides of cases) {
        const result = preflight(overrides);
        assert.equal(result.status, 1, JSON.stringify(overrides));
        assert.match(result.stderr, /phase=azure-target-preflight; sql-number=none/);
        assert.doesNotMatch(result.stdout, /MOCK:token-boundary/);
    }
});

test("a failed SQL batch is disposed and never retried or followed by another execution", () => {
    const result = powershell(`${loadFunction("Invoke-SqlBatch")}
        $global:executions = 0
        $global:disposed = $false
        $global:mockCommand = [pscustomobject]@{ CommandTimeout = 0; CommandText = '' }
        $mockCommand | Add-Member ScriptMethod ExecuteNonQuery {
            $global:executions++
            throw 'Simulated partial batch failure'
        }
        $mockCommand | Add-Member ScriptMethod Dispose { $global:disposed = $true }
        $connection = [pscustomobject]@{}
        $connection | Add-Member ScriptMethod CreateCommand { return $global:mockCommand }
        $failed = $false
        try { Invoke-SqlBatch 'SELECT 1;' } catch { $failed = $true }
        @{ executions = $executions; disposed = $disposed; failed = $failed;
            timeout = $mockCommand.CommandTimeout; sql = $mockCommand.CommandText } | ConvertTo-Json -Compress`);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(result.stdout), {
        executions: 1, disposed: true, failed: true, timeout: 120, sql: "SELECT 1;",
    });
});

test("runtime identity is bound as a GUID parameter rather than interpolated into SQL", () => {
    const result = powershell(`${loadFunction("Invoke-SqlBatch")}
        $global:parameter = [pscustomobject]@{ Value = $null }
        $parameters = [pscustomobject]@{}
        $parameters | Add-Member ScriptMethod Add {
            param($name, $type)
            $global:parameterName = $name
            $global:parameterType = $type.ToString()
            return $global:parameter
        }
        $global:mockCommand = [pscustomobject]@{ CommandTimeout = 0; CommandText = ''; Parameters = $parameters }
        $mockCommand | Add-Member ScriptMethod ExecuteNonQuery { return 0 }
        $mockCommand | Add-Member ScriptMethod Dispose {}
        $connection = [pscustomobject]@{}
        $connection | Add-Member ScriptMethod CreateCommand { return $global:mockCommand }
        Invoke-SqlBatch 'SELECT @RuntimeObjectId;' @{ '@RuntimeObjectId' = [guid]'${identities["sidequest-app"].principalId}' }
        @{ name = $parameterName; type = $parameterType; value = $parameter.Value.ToString();
            sql = $mockCommand.CommandText } | ConvertTo-Json -Compress`);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(result.stdout), {
        name: "@RuntimeObjectId", type: "UniqueIdentifier",
        value: identities["sidequest-app"].principalId, sql: "SELECT @RuntimeObjectId;",
    });
});

test("runtime DML allowlist exactly matches all 26 EF application tables and excludes history writes", () => {
    const snapshot = readFileSync(join(directory, "..", "..", "src", "Sidequest.Infrastructure",
        "Persistence", "Migrations", "SidequestDbContextModelSnapshot.cs"), "utf8");
    const tables = [...snapshot.matchAll(/\.ToTable\("([^"]+)"/g)].map(match => match[1]).sort();
    const grants = [...permissions.matchAll(/GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo\.(\w+) TO \[sidequest_runtime\];/g)]
        .map(match => match[1]).sort();
    assert.equal(tables.length, 26);
    assert.deepEqual(grants, tables);
    const allowlist = permissions.slice(permissions.indexOf("INSERT @Tables"), permissions.indexOf("IF EXISTS"));
    assert.deepEqual([...allowlist.matchAll(/\(N'([^']+)'\)/g)].map(match => match[1]).sort(), tables);
    assert.equal([...permissions.matchAll(/^GRANT /gm)].length, 27);
    assert.match(permissions, /GRANT SELECT ON OBJECT::dbo\.__EFMigrationsHistory TO \[sidequest_runtime\];/);
    assert.doesNotMatch(permissions, /GRANT\s+.*SCHEMA::|db_owner|db_datareader|db_datawriter|CREATE LOGIN|CREATE USER.*WITH SID/i);
});

test("runtime SQL rejects identity and grant drift and verifies DDL and history boundaries under impersonation", () => {
    assert.match(permissions, /sid <> CONVERT\(binary\(16\), @RuntimeObjectId\)/);
    assert.match(permissions, /FROM EXTERNAL PROVIDER WITH OBJECT_ID/);
    assert.match(permissions, /THROW 51014, 'Existing runtime user identity mismatch.'/);
    assert.match(permissions, /@RoleId IS NULL OR role_principal_id <> @RoleId/);
    assert.match(permissions, /@UserId IS NULL OR member_principal_id <> @UserId/);
    assert.match(permissions, /@UserId IS NOT NULL AND p\.grantee_principal_id/);
    assert.match(permissions, /@RoleId IS NOT NULL AND p\.grantee_principal_id/);
    assert.match(permissions, /THROW 51019, 'Unexpected existing runtime permission.'/);
    const proof = permissions.slice(permissions.indexOf("EXECUTE AS USER"));
    assert.match(proof, /N'DATABASE', N'CREATE TABLE'/);
    assert.match(proof, /N'SCHEMA', N'ALTER'/);
    assert.match(proof, /N'SCHEMA', N'CONTROL'/);
    for (const permission of ["INSERT", "UPDATE", "DELETE", "ALTER", "CONTROL"]) {
        assert.ok(proof.includes(`N'dbo.__EFMigrationsHistory', N'OBJECT', N'${permission}'), 1) <> 0`));
    }
    assert.match(proof, /BEGIN CATCH\s+IF @Impersonating = 1 REVERT;\s+THROW;/);
    assert.doesNotMatch(source, /Start-Transcript|Write-(?:Output|Host|Error).*accessToken|&\s*sqlcmd/i);
    assert.match(source, /\$connection\.AccessToken = \$tokenResponse\.accessToken/);
    assert.match(source, /\$builder\.ConnectRetryCount = 0/);
    assert.match(source, /\$builder\.Pooling = \$false/);
});
