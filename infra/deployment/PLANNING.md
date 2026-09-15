# Read-only infrastructure planning

**Disabled by default; no live approvals or Azure validation exist.** This is a
planning workflow, not an apply/deployment workflow or deployment authorization.
Owner assignment, actual administrator-control verification, environment/identity
creation, role assignment, and enabling or dispatching this workflow remain
external actions. This implementation authorizes none of them.

## Accepted trust model

The user-selected model is **`github-environment-with-manual-verification`**:
native GitHub environment-required reviews enforce review, under the explicit
assumption that the deployment owner has manually verified administrator controls.
Two independent repository Actions variables must both be the exact string
`true`; absent, false, differently cased or other values fail the metadata job:

| Repository variable | Meaning |
| --- | --- |
| `SIDEQUEST_PLANNING_ENABLED` | Owner-controlled planning opt-in; leave absent or false until separately authorized |
| `SIDEQUEST_PLANNING_ADMIN_CONTROLS_VERIFIED` | Owner assertion that administrator-bypass controls were manually verified before enabling planning |

The second variable is a **trusted operator assertion, not API evidence**. The
GitHub environment REST/GraphQL schemas inspected do not expose a supported
administrator-bypass setting. Approval history also does not document a reliable
bypass discriminator. Neither is invented or inferred here; there is no
approval-history heuristic. Native reviews do not prove a human physically
clicked the UI.

This does **not** resist an administrator who can alter repository settings,
variables, environment controls, federation or the workflow. The owner must
disable planning, clear the acknowledgement, cancel queued/running planning runs,
and reverify before re-enabling after relevant protection, reviewer, identity,
permission or parameter-scope changes. Workflow contexts and API reads are
snapshots, not transactional locks or continuously monitored controls. A repeated
pre-OIDC metadata check detects source/environment-ID drift but cannot eliminate
administrator-controlled races.

Keep these two variables at repository scope only, without organization or
environment overrides. The five planning settings below must be environment
secrets only, with **no same-name organization/repository fallback values**.
GitHub's native `secrets` resolution is used for these non-credential settings so
the runner masks them before rendering step environment blocks. Proving their
configuration scope is part of the accepted owner's manual verification, not a
fabricated API attestation. No secrets are created by this implementation.

## Workflow and source gates

`.github\workflows\infrastructure-plan.yml` has only `workflow_dispatch`, with one
choice input: `staging` or `production`. It accepts no SHA, commands, template
paths, parameters, tenant, subscription or resource-group dispatch input.
The helper reads inputs from the bounded runner-owned `GITHUB_EVENT_PATH` file,
not a step environment variable that would echo arbitrary input before validation.

1. **Metadata job: no environment, no OIDC.** A fixed shell guard rejects any
   repository other than `vaclav-pekarek-microsoft/sidequest`, events other than
   manual dispatch, refs other than `refs/heads/main`, or `run_attempt != 1`
   before checkout. Both checkouts use the immutable `github.sha`, not a moving
   branch or PR ref. Workflow source SHA/ref must agree with the dispatch source.
2. The Node gate checks the checkout SHA, current main tip, repository identity,
   active planning workflow and this run's metadata. Both accepted CI workflows
   must have successful **latest runs for that exact main SHA**:
   - `.github\workflows\ci.yml`: `push`;
   - `.github\workflows\infrastructure.yml`: `push` or `workflow_dispatch`.
   Old-source success, PR runs, skipped/failed/pending latest runs and mismatched
   workflow identities do not pass. Infrastructure CI has path filters; if no
   same-source evidence exists, its existing **offline** workflow must be run
   separately through the approved process. This workflow does not dispatch it.
3. The selected environment must **already exist**, named exactly
   `sidequest-staging` or `sidequest-production`. It must have 1–6 explicit,
   distinct, non-bot `User` reviewers and `prevent_self_review=true`, plus custom
   deployment branch policies containing exactly one `branch` rule named `main`.
   Team reviewers, protected-all, unrestricted, tag, wildcard, duplicate or
   unknown rules fail. This deliberately bounded policy avoids guessing team
   membership or silently accepting unknown controls. A recognized optional wait
   timer is permitted.
4. **Protected planning job:** depends on metadata success and is bound to the
   validated environment. Only this job has `id-token: write`. Native GitHub
   environment review happens before its steps. It rechecks metadata, source and
   the original environment ID before requesting OIDC.

Only GitHub read permissions (`contents`, `actions`, `deployments`) are granted
to the metadata job. HTTP failures, inaccessible metadata, malformed/missing
fields and pagination uncertainty fail closed. List responses must have matching
counts and no `Link` header; CI lists at 100 or more results are rejected instead
of guessing completeness. A failed gate fails the workflow; it is not turned
into a successful-looking skipped run. Failure requires a **fresh manual
dispatch**, not a rerun reusing an earlier review.

## Environment-managed settings

The final job reads these native GitHub environment secrets as masked
**configuration values, not cloud credentials**:

| Environment secret | Required value |
| --- | --- |
| `AZURE_TENANT_ID` | Approved deployment tenant GUID |
| `AZURE_SUBSCRIPTION_ID` | Approved existing subscription GUID |
| `AZURE_CLIENT_ID` | Approved federated deployment service-principal **application/client** GUID |
| `AZURE_RESOURCE_GROUP` | Approved **existing** resource-group name |
| `SIDEQUEST_PLAN_PARAMETERS` | JSON object of explicit `main.bicep` parameters, without ARM value wrappers or account context |

No access credentials or client secret belong in these values. No sample organization
address space, approved owner, location, identity, role or group is supplied.
The existing [offline preflight](README.md) validates all template parameters,
GUIDs, known synthetic IDs, ranges, enums and retention. Its three template
defaults remain the only defaults. The selected `environmentName` must agree
with the dispatch choice. `enableApplication=true` is always rejected.

Resource-group arguments use a conservative local ASCII policy: 1–90 characters,
letters/digits/underscore/parentheses initially, then those characters plus dot
and hyphen, and no trailing dot. This is a command-input policy, not a claim to
accept every Azure-supported Unicode name. All commands use argument arrays,
never shell evaluation, and have no caller-controlled option lists.

## Compiler, OIDC and Azure command boundary

The final job downloads the same official **Bicep 0.47.16 Linux x64** compiler
used by the offline infrastructure workflow, verifies SHA-256 before writing or
executing it, compiles the exact checked-out `infra\main.bicep`, and runs the
existing compiled-template contract tests. It requires an installed Azure CLI
**2.x, at least 2.76.0** for `ProviderNoRbac`; unsupported versions fail rather
than installing extensions or falling back to weaker validation.

| Immutable dependency | Pin |
| --- | --- |
| `actions/checkout` v7.0.1 | `3d3c42e5aac5ba805825da76410c181273ba90b1` |
| Bicep Linux x64 0.47.16 SHA-256 | `64c345a58e0c3e48b1bc98a4e62d6b3adb1d238281297de3400aeafb2697aa5a` |

No Azure login action is used. The Node wrapper requests the standard GitHub OIDC
token with the runner's `ACTIONS_ID_TOKEN_REQUEST_URL` and request token, using
audience **`api://AzureADTokenExchange`**. It accepts only HTTPS GitHub Actions
subdomains ending in `.actions.githubusercontent.com`, with no credentials,
custom port, fragment, duplicate query keys or preexisting audience. It follows
no OIDC redirects. The runtime endpoint and request token must come from the
trusted GitHub-hosted job, never a PR, variable override or dispatch input.

The returned compact JWT is bounded; issuer, audience, repository/ID, branch,
source, workflow, run/attempt, environment and validity times are checked before
login. Local decoding is **not signature verification**: Entra must validate
the token and externally configured federation. The owner must bind federation
to the correct issuer, audience and repository/environment subject, accounting
for GitHub's current immutable-subject format where applicable. Broad repository
or branch federation is not an acceptable substitute for reviewed environment
binding. None of that federation is created here.

The JWT is registered with GitHub's masking protocol before invocation, never
included in summaries or artifacts. It is passed in a child argument array, so
the trusted isolated runner/process boundary remains important. All child stdout
and stderr are captured; stderr and errors are not forwarded. Only these Azure
CLI command families are executed:

- `az version` — local tool check;
- `az login --service-principal --username ... --tenant ... --federated-token ...`
  — no interactive/user/device-code/client-secret fallback;
- `az account set`, `az account show`, `az account clear` — isolated local
  profile selection, authenticated context and cleanup;
- `az group show` — require the approved group to exist in the selected
  subscription;
- `az deployment group validate` and `az deployment group what-if` — explicit
  subscription/group, local compiled ARM template and preflight-generated
  parameters, `Incremental`, `ProviderNoRbac`, and no prompt.

Account tenant, subscription, enabled public-Azure state and service-principal
client ID must agree with approved settings. Only that authenticated account
context is supplied to final ARM-parameter generation. Workforce and deployment
tenants must agree; syntax/equality do not establish workforce status, real group
existence or administrator authority.

What-if uses `--result-format ResourceIdOnly --no-pretty-print`. It never uses
deployment `create`, `--confirm-with-what-if`, resource-group creation, grants,
migrations, application activation, package publishing or another resource-changing
command. `ProviderNoRbac` checks resource read permissions rather than deployment
write permissions; it does **not** itself grant the ARM validate/what-if actions
or prove least privilege. The deployment owner must separately approve and
verify the narrowly scoped planning identity's effective permissions.

## Output, bounds and cleanup

Success produces only a small JSON summary in the job log and GitHub step summary:
source SHA, fixed environment name, `enableApplication=false`, counts by
documented change type and fixed non-approval limitations. No resource IDs,
properties, provider diagnostics, parameter values or credentials are included.
There is no artifact upload and no public full-plan artifact.

ARM failures, non-success states, missing expected response shapes, validation
diagnostics, unknown what-if change types, `Ignore` or `Unsupported` changes
fail instead of producing a success summary. Count output is **not a full change
review**; a future apply workflow needs separately approved full change review.
What-if predictions, including `Deploy`/`Modify`, do not establish actual effects.

Local parser/transport safety limits (not Azure/GitHub service limits): 1 KiB
dispatch JSON, existing 16 KiB preflight envelope, 2 MiB metadata, 64 KiB OIDC
response, 32 KiB JWT, 128 MiB compiler download, 8 MiB combined child output and
at most 10,000 what-if changes. HTTP reads and commands are timed; stage failures
are fixed actionable messages with nonzero exit, never raw exceptions.

Runtime files are confined to a newly owned `.sidequest-plan-*` directory inside
the fresh runner workspace: private compiler/template/parameter files and a
separate `AZURE_CONFIG_DIR`. GitHub/OIDC/parameter environment values are removed
from child environments; CLI telemetry and dynamic extension installation are
disabled. CLI profile cleanup and recursive removal of **only that owned
directory** run in `finally`, including failures and partial login. Cleanup
failure prevents success. Forced job termination still depends on destruction
of the ephemeral GitHub-hosted runner; there is no shared/self-hosted runner
claim and no post-cancellation success artifact.

## Offline use and verification

These are workflow-internal commands, not a local login convenience:

```text
node infra\deployment\plan-workflow.mjs --help
node infra\deployment\plan-workflow.mjs gate
node infra\deployment\plan-workflow.mjs plan
```

`gate` and `plan` require the trusted GitHub runtime context and fail outside it.
Do not fabricate that context to run cloud operations locally.

From the repository root:

```powershell
node --test (Get-ChildItem infra\deployment\tests\*.test.mjs | ForEach-Object FullName)
```

Tests use fake metadata, OIDC and Azure/compiler boundaries, plus actual harmless
Node subprocesses to verify capture, limits, redaction and exit codes. They create
no temporary files, request no tokens, run no Azure commands, and dispatch no
workflow. Structural tests inspect the actual workflow and pinned compiler
relationship. Passing tests do not prove live GitHub permissions, native review,
federation or Azure availability.

Private-runner SQL identity/bootstrap, migrations, provider configuration,
private DNS/routing/capacity/SKU/runtime acceptance, live recovery and load tests
remain unfinished. This package completes none of those release gates.

## Authoritative sources checked

- [GitHub environment protection semantics](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments)
  and [review/bypass semantics](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/review-deployments).
- [Environment REST API](https://docs.github.com/en/rest/deployments/environments),
  [branch-policy REST API](https://docs.github.com/en/rest/deployments/branch-policies),
  [workflow-run REST API](https://docs.github.com/en/rest/actions/workflow-runs) and
  [official response schemas](https://github.com/github/rest-api-description/blob/main/descriptions/api.github.com/api.github.com.json).
  Runtime GET requests use API version `2026-03-10`.
- [Supported OIDC request variables, claims and audience](https://docs.github.com/en/actions/reference/security/oidc#methods-for-requesting-the-oidc-token).
- [Azure CLI federated login](https://learn.microsoft.com/en-us/cli/azure/reference-index#az-login),
  [account commands](https://learn.microsoft.com/en-us/cli/azure/account),
  [resource-group commands](https://learn.microsoft.com/en-us/cli/azure/group),
  [validate/what-if flags](https://learn.microsoft.com/en-us/cli/azure/deployment/group),
  [ARM validation response](https://learn.microsoft.com/en-us/rest/api/resources/deployments/validate)
  and [what-if limitations](https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/deploy-what-if).
- [Checkout release pin](https://api.github.com/repos/actions/checkout/git/ref/tags/v7.0.1)
  and [official Bicep release](https://github.com/Azure/bicep/releases/tag/v0.47.16).

The trust policy, explicit owner acknowledgement, direct-User-reviewer restriction,
complete-single-page policy, conservative argument syntax and fail-on-incomplete
planning policy are local decisions—not inferred provider guarantees.
