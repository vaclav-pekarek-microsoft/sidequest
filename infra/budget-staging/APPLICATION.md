# Owner-supervised hackathon application deployment

This is the approved **separate Staging application**, not a production release.
All Azure resources remain in subscription
`b75472bd-4174-4f66-b159-bae420212abc`, tenant
`99e674a6-6773-4f53-90a3-e3ab8c37c856`, resource group `sidequest-rg`.
Regional resources use **West US 3**. ACS requires ARM location `global`,
with email/communication data location **United States**; it is not a West US 3
regional service. App registrations are tenant directory objects, not resources
that can be placed in an Azure resource group.

The application name is `sidequest-hackathon-b7ljjkoqcaedc`, using the same
subscription/resource-group suffix as the accepted SQL deployment. Verify the
actual template output before registering or publishing.

## What changes, and what does not

- One Linux **B1**, always-on App Service with HTTPS, WebSockets, affinity,
  TLS 1.2 minimum, disabled FTP/SCM basic authentication, and readiness probing.
  The template **always leaves the app disabled**; rerunning Apply disables it.
  `Hosting:Azure:AppServiceProxyEnabled=true` is an explicit Staging-only
  opt-in that restores the external HTTPS scheme before redirects/authentication.
  It trusts at most one `X-Forwarded-Proto` value from a known private
  App Service proxy address (RFC1918/loopback), never forwarded host or a public/
  unknown peer. Do not enable a trust-all forwarded-header environment switch.
- A system-assigned identity for Blob, Key Vault, metrics and ACS; the existing
  `sidequest-app` user-assigned identity is attached for SQL only. SQL uses the
  actual identity **client ID** in `User Id`, managed-identity authentication,
  encryption and certificate validation.
- LRS storage with private `covers` and `data-protection` containers, seven-day
  deletion retention and blob versioning. Anonymous blobs and shared-key
  authentication are disabled. Public HTTPS identity-protected Blob/Key Vault
  endpoints are intentional; **private containers do not mean private endpoints**.
  No private endpoints, NAT, network integration or premium plan are added.
- A dedicated RBAC Key Vault, purge protection and seven-day soft delete,
  versionless RSA wrapping key, retained encrypted Data Protection key ring.
  Blob grants are container-scoped; wrapping is key-scoped; secret reads are
  limited to this dedicated vault. No plaintext/local-key fallback is introduced.
- Metrics-only Application Insights, disabled local authentication, a 30-day
  Log Analytics workspace and a **0.1 GB daily ingestion cap**. This cap can
  overshoot, is not a spending ceiling, and may temporarily suppress metrics.
  Existing process-level Statsbeat/SDK statistics opt-outs remain required.
  There is no automatic request/dependency/log exporter.
- ACS Email with an Azure-managed domain and `DoNotReply@<fromSenderDomain>`,
  United States data location, no engagement tracking, no custom DNS, Graph
  Mail.Send, Outlook mailbox or connection-string key.
- No SQL schema/admin/MI privilege changes. SQL remains Basic 5 DTUs/2 GB with
  its accepted migrations and runtime grants. The firewall helper manages only
  `sq-hackathon-<actual-IPv4>` rules and refuses unrelated existing rules.
  No zero-address Azure exception, ranges, possible-outbound list or operator
  address is admitted. Re-review after any App Service hosting change.

B1 + SQL retail guidance remains approximately USD 18/month before
usage-dependent storage, Key Vault, email, telemetry and transfers. No hard
USD 30 ceiling or production capacity/recovery claim is made.

## Isolated assigned-participant admission

The owner explicitly approved `hackathon-assigned-users`, including the owner's
tenant **Guest** account. This is not proof of employment and does not modify
the default workforce policy:

| Setting | Required value |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Staging` |
| `Authentication:Mode` | `Entra` |
| `Authentication:AdmissionPolicy` | `hackathon-assigned-users` |
| `Authentication:HackathonRole` | `Sidequest.Hackathon.Participant` |
| `Authentication:HackathonParticipants:0` | `1250fe10-b814-4735-801f-ea5a0a4c1219` |
| `AzureAd:TenantId` | `99e674a6-6773-4f53-90a3-e3ab8c37c856` |
| `AzureAd:ClientId` | The newly verified single-tenant registration |
| `AzureAd:ClientSecret` | Key Vault reference to `entra-client-secret` |
| `Authentication:BootstrapAdministrator:TenantId` | Same approved tenant |
| `Authentication:BootstrapAdministrator:ObjectId` | Same explicitly approved owner |

Each sign-in requires validated issuer/audience, exact tenant/object,
the **dedicated role claim**, and membership of the immutable configured
participant list. Tenant membership, email, a workforce role, Global
Administrator or subscription Owner is not enough. Synthetic Development
authentication is never enabled publicly. A missing/empty/invalid list fails
startup. Only Staging and explicitly configured real-Entra Development accept
this policy; Production and other environments reject it. Omission defaults to workforce,
which still requires its approved extension and rejects directory Guests.
The bootstrap remains the exact pair, only on first provisioning; there is no
first-user-wins or restoration of a removed administrator.

The owner-maintained list is the authoritative participant eligibility source
for **directory operations**, combined with Graph `accountEnabled=true` and
known `Member`/`Guest` type. It is copied from validated authentication settings,
not a separately bindable directory override. Graph is not queried for live
role assignments during each directory read; do not represent this as such.
The enterprise application must require assignment, with **initially only the
owner directly assigned** the dedicated role. The deployment owner must keep
the Entra assignment and application allowlist aligned. For removal, first
remove the allowlist entry/restart (or disable the app while changing the
initial-owner-only profile), persist local ineligibility for immediate
deprovisioning, then remove the role assignment. Never merely remove the role
and leave the directory allowlist admitting that person. Entra claim revocation
alone retains the existing up-to-one-hour fresh-sign-in boundary.

Adding a participant requires an explicit owner-approved assignment and
reviewed configuration/template change; the initial template cannot admit a
second account. No default group, blanket tenant admission, directory extension
claim or `Member == employee` inference is used.

## Entra, secrets and exact permissions

On 2026-09-17 the owner explicitly approved the two Graph application read grants,
the dedicated-resource ACS Read/Write role, and temporary operator access to store/read
the application credential in this staging vault for hosting and local user secrets.
Remove the temporary operator grant after credential setup. This approval does not
extend to other resources, directory writes by the app, or production deployment.

`identity.ps1` implements the owner-executed registration and credential steps.
It is not run by a deployment or test automatically. Source pinning and account/
subscription checks apply to every entry point.

1. Obtain explicit consent approval for the two **application** permissions on
   Microsoft Graph: `User.Read.All` (complete enabled user details) and
   `GroupMember.Read.All` (supported groups and complete transitive user expansion).
   No directory-write or Mail.Send permission is requested by the app.
   The script resolves the exact enabled application-role IDs from the tenant's
   Microsoft Graph service principal and explicitly grants those two roles.
   Operator directory-admin rights to create registrations/consent are separate
   from these runtime permissions.
2. Create one `AzureADMyOrg` registration, `Sidequest Hackathon`, with the
   dedicated User app role. Its enterprise application sets
   `appRoleAssignmentRequired=true` and assigns only the approved owner object.
   Register both `/signin-oidc` and `/signout-callback-oidc` HTTPS redirect URIs;
   front-channel logout is `/signout-oidc`. Existing same-name registrations
   cause a stop; independently verify any partial creation before continuing.
   The helper also reads the accepted `Properties\launchSettings.json` HTTPS
   profile and registers `https://localhost:7193/signin-oidc` and
   `https://localhost:7193/signout-callback-oidc`. No HTTP callback or arbitrary
   development hostname is added.
3. After infrastructure exists, an operator with explicitly approved **secret-set
   access on the dedicated staging vault** calls `Set-SidequestHackathonCredential`.
   It creates one 90-day credential, immediately sends it to the vault using
   in-memory HTTPS request bodies and returns only secret ID, credential key ID
   and expiry. Neither credentials nor tokens enter arguments, logs or files.
   Do not run these steps under a PowerShell transcript or HTTP-body tracing.
   Renew before expiry; remove the exact unused credential if vault writing
   fails. The helper does not grant itself vault access.
4. The two Key Vault references supply the same credential to `AzureAd` and
   `Directory:Credentials`. `Directory:Graph:TenantId` and
   `Directory:Credentials:TenantId` match authentication. No workforce extension
   or `WorkforcePolicyApproved` setting is needed or fabricated.

### Local development against the staging database

The same approved participant policy may be explicitly selected in the local
**Development** environment only with `Authentication:Mode=Entra`. It still
requires the dedicated role, real tenant/client credential, exact participant
allowlist and approved bootstrap pair. This does **not** select synthetic
authentication, weaken secure cookies, map synthetic endpoints or admit other
guests. Use the `https` launch profile at `https://localhost:7193`; populate the
ignored `appsettings.Development.json` only after the registration is known,
and keep the client credential in user secrets, never that file or tracked source.

The parent-owned tracked `appsettings.Development.example.json` remains the
synthetic **isolated LocalDB** example. Never combine synthetic Development
authentication with the shared Azure database. The ignored actual local file
may select that database with `Authentication=Active Directory Default`,
`Encrypt=True` and `TrustServerCertificate=False`, using the approved operator
credential. Refresh operator tokens after new Entra SQL group assignment.
No SQL password or automatic migration is introduced, and production still
uses the attached application MI client ID rather than the developer chain.

Leave `Hosting:Azure:Enabled` and `Hosting:Azure:AppServiceProxyEnabled` disabled
locally: Azure managed-identity hosting still rejects Development, and the
platform proxy opt-in remains Staging-only. Local HTTPS terminates directly at Kestrel and does not
need proxy-header trust. The local ignored configuration is excluded from publish;
it must never override the deployed Staging settings or provide cloud evidence.
Set `Delivery:Work:Enabled=false` locally so it does not claim shared queued work
without the deployed host's managed identities. The deployed worker remains enabled
and processes changes submitted through either web host. Local media uploads still
require a separately configured authorized storage credential.

ACS's documented managed-identity requirement is the custom role with
`Microsoft.Communication/CommunicationServices/Read` and
`Microsoft.Communication/CommunicationServices/Write`, assigned **only to the
dedicated ACS resource**. Its definition is assignable only within this RG.
These management actions are broader than sending one email; review that actual
scope before Apply. They do not allow key listing, deletion or RBAC administration.
An asserted built-in “Azure Communication Services Email Sender” role was not
present in the subscription's authoritative role definitions; do not invent its
ID or broaden to Contributor to make a failed send pass.
Verify real sending with the deployed system identity and stop for explicit
review if the documented custom role is not honored.

References:
- [Microsoft ACS managed-identity email sample and exact Read/Write permissions](https://github.com/Azure-Samples/communication-services-dotnet-quickstarts/tree/main/SendEmailAdvanced/SendEmailWithManagedIdentity)
- [Azure-managed email-domain ARM contract](https://learn.microsoft.com/azure/templates/microsoft.communication/2023-03-31/emailservices/domains)
- [Linked ACS domain ARM contract](https://learn.microsoft.com/azure/templates/microsoft.communication/2023-03-31/communicationservices)

## Accepted-source execution sequence

These commands mutate cloud/directory resources **only when the owner runs them**.
All repository changes must first pass a PR into `release-infrastructure`.
Use a clean worktree at its exact accepted head, GitHub account
`vaclav-pekarek-microsoft`, PowerShell 7.4+ and Bicep 0.47.16.
Retain nonsecret review/build evidence in the worktree's Git metadata directory.
Do not stage it, use a temporary directory, or store secrets there.

```powershell
. .\infra\budget-staging\identity.ps1
$acceptedSha = git rev-parse HEAD
$artifacts = git rev-parse --path-format=absolute --git-path sidequest-hackathon
New-Item -ItemType Directory -Force $artifacts | Out-Null
$app = 'sidequest-hackathon-b7ljjkoqcaedc'

# Only after the owner separately approves the two read-only Graph grants:
$identity = New-SidequestHackathonRegistration -SourceCommit $acceptedSha `
    -ApplicationUrl "https://$app.azurewebsites.net/" -ReadOnlyGraphConsentApproved

$review = Join-Path $artifacts 'application-review.json'
Invoke-SidequestApplicationDeployment -Mode WhatIf -SourceCommit $acceptedSha `
    -ClientId $identity.clientId -BicepPath $pinnedCompiler -ReviewPath $review
# Owner inspects every retained ARM change, including ACS role scope and costs.
Invoke-SidequestApplicationDeployment -Mode Apply -SourceCommit $acceptedSha `
    -ClientId $identity.clientId -BicepPath $pinnedCompiler -ReviewPath $review

# Operator vault secret-set grant must already be approved and assigned.
Set-SidequestHackathonCredential -SourceCommit $acceptedSha `
    -ApplicationObjectId $identity.applicationObjectId -ClientId $identity.clientId `
    -VaultName 'sqh-b7ljjkoqcaedc'

$firewallReview = Join-Path $artifacts 'firewall-review.json'
Set-SidequestApplicationFirewall -Mode WhatIf -SourceCommit $acceptedSha `
    -ApplicationName $app -ReviewPath $firewallReview
# Owner reviews the actual live App Service egress addresses.
Set-SidequestApplicationFirewall -Mode Apply -SourceCommit $acceptedSha `
    -ApplicationName $app -ReviewPath $firewallReview
```

Build the **accepted revision**, including the accepted UI assets, as Linux x64
self-contained .NET 10. No server runtime download/build or schema migration
runs during publish. The app startup command marks the bundled executable
executable before launching it; the .NET 10 App Service image supplies the
native OS dependencies. Verify availability of that Linux runtime image in the
subscription before Apply rather than silently substituting another runtime.

```powershell
$publish = Join-Path $artifacts 'publish'
dotnet publish .\src\Sidequest.Web\Sidequest.Web.csproj -c Release -r linux-x64 `
    --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$zip = Join-Path $artifacts 'application.zip'
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
$manifest = Join-Path $artifacts 'application-manifest.json'
@{
    sourceCommit = $acceptedSha
    sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash
} | ConvertTo-Json | Set-Content $manifest

# This is trusted operator acknowledgement of the gates below, not proof generated by the script.
Publish-SidequestApplication -SourceCommit $acceptedSha -ApplicationName $app `
    -ZipPath $zip -ManifestPath $manifest -IdentityAndProviderGatesVerified
```

Publish checks the source/artifact hash and resolved Key Vault references,
using the live GET `config/configreferences/appsettings` collection contract
(`2022-03-01`); missing, duplicate, unresolved or paginated required-reference
evidence blocks deployment before activation.
It uses Entra-authenticated deployment without enabling basic SCM authentication,
and activates the host explicitly. Failed SQL-backed readiness stops it.
The manifest proves only the owner's asserted source-to-artifact association;
use the clean accepted build command and retain its successful output.
Do not call the acknowledgement “CI attestation”.

## Gates and live verification

Before the final activation acknowledgement, independently verify:

- Accepted PR/source/hash; exact subscription, tenant, RG, Linux plan region and
  outbound rules; unchanged SQL administrator/group and MI runtime grants.
- Single-tenant registration, assignment-required enterprise app, exact role
  issuance, direct owner-only assignment and matching participant list.
  Graph consent and actual tenant read access are verified, not assumed from GA.
- Key Vault references resolve using the system identity; grants have propagated;
  ACS linked Azure-managed domain's `fromSenderDomain` and `DoNotReply` sender
  match the template; the intended test recipient is approved and has actual
  routable `mail`. Guest UPN is never substituted for a mailbox.
- Data residency/cost and ACS Read/Write scope are understood; no real production
  employee data or additional participant set is implicitly approved.

After activation, **record actual results**, not just resource provisioning:

1. HTTPS `/health/live` and `/health/ready` return 200; readiness checks SQL and
   the nonempty exact compiled migration history with the application SQL MI.
   This is the first real app-MI SQL check, not evidence from the operator's SQL login.
2. Owner completes real Entra sign-in, exact bootstrap/persisted eligibility is
   checked, and an unassigned account is denied. Confirm production-default
   workforce tests still reject guests.
3. Upload/read a cover through the app; verify no anonymous blob access. Verify
   encrypted key-ring creation and sign-in continuity after an App Service restart.
4. Exercise directory search/group expansion with the approved owner, and exclude
   an enabled but unlisted Member/Guest. Never grant broader Graph rights on failure.
5. Send an approved test notification/calendar request through ACS, verify delivery,
   update and cancellation in the recipient's Outlook, and record provider results.
   Azure-managed `DoNotReply` is the stable organizer but is not a reply mailbox.
6. Verify privacy-safe metrics actually ingest under managed identity and that no
   request traces, payloads, identities, client secrets or token bodies are emitted.

Live Entra, actual app-MI SQL, Blob/keyring, metrics and ACS delivery evidence
cannot be established by offline unit/ARM tests. Any failure is an explicit gate,
not permission to enable Development auth, widen firewall/RBAC, fabricate
workforce evidence, fake successful delivery or run automatic migrations.

## Offline checks

Compile with the reviewed Bicep 0.47.16 binary, then:

```powershell
& $pinnedCompiler build .\infra\budget-staging\application.bicep `
    --outfile (Join-Path $artifacts 'application.json')
$env:SIDEQUEST_APPLICATION_ARM_TEMPLATE = Join-Path $artifacts 'application.json'
node --test .\infra\budget-staging\application.test.mjs
dotnet test .\tests\Sidequest.UnitTests\Sidequest.UnitTests.csproj --no-restore `
    --filter "FullyQualifiedName~AuthenticationTests|FullyQualifiedName~GraphDirectoryGatewayTests|FullyQualifiedName~CoreWorkflowRegistrationTests|FullyQualifiedName~AzureHosting"
```

Tests inspect the **actual compiled ARM**, exercise bounded pure PowerShell
guards without cloud calls, and cover staging isolation, dedicated-role/list
intersection, disabled/unlisted guests, immutable lists, composition override
rejection, bootstrap constraints and unchanged workforce/hosting behavior.
