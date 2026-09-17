# Low-cost hackathon SQL staging

This is the separate **D40 dev/test profile**, not the production deployment in
`infra/main.bicep`. The owner authorized only subscription
`b75472bd-4174-4f66-b159-bae420212abc`, tenant
`99e674a6-6773-4f53-90a3-e3ab8c37c856`, resource group `sidequest-rg`.
Never use the CLI's default subscription as a fallback.

The earlier USD 30/month figure is guidance, not a hard ceiling. Favor a simple
inexpensive hackathon environment, not complex budget-enforcement infrastructure.
Keep the subscription's existing Visual Studio spending limit unchanged. This
offer is for dev/test, not production; credit availability is not release approval.

## Scope and cost

The SQL slice creates only a logical server, **Basic 5 DTU / 2 GB** database
`sidequest`, seven-day local-backup retention and two user-assigned identities:
`sidequest-app` and `sidequest-migration`. It creates no App Service plan, running
application, NAT Gateway, private endpoint, VM, storage account, paid Defender
plan or monitoring exporter. Managed identities do not themselves grant Azure
or database permissions.

West US 3 is available for Basic SQL in the approved subscription. Checked
US East/US East 2/US West 2 and several European alternatives were restricted.
On 2026-09-17 the Microsoft retail API returned:

| Future complete-app baseline | Rate | 31-day estimate |
| --- | --- | --- |
| Linux App Service B1, West US 3 | USD 0.017/hour | USD 12.648 |
| SQL Database Single Basic | USD 0.161/day | USD 4.991 |
| Combined hosting + SQL | | USD 17.639 |

Only the SQL portion is provisioned by this slice. Storage, Key Vault, email,
telemetry, transfers and taxes are additional usage-dependent costs. These are
retail estimates, not billing guarantees, reserved pricing, or proof that Basic
meets the production 300-user and recovery targets.

## Identity and network boundary

- Use an actual Entra security group named `Sidequest SQL Administrators`.
  Before provisioning, verify its object ID and the owner's direct membership.
  For a new group, the owner-approved directory administrator creates that group
  with the owner as a member/owner; never adopt a same-name group without checking
  its identity and members. Entra groups are directory objects, not Azure
  resource-group resources.
- SQL permits Entra authentication only and TLS 1.2 or later. There is no SQL
  password administrator, "Allow Azure services" rule or template firewall entry.
  Public endpoint availability does not mean anonymous or unrestricted SQL access.
- Bootstrap/SSMS requires a narrow rule for the owner's current public IPv4
  address. Use a unique temporary rule name and identical start/end addresses;
  remove that exact rule in `finally`, including on migration failure. Do not
  delete existing rules or widen ranges to make a connection succeed.
- When the app is deployed later, allow only its verified outbound addresses.
  App Service egress addresses are not an identity boundary: managed identity
  and SQL permissions remain mandatory. Reverify firewall rules after hosting
  changes. No generic Azure-wide firewall exception is allowed.
- Workforce application admission is separate from deployment ownership. A
  subscription Owner or tenant administrator is not automatically an eligible
  Sidequest user; guest rejection remains unchanged.

## Reviewed local deployment

This owner-supervised local path is limited to D40. It does not enable or bypass
the existing production planning/apply workflows. Merge the task PR into
`release-infrastructure` after its checks pass. Use a clean worktree at that exact
accepted head, the approved GitHub account and Bicep **0.47.16**.

The entry point is dot-sourced PowerShell 7.4+:

```powershell
. .\infra\budget-staging\deploy.ps1
Invoke-SidequestSqlDeployment -Mode WhatIf -SourceCommit $acceptedSha `
    -AdministratorGroupObjectId $verifiedGroupId -BicepPath $pinnedCompiler `
    -ReviewPath $outsideRepositoryReviewPath
```

`WhatIf` may create the approved empty `sidequest-rg`; it does not create SQL.
Read the retained full ARM change review before the separate apply. The review
file contains noncredential configuration and must remain outside the repository.
It binds source, compiled-template SHA-256, subscription, tenant, region, resource
group and administrator group. It is an operator review record, not an
independent approval attestation.

```powershell
Invoke-SidequestSqlDeployment -Mode Apply -SourceCommit $acceptedSha `
    -AdministratorGroupObjectId $verifiedGroupId -BicepPath $pinnedCompiler `
    -ReviewPath $outsideRepositoryReviewPath
```

Apply rechecks the accepted source, clean worktree, GitHub identity, live Azure
subscription/tenant/offer/spending limit, actual group membership and retained
review bindings. It does not change the CLI default account, retry resource
writes or enable the application. A failed ARM deployment may have created some
resources: inspect the exact deployment and costs rather than assuming rollback
or rerunning blindly.

## Schema and permissions

Generate an idempotent SQL script using the repository-pinned `dotnet-ef`,
without starting the web host. Explicitly set a nonproduction tooling connection
even though script generation does not open it. Do not accidentally use the
design-time factory's `SidequestDevelopment` fallback.

```powershell
dotnet ef migrations script --idempotent `
    --project src\Sidequest.Infrastructure --startup-project src\Sidequest.Web `
    --output $outsideRepositoryMigrationSql
Get-FileHash $outsideRepositoryMigrationSql -Algorithm SHA256
```

Retain the SQL hash, source SHA, expected migration IDs, ARM outputs and execution
result together. Bootstrap must verify the exact database and identities, execute
the reviewed schema separately from app startup and grant the application only
the explicit runtime table permissions. Existing identities with mismatched
object IDs must cause failure, not silent reassignment. Do not interpret reserved
managed identities or SQL impersonation checks as proof that an application
host can obtain a token or connect. The application and its activation remain
the next separate deployment slice.

## Offline checks

Compile `sql.bicep` with the pinned compiler, set
`SIDEQUEST_BUDGET_ARM_TEMPLATE` to that JSON, and run:

```powershell
node --test infra\budget-staging\sql.test.mjs
```

These checks use compiled ARM plus an isolated PowerShell command stub; they do
not log in, create resources, resolve real groups or execute live SQL. Hosted
infrastructure CI runs them without Azure credentials alongside the unchanged
production template contracts.
