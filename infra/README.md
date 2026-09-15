# Azure infrastructure

**M4 draft, not deployment-ready.** `main.bicep` compiles and its compiled ARM
contracts are checked without an Azure login. Azure-hosted Data Protection,
authenticated telemetry export, provider settings, SQL identity/migration
bootstrap and the approval-gated OIDC deployment workflow still require host and
deployment integration. Do not enable or deploy this draft as a release.

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
The deployment tenant and workforce tenant must be verified to match. Real GUID
validation, environment approval enforcement and network-range validation belong
to the forthcoming deployment preflight, not this compiler check.

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

The template emits `Hosting:DataProtection:ApplicationName`, `:BlobUri` and
`:KeyUri` for forthcoming host registration. The application name must remain
stable across deployment restarts; the key URI is versionless. Retain old wrapping
key versions needed for decryption. Provisioning a key or container alone does
not make the application's key ring durable or encrypted.

The monitoring flag is intended to enable the release sampler once composed.
Application Insights requires the forthcoming managed-identity-authenticated
exporter: a connection string and role assignment alone do not prove telemetry
delivery. Data-plane access propagation, key-reference resolution and actual
telemetry ingestion must be checked before enabling the app.

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
