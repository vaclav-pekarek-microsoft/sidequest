# Azure infrastructure

**M4 draft, not deployment-ready.** `main.bicep` compiles and its compiled ARM
contracts are checked without an Azure login. Azure-hosted Data Protection and
metrics-only managed-identity export and queue sampling are wired behind explicit
opt-in. Provider settings, SQL identity/migration bootstrap and the
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

The accepted planning approval model (handoff decision D39) uses GitHub environment
reviews plus deployment-owner manual verification of administrator-bypass controls.
Supported API metadata does not prove that bypass is disabled or distinguish every
bypass from a normal approval. An explicit owner-controlled acknowledgement is a
trusted operator assertion, not API evidence. Keep planning disabled until those
controls and the deployment identity are verified; disable and reverify it after
relevant changes. This model trusts authorized repository administrators and does
not claim protection against an administrator who changes settings or workflow code.
No environment, identity, owner assignment or live planning authorization is supplied
by this decision.

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

The monitoring flag enables the release sampler with a default 30-second interval.
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

## Readiness and queue observations

Operational persistence adapters are always registered, including when monitoring
is disabled. `/health/ready` checks SQL access and ordered applied migration history
against the compiled migration set. Missing, pending, unknown or absent compiled
migrations are unready. This is not a manual schema-drift auditor, and never applies
migrations. `/health/live` remains process-only; both endpoints expose status only.
Neither probe calls Graph, Blob or email providers.

`Operations:Monitoring:Enabled` defaults false. Enabled sampling uses a fresh scope
and SQL context per sequential attempt. `Operations:Monitoring:SampleInterval`
defaults to `00:00:30`, accepts 5–300 seconds, and is fixed until restart. The first
attempt is immediate; later delays begin after completion. SQL command timeout is
five seconds; opening retains the configured connection timeout and cancellation.

Each queue (`outbox`, `scheduled`, `delivery`) emits `sidequest.queue.` gauges:
`pending`, `due`, `dead_letter`, `oldest_due_age`, `observation_available`,
`observation_stale`, and `observation_age`. Counts are work items; ages are seconds.
Due work includes eligible Pending rows and expired Processing leases with fewer
than eight attempts. Reads do not claim work or make an atomic cross-queue snapshot.

Alert/dashboard requirements, not yet deployed alert rules:

- Gate backlog interpretation on `observation_available=1` and
  `observation_stale=0`. Staleness begins only after twice the interval.
- Missing observations, disabled sampling, exporter failures and stale/failed
  attempts are unavailable, not healthy zeros. Backlog gauges are absent then;
  actual empty queues yield zero counts and no oldest-due-age point.
- Observation age continues since the last success through failures. Never carry
  forward an old backlog as a current healthy sample or sum copies across instances.
- Oldest-due age is frozen at the sample cutoff, not mailbox arrival, completed
  delivery latency or a reminder business deadline. It does not establish the
  two-minute release targets.

Application latency, authorization/error signals, Graph throttling, image failures,
email rejections and live alert delivery still require release work and evidence.

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
