# Disabled-application infrastructure apply preparation

**Disabled by default, offline-tested only. No deployment or external approval
exists.** This package prepares a separately reviewed infrastructure apply path;
it does not authorize enabling it, creating identities/environments, configuring
permissions, dispatching a workflow or activating the application.

The [planning workflow](PLANNING.md) remains read-only. Its review, identity and
count-only summary are **not deployment approval**. The apply workflow uses a
distinct deployment client, distinct protected environments and distinct opt-ins.
Only infrastructure from the exact reviewed source can be applied. No application
package is accepted; `applicationArtifact` must be `null`, and the existing
preflight always generates `enableApplication=false`.

## Trust and external setup

The D39 model uses native GitHub required environment reviews plus the deployment
owner's **manual** verification of administrator controls. The acknowledgement is
an operator assertion, not API evidence. This is not bypass-proof enforcement
against an administrator who can change settings, identities, secrets or code.
There is no approval-history heuristic or invented administrator-bypass property.
Reviews do not prove that a human physically clicked the UI.

The owner must independently verify and approve all of the following before any
enablement; this repository creates none of them:

- Existing `sidequest-apply-staging` and/or `sidequest-apply-production`
  environments, separate from the corresponding planning environments.
- Required reviews by 1–6 explicit distinct non-bot GitHub `User` accounts;
  self-review prevention; administrator-bypass restrictions manually verified;
  custom deployment policies containing exactly one **branch** rule, `main`.
  Team reviewers, tags, wildcards, unrestricted/protected-all policies, absent
  environments and unknown controls fail the supported metadata checks.
- An existing approved resource group and approved real workforce/administrator
  identities, region, network ranges and recovery settings. Syntax is not proof
  of identity, workforce eligibility, group existence or network suitability.
- A **separate** Entra deployment service-principal client, neither the planning
  client nor the workforce application client. Its federation must bind the
  exact repository/apply environment and approved issuer/audience, accounting
  for GitHub's supported subject format. Planning federation must not grant
  access to the deployment identity.
- Explicit least-privilege review of that client's effective permissions at the
  selected resource-group scope, including required ARM deployment/validation/
  what-if operations, the resource writes in this template, and its role
  assignments. `Provider` validation checks sufficient permissions; it does not
  prove the absence of excess privileges. Client-ID inequality also does not
  prove permission separation.
- Separate review of **constrained role-assignment authority** for exactly the
  template's roles, principals and target resources. Resource creation permission
  alone is insufficient because this template provisions scoped role assignments.
  No blanket Owner grant, new permission, identity, administrator or credential
  is approved or prescribed here. The owner must choose and verify the actual
  least-privilege mechanism; absent that review, apply stays disabled.

These repository Actions variables must both be the literal string `true`:

| Repository variable | Meaning |
| --- | --- |
| `SIDEQUEST_INFRASTRUCTURE_APPLY_ENABLED` | Separate explicit infrastructure deployment opt-in |
| `SIDEQUEST_APPLY_ADMIN_CONTROLS_VERIFIED` | Owner acknowledgement of manually verified **apply** administrator controls |

Keep both absent or false now. Planning variables cannot substitute. Keep these
variables repository-scoped, without organization/environment overrides.

Use the following **apply-environment secrets** for masked configuration, not
cloud credentials. Do not define same-name repository/organization fallbacks.
Their scope is another owner-verified assertion, not API attestation. GitHub
reads environment secrets when the referencing job starts.

| Environment secret | Format |
| --- | --- |
| `AZURE_TENANT_ID` | Approved deployment/workforce tenant GUID |
| `AZURE_SUBSCRIPTION_ID` | Approved existing subscription GUID |
| `AZURE_APPLY_CLIENT_ID` | Approved separate deployment application/client GUID |
| `AZURE_PLANNING_CLIENT_ID` | Corresponding planning identity's application/client GUID, used to reject identity reuse |
| `AZURE_RESOURCE_GROUP` | Approved existing group name, using the planning helper's conservative ASCII input policy |
| `SIDEQUEST_APPLY_PARAMETERS` | Plain JSON object of `main.bicep` parameters, no ARM wrappers/account context/credentials |
| `SIDEQUEST_APPLY_APPROVAL` | Strict flat JSON owner review record described below |

There is no client secret, certificate, SQL password or stored federated token.
Keep application/provider secrets out of these values.

After relevant protection, identity, federation, permission, parameter-scope or
workflow changes, **disable apply, clear its acknowledgement, stop queued work,
privately inspect any in-flight deployment, and reverify before re-enabling**.
Cancelling a workflow cannot be assumed to cancel a submitted ARM deployment.

## Exact source, private change review and approval record

The workflow is manual-only (`workflow_dispatch`), with only a `staging` or
`production` choice. No source SHA, commands, identities, parameter values or
artifact paths can be supplied as dispatch inputs.

1. Obtain successful latest runs of **both** `ci.yml` and `infrastructure.yml`
   for the exact current `main` SHA. CI must be from an accepted main push
   (`infrastructure.yml` also accepts its existing manual offline validation).
   Infrastructure CI runs on every main push. Neither workflow is dispatched by
   this utility.
2. Through a separately authorized **private** process, review the complete
   `FullResourcePayloads` ARM what-if for that exact compiled template, disabled
   parameters, deployment scope and identity. Use the pinned Linux compiler and
   the same `Incremental` / `Provider` operation settings as apply. Record all
   changes, identity/role-assignment effects, costs and external prerequisites.
   Planning counts or a `ResourceIdOnly` response are not sufficient. This
   package does not obtain or publish this owner's full-review material.
3. Calculate the three fingerprints locally from those private reviewed files:

   ```powershell
   node infra\deployment\apply-workflow.mjs fingerprints reviewed-template.json reviewed-parameters.json reviewed-full-whatif.json
   ```

   This command is offline and only prints three SHA-256 values. It neither
   creates an approval record nor supplies approval assertions. Input files are
   operator-owned; the helper does not delete them. Protect and retain/delete
   them according to the approved private-review policy, never in this public
   repository, workflow artifacts or logs.
4. Only after separate authorization, dispatch a **fresh** apply run at that
   exact main source. Its metadata-only job checks the fixed repository, first
   attempt, exact workflow/ref/source, current main tip, both CI results and the
   existing apply environment. It has **no environment and no OIDC**.
5. While the native apply-environment job awaits required review, an authorized
   owner configures the protected approval record for **this run ID** and checks
   that the other environment settings still match the private review. Reviewers
   approve this infrastructure deployment only after checking the full material
   and record. Do not approve first and attempt to race a secret update. A
   missing, stale or mismatched record fails without requesting a cloud token.
   Workflow/job approval and secret maintenance remain external actions.
6. If main advanced, review expired, metadata changed or a gate failed, use fresh
   private review and a fresh manual dispatch. `run_attempt != 1` is denied.
   An old run's record cannot authorize another run. No automatic rerun or
   rollback is implemented.

`SIDEQUEST_APPLY_APPROVAL` requires **exactly** these keys (no extras or duplicate
keys, even escaped duplicates; no nested objects or arrays; at most 16 KiB):

| Key | Required value |
| --- | --- |
| `version` | Number `1` |
| `purpose` | `"disabled-infrastructure-apply"` |
| `applyRunId` | Decimal **string** of this fresh GitHub run ID |
| `sourceSha` | Exact lowercase 40-character checked-out/current-main SHA |
| `templateSha256` | Lowercase 64-character SHA-256 of exact compiled template bytes |
| `parametersSha256` | Lowercase SHA-256 of canonical generated ARM parameters document |
| `whatIfSha256` | Lowercase SHA-256 of the canonical **entire** reviewed full-payload what-if JSON |
| `applicationArtifact` | JSON `null`, always |
| `tenantId`, `subscriptionId` | Approved GUIDs matching authenticated settings |
| `resourceGroup` | Exact approved group name |
| `deploymentClientId`, `planningClientId` | Approved distinct client GUIDs matching settings |
| `permissionScope` | `/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}` |
| `leastPrivilegeReviewed` | Boolean `true`, explicit owner assertion |
| `roleAssignmentsReviewed` | Boolean `true`, explicit owner assertion |
| `reviewReference` | Nonblank private review reference, <=256 characters, no controls; never printed |
| `reviewedAt`, `expiresAt` | Canonical UTC `Date.toISOString()` strings, including milliseconds |

The interval must be positive and at most **24 hours**; review time cannot be
future, and expiry must be strictly later than the check. This is a bounded local
review-freshness policy, not an Azure/GitHub platform limit. It is rechecked just
after the final asynchronous file verification, immediately before create. The
record is an **owner assertion protected by native environment review**, not a
signature, verified approval-history event or independent RBAC
attestation. No approval record or actual identities are provided as samples.

Template hashing preserves exact bytes, including compiler metadata/newlines.
Use the exact pinned Linux compilation output, not a differently formatted
Windows build. JSON hashing sorts object keys, preserves array order and every
field, and rejects unsupported/deep values. No provider fields are discarded to
force a match. Volatile fields, ordering differences or environmental drift can
therefore cause a conservative rejection requiring another review.

## Execution, redaction and partial failures

After native environment review, only the protected apply job has
`id-token: write`. It rechecks source/CI/protection metadata and the original
environment ID. It then validates settings and the owner record, compiles the
exact source with the hash-verified Bicep compiler, runs compiled-template
contracts and compares template/parameter hashes **before requesting OIDC**.

The captured Node OIDC/federated-login boundary is shared with planning:
validated HTTPS GitHub runtime endpoint, `api://AzureADTokenExchange` audience,
exact workflow/source/run/attempt/environment claims, token masking and argument
arrays without shell interpolation. Entra validates signature and federation;
local token parsing is not authentication evidence. No raw Azure login action
or credential fallback is used. Actual `az account show` must match the tenant,
subscription and deployment service-principal client; only that account context
is used to regenerate disabled ARM parameters.

Allowed Azure command sequence:

- Local `version`, federated `login`, local `account set/show/clear`.
- `group show` for the existing bound scope.
- `deployment group validate` with `--validation-level Provider`.
- `deployment group what-if --result-format FullResourcePayloads
  --no-pretty-print`, with full approved fingerprint equality and scope checks.
- Exactly one `deployment group create`, with the same private local template
  and parameters, `--mode Incremental --validation-level Provider --no-prompt true`,
  explicit subscription/group, and name `sidequest-apply-{runId}`.

Create uses no `--no-wait`, automatic retry, rollback or remote template.
There are no group-creation, package-publishing, SQL-grant/migration, application
deployment/activation or provider-secret commands. **Create is resource-changing**:
it provisions/updates infrastructure and the template's reviewed role assignments.
Incremental mode is not a guarantee of nondestructive property/child-resource
updates.

Full what-if must explicitly succeed, with no errors/diagnostics, unknown,
`Ignore`, `Unsupported` or `Deploy` changes. Create/Modify require after payloads;
Delete/Modify require before payloads. Resource IDs must be unique and within
the approved resource group. An explicit zero-change result is valid; missing
results are not. Template and parameter files are reread immediately before
create to reject changed local bytes.

ARM what-if is **not an atomic saved plan, reservation or state lock**. This
comparison cannot prevent external changes between what-if and create, prove
provider predictions complete, or guarantee actual effects. GitHub concurrency
only serializes this workflow's environment; it does not lock Azure or external
operators. The owner must control concurrent changes and approve that limitation.

Completion requires the exact deployment ID, `Succeeded` provisioning and a
boolean `applicationIsEnabled=false` output. Only fixed status, source/environment,
deployment name and the three hashes are summarized. No raw parameters, private
review reference, resource properties/IDs, credentials or provider diagnostics
are printed or uploaded. Azure itself stores deployment history: its access and
retention belong to the owner's review.

All external stdout/stderr is captured and bounded to 8 MiB; raw stderr is never
forwarded. Files have private modes in an owned runner-workspace directory with
an isolated Azure CLI profile. GitHub/OIDC/PLAN/APPLY/AZURE configuration variables
are stripped from Azure/compiler/test child environments except explicit
private-profile controls.
Automatic extensions and CLI telemetry are disabled. Normal failures clear the
profile and remove owned files; forced runner termination relies on destruction
of the isolated GitHub-hosted runner. Do not reuse a shared persistent runner
without separately reviewed cleanup/process isolation.

Commands default to five-minute timeouts; create has a **30-minute** captured
timeout inside a **45-minute** job. If create starts and then fails, times out,
returns incomplete results, or cleanup/reporting fails, the nonzero error states
that **resources may have changed**. A stopped CLI is not proof that ARM stopped.
Do not retry or roll back automatically. Privately inspect Azure deployment
state/operations, reconcile partial effects, and obtain a new exact review before
another run. This workflow does not implement that live recovery procedure.

## SQL and application activation remain blocked

No executable SQL bootstrap/migration placeholder is supplied. Running invented
grants against an unapproved principal or declaring migrations complete without
private connectivity would be unsafe. The remaining bounded prerequisite is an
integration-owner-approved private execution package with:

1. An approved private-network runner, verified SQL/private DNS routing and
   appropriate outbound identity-token access. The public-hosted apply job does
   not connect to the private SQL data plane.
2. An authorized Entra SQL administrator/bootstrap identity and a separate
   migration identity, with reviewed identity-resolution permissions. Do not
   invent SQL administrator IDs/passwords or widen public firewall access.
3. The **actual** system-assigned web principal resolved and verified after
   infrastructure provisioning, plus approved contained-database-user creation
   and the application's explicitly reviewed least-privilege runtime table/
   procedure access. The running application must not receive schema-change
   authority or blanket administrative membership.
4. A source-bound, hashed migration artifact produced and reviewed by the
   integration owner, expected starting/ending migration history, backup and
   partial-failure/recovery procedure, and a private execution/verification path.
   This utility neither generates migrations nor treats readiness as migration
   execution. Never migrate on application startup.
5. Actual private SQL access and exact migration-history readiness, then verified
   Blob/Key Vault access, wrapping/recovery, secret references, Graph workforce
   policy/credentials, ACS sender/identity permissions and provider approvals.

A future application release flow must separately bind the immutable application
artifact digest to its exact accepted source/build/test provenance and approved
configuration, review it, and enforce SQL/bootstrap/migration, provider and
device/data approvals **before activation**. Neither infrastructure approval nor
`applicationArtifact:null` proves that application release is ready. Private-runner
bootstrap, live federation/provider validation, real recovery/load acceptance,
and application activation remain unfinished.

## Offline validation and authoritative references

```powershell
node --test (Get-ChildItem infra\deployment\tests\*.test.mjs | ForEach-Object FullName)
```

Tests use synthetic metadata, clock, filesystem and process boundaries; harmless
Node CLI subprocesses verify usage/denial exits. They perform no Azure operation.
Infrastructure CI also compiles `main.bicep` and runs its existing ARM contracts.

Pins remain shared with planning:

- `actions/checkout` v7.0.1:
  `3d3c42e5aac5ba805825da76410c181273ba90b1`.
- Bicep Linux x64 v0.47.16 SHA-256:
  `64c345a58e0c3e48b1bc98a4e62d6b3adb1d238281297de3400aeafb2697aa5a`.
- Installed Azure CLI must be 2.x, at least 2.76.0; no installation or extension
  fallback is attempted.

Authoritative API/OIDC sources are listed in [PLANNING.md](PLANNING.md).
Additional supported apply semantics:

- [Azure deployment group CLI](https://learn.microsoft.com/en-us/cli/azure/deployment/group):
  `create`, `validate`, `what-if`, validation levels and operation flags.
- [ARM what-if](https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/deploy-what-if):
  prediction limits and full resource payloads, not a saved-plan guarantee.
- [GitHub secret availability](https://docs.github.com/en/actions/reference/security/secrets):
  environment secrets are read when the referencing job starts.

Documentation describes a future owner-operated procedure, not authorization to
perform its external actions now.
