# SQL and Blob recovery acceptance — NOT EXECUTED

Target: **RPO <= 1 hour; RTO <= 4 hours** (handoff §57 / A24).
Configured SQL PITR, Blob versioning/soft-delete/PITR, a healthy endpoint,
ownership recovery and a synthetic key-ring test are **not recovery evidence**.
This runbook requires an actual approved recovery operation and restored
application verification. It does not grant permission to create/delete/restore
resources or change production.

## Authorization and preflight

The deployment/DB/storage owners must approve and record the failure model,
scope, test-data policy, recovery point, rollback plan, costs, target resources,
private-network runner, operator roles and notification containment. Start with
an approved nonproduction clone and synthetic content. **Never induce corruption
or invoke in-place Blob restore on shared/production storage for this exercise.**
If no safe destination exists, stop; obtaining one is an external approval gate.

Pin application artifact/source SHA, schema migration list, configuration
fingerprint, deployment inventory and actual SQL/Blob used sizes/version counts.
Record backup creation/earliest/latest restorable times and provider operation
limits for the deployed region/SKU. Inspect real backup/restoration availability,
not just Bicep declarations. Have owners verify currently supported Azure SQL
PITR and Blob restore semantics immediately before the approved run.

Before the incident:

1. Verify least-privilege restore/operator access, private DNS/connectivity and
   access to audit/backup records. Running app identity must not gain schema-
   change permission; migration/bootstrap remains a separate approved action.
2. Verify separate private cover and Data Protection containers, Blob versioning,
   change feed/retention/restore window, container deletion protection and SQL
   PITR/log retention. Blob PITR is not a universal account/container/key backup:
   document supported object types and excluded operations, container restore
   needs, metadata/version behavior and ordering. Do not assume SQL and Blob
   automatically restore to a transactionally consistent point.
3. Verify Key Vault soft-delete/purge protection and **all wrapping-key versions
   referenced by retained encrypted Data Protection keys**. Confirm roles,
   stable application discriminator and versionless configured key URI.
   Restoring a Blob key ring without required wrapping-key versions is not usable
   recovery. Never solve decryption failure with plaintext/local fallback.
4. Set up approved outbound containment **before starting a recovered host**:
   production recipients unreachable, only approved test recipients permitted,
   controlled worker startup and no parallel original/recovered email senders.
   The application has no assumed magic “recovery safe mode”; owners must design
   and verify actual containment using approved deployment/network/provider
   controls. Do not invent a configuration switch.
5. Create a restricted baseline ledger of rows/relations, immutable audit/outbox/
   delivery IDs, calendar UID/sequences, queue leases, Blob content hashes/
   versions/lengths and media pointer/status. Validate the source application.

## Recovery markers and clock origin

Use an approved driver to record a marker sequence at least every five minutes
for more than one hour before the incident: durable SQL domain changes with
audit/outbox, valid uploaded cover versions with DB references, membership/
private invitation changes, participation and delivery state. Keep external
manifest timestamps/content hashes in restricted independent storage. Record
provider receipts for mail already submitted.

Markers must permit proving a **contiguous recoverable prefix**, not merely
finding one recent timestamp. Include DB references to older Blob versions,
replacement/cleanup, and private access revocation near the selected point.
Use real domain operations/approved seed mechanisms; no invented ledger rows.

Record UTC and synchronization uncertainty for:

* `incidentUtc`: earliest actual loss of required service/data in the approved
  incident, **not** when someone eventually clicks Restore.
* Detection, escalation, authorization, each restore request/completion, DNS/
  identity/key resolution, validation start/end and traffic re-enable.
* `sqlRecoveredThroughUtc` / `blobRecoveredThroughUtc`: latest verified
  **contiguous** source ledger prefix usable in each store after reconciliation,
  not a requested backup time or cloud status timestamp.
* `usableUtc`: restored service actually passes the complete checks below and is
  approved for use. Do not stop the clock at SQL provisioning success.

RPO = incident time minus the **older** verified usable SQL/Blob prefix.
RTO = usable time minus incident time, including detection/approval/queue/key/
DNS/cross-store reconciliation. Include timing uncertainty conservatively when
near a target. Restore rollback/discard decisions can make the effective
recoverable point older than the requested PITR point; use that older point.

## Execution checklist (approved operator, actual Azure operations)

1. Record the actual incident and immediately fence writes/workers/senders
   according to the approved plan. Preserve original logs and source resources
   for analysis. Avoid multiple active writers during a restore.
2. Confirm available SQL recovery points and choose one consistent with the
   approved failure scenario. Invoke Azure SQL PITR to the **approved distinct
   destination database** through the organization's reviewed procedure.
   Capture provider operation ID, requested time, source/destination identifiers
   (restricted), start/end UTC, status and errors. Wait for actual completion.
3. Restore cover content using the approved account/container-specific procedure.
   Blob PITR can be in-place and can affect a range of names: verify ownership
   and exact scope first. Where restoring to another approved destination is
   required, use the separately reviewed version-copy/recovery procedure.
   Capture the actual restore/copy operation IDs, versions/ranges, times and
   status; do not assume SQL PITR also restores Blob.
4. Restore/verify the private Data Protection key ring and required wrapping-key
   versions without exposing encrypted XML or keys in public artifacts. Retain
   old key versions required by preserved cookies/protected payloads. Confirm
   the same application discriminator and identity/permissions as intended.
5. Reconcile the two stores to a verified usable ledger prefix. Resolve missing
   or wrong-version covers, orphaned blobs and cleanup intents with a reviewed
   procedure. **Do not run ordinary cleanup blindly before reconciliation**.
   Preserve evidence of losses; do not make referential errors disappear by
   silently dropping data. Document the effective common recovery point.
6. Point the fenced approved recovery host to the recovered store using reviewed
   deployment controls. Verify contained SQL identity/bootstrap and migration
   history against the exact artifact. A newer schema requires a reviewed
   migration/rollback decision; application startup must not migrate the DB.
7. Perform the full verification below while external sending remains contained.
   Reconcile external effects before releasing any recovered queue work.
8. With owner authorization, enable recovered service and bounded test workers,
   validate controlled send/reconnect/user journeys, record `usableUtc`, and
   confirm the original host cannot continue as a second writer/sender.
   Roll back to the preserved original environment if checks fail; record the
   failure and repeat against a new approved point rather than declaring RTO
   at resource completion.

Do not paste generic `az ... restore` commands with placeholder resource IDs
into a live shell. Exact commands and parameters are environment-specific,
approval-gated deliverables of the deployment owner; store them with the
restricted operation record before the exercise. No live commands were run
while preparing this document.

## Required restored-service verification

| Check | Measured proof to retain |
|---|---|
| SQL integrity/schema | Provider completion plus read-only relational checks, row/relationship/ledger-prefix reconciliation, constraints, migration history and reviewed integrity validation |
| Blob content | Read actual restored bytes through the intended private path; compare hashes/lengths/versions for ledger covers and key ring, including replacement/deletion cases |
| Cross-store consistency | Every selected restored Ready media pointer resolves correctly; pending/failed/reclaimable intents accounted for; no content publicly exposed to hide missing authorization |
| Authorization | At least owner, member, nonmember, invited/revoked private user and nonmember administrator checks. Restore must not silently re-grant access revoked after the selected point; reconcile revocations before reopening |
| Data Protection | Approved previously issued protected payload/cookie decrypts only for the intended app where retention requires it; new sessions work; retained old wrapping-key version works; failure is closed |
| Queue reconciliation | Expired leases recover, no duplicate logical in-app notification, immutable due/revision/order retained, obsolete reminders suppressed, no post-start send, fresh provider receipt captured only for eligible controlled work |
| External effects | Exact list of already-sent or uncertain emails/calendar operations and reconciliation decisions; no blind replay or sequence rollback |
| User-visible service | Actual signed-in interactive Query/Join/Leave/Edit on restored resources, expected conflict behavior, protected image reads, fresh readiness and live metric observations; HTTP readiness alone insufficient |

The evaluator's seven `recovery.checks` booleans summarize these observations
for arithmetic review, **never replace them**. A reviewer must inspect actual
provider records, raw validation results and ledger consistency before marking
an acceptance gate.

## Irreversible/external effects and residual risk

Already-submitted email, received/accepted calendar updates and user exports
cannot be rolled back by SQL/Blob restore. A database rollback may remove a
receipt after an external send or lower a calendar sequence that clients have
already seen. Quarantine affected work, reconcile against provider/client
evidence and approve any compensating higher-sequence update/withdrawal using a
reviewed procedure. Do not “fix” this with arbitrary DB sequence edits or claim
exactly-once external delivery.

Disconnected joined-Quest device caches may retain pre-incident or revoked
basics until their documented refresh/expiry/logout rules apply; restore does
not remotely erase them. Record the user communication and revocation
reconciliation plan. Graph membership/departure truth and identity/provider
settings are outside the SQL backup and require current verification.

## Decision and cleanup

Both RPO <=3,600,000ms and RTO <=14,400,000ms plus restored correctness must be
met. Keep failed exercise records even if a later retest passes. A PITR failure,
missing key version, inaccessible private destination, unverified ledger, stale
revocation or unreconciled email prevents acceptance regardless of timings.
Retain evidence under approved policy, then have owners authorize removal of
test resources/senders and verify no orphaned resources/queued mail remain.
Deletion is not authorized by this document. **Actual restore acceptance remains
OPEN.**
