# Disabled-deployment offline parameter preflight

This utility validates inputs to `..\main.bicep` and generates an ARM parameters
document. It **does not authorize deployment, verify live approvals or constitute
a deployment workflow**. No artifact or successful exit is approval evidence.
The M4 infrastructure remains a disabled draft; no live approvals exist.

Only built-in Node modules are used. No Azure CLI, login, HTTP request, provider,
browser, resource, identity or secret operation occurs.

## Public API and CLI

`buildArmParameters(parameters, accountContext)` from `parameter-validation.mjs`
returns `{ $schema, contentVersion, parameters: { name: { value } } }`, or throws
`PreflightError` with a fixed actionable message and code. It does not mutate
inputs. `parseRequestJson(text)` parses the CLI envelope below; call the builder
to perform semantic validation.

The CLI reads **one UTF-8 JSON object from stdin** and writes only the ARM
parameters JSON to stdout. It accepts no arguments except `--help`. Errors produce
exit code **1**, a fixed message on stderr, and no generated parameters. Success
is exit code **0** with empty stderr. A downstream output-stream failure can leave
partial output: consumers must check the exit code and discard failed output.
The utility does not open or overwrite files itself.

```text
node infra\deployment\preflight.mjs < request.json > parameters.json
node infra\deployment\preflight.mjs --help
```

The first command uses shell redirection (for example, `cmd.exe` on Windows).
PowerShell can use `Get-Content -Raw request.json | node infra\deployment\preflight.mjs`;
ensure UTF-8 pipeline/output encoding, inspect `$LASTEXITCODE`, and only then
persist/consume the output. Do not log inputs or generated parameters unnecessarily.

The envelope has exactly two keys:

- `parameters`: a flat object of the Bicep parameter names below, with primitive
  values (not ARM `{ "value": ... }` wrappers).
- `accountContext`: exactly `{ "subscriptionId": "...", "tenantId": "..." }`.
  Both identifiers are mandatory and are **not** emitted in the ARM document.
  A future parent-owned OIDC workflow must derive this context from its
  authenticated deployment job's selected Azure account, **not untrusted PR
  inputs**. This offline function cannot authenticate that provenance, establish
  the selected resource group/subscription, or prevent subsequent context changes.

No deployable example identifiers, organization network ranges, location, owner,
role or group are invented here. Supply independently approved values through the
trusted release process. Never place credentials, tokens, passwords or client
secrets in either object; there are no credential parameters.

## Input contract

| Parameters | Validation/default |
| --- | --- |
| `location`, `operationalOwner`, `workforceRole`, `sqlAdministratorGroupName` | Required nonblank strings, preserved exactly; independent approval is still required |
| `environmentName` | Required, exactly `staging` or `production` |
| `workforceTenantId`, `workforceClientId`, `bootstrapAdministratorObjectId`, `sqlAdministratorGroupObjectId` | Required nonempty GUIDs |
| `virtualNetworkAddressPrefix`, `applicationSubnetAddressPrefix`, `privateEndpointSubnetAddressPrefix` | Required canonical IPv4 network CIDRs |
| `blobRestoreDays` | Required integer, **1–364 inclusive** |
| `sqlPointInTimeRetentionDays` | Required integer, **1–35 inclusive** |
| `logRetentionDays` | Required number, exactly **30, 31, 60, 90, 120, 180, 270, 365, 550, 730** |
| `enableApplication` | Omitted defaults to **false**; only explicit **false** is accepted |
| `appServiceSku` | `P1v3`, `P2v3`, `P3v3`; omitted defaults to **P1v3** |
| `sqlSku` | `S0`, `S1`, `S2`, `S3`; omitted defaults to **S1** |

Enums, retention boundaries and the three defaults come from `..\main.bicep`,
not newly inferred Azure provider limits or organizational policy. No other
defaults are supplied. `enableApplication=true` is rejected: application
activation requires a separate verified release flow.

GUID syntax is hexadecimal `8-4-4-4-12`, case-insensitive, with no braces, URN,
whitespace or all-zero GUID; supplied casing is preserved. The workforce tenant
must equal the account tenant ignoring hexadecimal case. The exact synthetic
tenant and all four persona object IDs in
`src\Sidequest.Web\Authentication\DevelopmentPersonas.cs` are rejected in **every**
GUID field. This finite denylist is not a general fake-identity detector. GUID
syntax and equality do not prove real identity, workforce status, app-role
assignment, administrator authority, group existence or group type.

CIDRs use four decimal octets **0–255**, no leading zeros, a decimal prefix
**0–32** without leading zeros, and zero host bits. Both subnets must fit entirely
inside the VNet and be disjoint, including at address boundaries. Adjacent
subnets are allowed. `/0` and `/32` are mathematical validation boundaries,
**not** a claim that Azure can provision those subnet sizes.

JSON input is limited to **16,384 UTF-8 bytes**, two object levels, no arrays or
duplicate keys (including escaped spellings). This is a local parser safety
limit, not a provider limit. Unknown keys, null/nested values, missing fields,
type coercion and explicit undefined defaults are rejected. The direct library
also requires plain data records (ordinary or null prototypes), rejecting own
symbols, accessors and non-enumerable properties. It is not an isolation boundary
against executable JavaScript such as hostile Proxies; use JSON for untrusted data.
Failure messages never interpolate supplied key names, values or raw exceptions.

## Remaining release checks

This does not replace live ARM validation/what-if, private DNS and routing checks,
existing/peered/on-premises range-overlap checks, subnet capacity and provider
size restrictions, regional SKU/runtime/backup availability, residency/cost
approval, identity/group verification or permission propagation. Organization
address-space suitability is not assessed. SQL identity/migration bootstrap,
private runner connectivity, required vault secrets, recovery tests and verified
application activation remain separate work. GitHub environment validation and
live release approval verification are not implemented here.

## Focused offline tests

From the repository root, using PowerShell-safe wildcard expansion:

```powershell
node --test (Get-ChildItem infra\deployment\tests\*.test.mjs | ForEach-Object FullName)
```

Tests use the real Node CLI via stdin/stdout, create no temporary files or
repository artifacts, and make no network calls. They do not run the independent
Bicep compiler contract suite or supply any live deployment evidence.
