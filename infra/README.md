# Azure infrastructure

**M4 draft, not deployment-ready.** `main.bicep` compiles and its compiled ARM
contracts are checked without an Azure login. Azure-hosted Data Protection and
metrics-only managed-identity export are wired behind explicit opt-in. Queue
sampling composition, provider settings, SQL identity/migration bootstrap and the
approval-gated OIDC deployment workflow remain unfinished. Do not enable or deploy
this draft as a release.

The template defines one Linux .NET 10 App Service instance with WebSockets,
Always On and affinity; private-endpoint-only SQL, Blob and Key Vault; separate
private cover/key containers; managed identities and scoped data-plane roles;
Key Vault wrapping-key infrastructure; Application Insights with local
authentication disabled; and a delegated application subnet with explicit NAT
egress. FTP and SCM basic publishing credentials are disabled.

The application is disabled by default. No resources, role assignments, secrets,
Entra registrations, GitHub deployment identities or environments have been
created by local validation. Compilation is not an Azure what-if, successful
provisioning, runtime-availability check, provider authorization or restore test.
There is no deployment action in `infrastructure.yml` and it receives no OIDC
permission.

## Required approvals and integration

Parameters require an approved region, operational owner, non-overlapping network
ranges, workforce tenant/application/role and explicit bootstrap administrator,
an Entra SQL administrator group, and data/log recovery-retention windows.
The deployment tenant and workforce tenant must be verified to match. The
[offline parameter preflight](deployment/README.md) validates GUID syntax, tenant
equality, template limits and canonical non-overlapping IPv4 ranges, and emits
only disabled-application ARM parameters. Its account context must come from the
authenticated deployment job, not untrusted PR inputs. It proves neither actual
identity/group existence nor approval, network suitability or runtime availability.
Environment approval enforcement and live Azure validation remain unfinished.

No SQL password administrator or public data-service firewall exception is
created. An approved runner with private network/DNS access must bootstrap the
application's contained SQL identity and least-privilege data access, and apply
reviewed migrations using a separate deployment identity. Never grant the running
application schema-change permissions or migrate on application startup.

Secrets such as `entra-client-secret` must already be placed in the private vault
by the approved process. Graph workforce policy/credentials and an approved ACS
endpoint/sender and managed-identity grant must be wired before those features
are considered operational. Never put secret values or cloud login credentials
in parameter files, source, command output or workflow artifacts.

The template enables `Hosting:Azure:Enabled` and emits
`Hosting:DataProtection:ApplicationName`, `:BlobUri` and `:KeyUri` for
`AddSidequestAzureHosting`. Ordinary hosts do not opt in automatically, and
Development or synthetic-authentication hosts cannot enable Azure hosting.
The application name must remain
stable across deployment restarts; the key URI is versionless. Retain old wrapping
key versions needed for decryption. Key-ring persistence uses the private Blob SDK
provider and the Key Vault XML encryptor with system-assigned managed identity,
not SAS or a developer credential chain. Native HTTPS endpoints without
credentials, query strings or fragments are required. Denied storage or wrapping
does not fall back to local/plaintext keys.

The monitoring flag is intended to enable the release sampler once composed.
The managed-identity-authenticated metrics exporter subscribes only to
`Sidequest.Operations`, retaining only the fixed `queue` dimension. It does not automatically
export traces, application logs, SQL statements or request headers, and disables
local offline telemetry spooling. Named exporter options avoid mixing future
signal settings. The template sets `APPLICATIONINSIGHTS_STATSBEAT_DISABLED=true`
and `APPLICATIONINSIGHTS_SDKSTATS_DISABLED=true`; enabled hosting rejects missing
process-level opt-outs. JSON configuration alone cannot disable these SDK
diagnostics, which otherwise collect additional statistics and can contact
Microsoft-owned diagnostic endpoints outside the application's telemetry resource.
Live metrics, standard metrics and performance counters are also explicitly disabled.
A connection string and role assignment alone do not prove
telemetry delivery. Data-plane access propagation, key-reference resolution and
actual ingestion must be checked before enabling the app. Application latency and
other release signals still need separate instrumentation and acceptance evidence.

Local hosting checks use the actual Azure Data Protection repository/encryptor with
synthetic storage and RSA-backed key resolvers: restart, wrapping-key rotation,
application isolation, encrypted stored XML and fail-closed behavior are exercised.
These are not live Blob/Key Vault permission or recovery evidence. Hosting pins
Azure Data Protection Blobs 1.5.4, Keys 1.6.4, Azure Identity 1.21.0 and Azure Monitor
Exporter 1.9.0 (MIT), plus OpenTelemetry.Extensions.Hosting 1.18.0 (Apache-2.0).

## Recovery and cost boundaries

Blob versioning, change feed and point-in-time restore are enabled, with soft
deletion retained one day beyond the approved restore window. No lifecycle
content-deletion policy is installed; versioned data can grow until an approved
retention policy is implemented. SQL point-in-time and log retention are explicit
parameters. Neither configuration proves RPO <= 1 hour or RTO <= 4 hours.

The baseline is one Premium v3 App Service instance and Standard SQL, not an HA or
300-user performance claim. Plan, database, ZRS storage, private endpoints, NAT,
public egress IP, Key Vault and log ingestion incur charges. Review a real Azure
what-if and cost estimate with the deployment owner before any apply. Check
regional SKU, zone-redundant storage, backup and .NET 10 runtime availability.

## Offline validation

Use Bicep **0.47.16**. The validation workflow downloads the Linux compiler from
the official Azure/Bicep release and verifies its published SHA-256 digest before
execution. Local Windows validation used the same release's Windows x64 compiler,
also checked against its published digest.

```powershell
bicep build infra\main.bicep --outfile "$env:TEMP\sidequest-infrastructure.json"
$env:SIDEQUEST_ARM_TEMPLATE = "$env:TEMP\sidequest-infrastructure.json"
node --test infra\tests\template.test.mjs
```

These checks inspect the compiled deployment shape: network exposure, keyless
identities, private containers, explicit retention, secure App Service settings
and the disabled default. They do not simulate ARM/provider behavior or claim
that Azure deployment prerequisites have been satisfied.
