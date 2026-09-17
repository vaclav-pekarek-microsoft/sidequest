# Offline measurement dossier evaluator

`tools\release-acceptance\evaluate.mjs` uses only Node built-ins. It reads one
bounded local JSON dossier and six hash-bound local artifacts; it performs no
network access, authentication, load generation, SQL action or restore.

**Trust boundary:** this is consistency and threshold arithmetic, not evidence
attestation. A file hash only identifies bytes. Anyone who can invent raw rows,
artifact bytes and metadata can obtain numerically conforming results. The tool
therefore **always emits `releaseAccepted: false` and all live gates OPEN**, even
when every numerical target is met. `review-required` is never measured-load or
restore certification. No field, flag or exit code can approve release. A summary
such as `p95: 1000, restored: true` is rejected, not converted into raw evidence.

An independent authorized reviewer must inspect provider restore operation
records, contiguous recovery markers, actual application/circuit/receipt logs,
identity isolation and environment provenance. The tool does not authenticate
Azure/Entra/GitHub, attest timestamps, verify the content semantics of arbitrary
attachments, prove that all eligible work was captured, or enforce statistical
representativeness. Do not submit its unit-test fixture as a measurement.

## Commands and outcomes

```powershell
node --test tools\release-acceptance\evaluate.test.mjs
# After an approved real exercise and evidence custody/review:
node tools\release-acceptance\evaluate.mjs <approved-local-dossier-path>
```

The second line is usage notation: replace the angle-bracket path only with an
existing reviewed local dossier, not a new fabricated example. Store dossier/
raw files outside source-controlled deliverables in approved restricted storage.
Do not include cookies, tokens, credential files, real addresses, Quest text or
employee identifiers. Use pseudonymous trace IDs and restricted identity mapping.

| Exit | Status | Meaning |
|---|---|---|
| 0 | `review-required` | Structurally adequate and numerically within conservative guards; **release not accepted**, provenance/live gates open |
| 1 | `targets-not-met` | Validly structured but a measured target/correctness check fails; review and retest required |
| 2 | no dossier result | Invalid/missing/inconsistent/insufficient/oversized input or artifact; no calculation accepted |

CLI errors deliberately do not echo input values or filenames. There is no
“ignore errors,” approval override, threshold override or inferred healthy zero.
Results include calculated p95 per class/global, on-time/total counts, RPO/RTO
milliseconds, total failed checks and at most the first 100 failure identifiers.

## Strict schema v1

Every object has **exactly** the keys below: missing/unknown fields reject.
Numbers are safe integers, no strings-as-numbers, fractional durations or NaN/
Infinity. IDs are 1–80 ASCII letters/digits/underscore/hyphen, starting with a
letter/digit. Timestamps are canonical `YYYY-MM-DDTHH:mm:ss.sssZ`, including
milliseconds and a real calendar date. All timing rows use nonnegative
milliseconds relative to `run.startedUtc`; server spans must be conservatively
rounded **up** to whole milliseconds, not rounded down past a target.

The file must be exact UTF-8 two-space pretty JSON produced by
`JSON.stringify(data, null, 2) + '\n'` (final LF, no BOM). This canonical encoding
also rejects duplicate keys, alternate numeric encodings and otherwise
ambiguous JSON rather than silently accepting last-key-wins input. Each input/
artifact is a nonempty regular file <=32 MiB, not a symlink. Filenames are
same-directory simple lowercase names, no separators, reserved Windows devices
or traversal. Do not place bundles in untrusted writable directories: this is
an offline checker, not a hardened hostile-filesystem service.

| Object | Exact keys / constraints |
|---|---|
| Root | `schemaVersion` (exactly 1), `run`, `artifacts`, `sessions`, `operations`, `isolation`, `notifications`, `reminders`, `recovery` |
| `run` | `id`, `commit` (40 lowercase hex), `startedUtc`, `durationMs` (1,800,000–3,600,000), `environment` (exactly `approved-representative`), `authentication` (exactly `entra-distinct-users`), `transport` (exactly `blazor-interactive`), `users` (exactly 300), `databaseBytes` (positive safe integer), `seed`, `hostProfile` |
| `artifacts[]` | Exactly six rows with unique `role` and `file`; row keys: `role`, `file`, `sha256` (64 lowercase hex), `bytes` (1–33,554,432). Roles exactly `environment`, `generator`, `application`, `circuits`, `delivery`, `recovery`; declared bytes/hash must match actual local file |
| `sessions[]` | Exactly 300; keys `user`, `circuit`, `fromMs`, `toMs`; unique users and circuits; every interval begins at 0 and ends at `durationMs` |
| `operations[]` | 18,000–100,000 rows; keys `id`, `user`, `operation`, `atMs`, `appMs`, `ok`; unique IDs, known users; operation one of `discover`, `quest-detail`, `joined`, `join-leave`, `edit`, `notifications`; `atMs` in window, completion within window; `appMs` nonnegative integer; `ok` boolean |
| `isolation[]` | Exactly 300, unique `user`; keys `user`, `otherUser`, `ownRead`, `foreignReadDenied`, `foreignMutationDenied`; distinct known users; three boolean observed outcomes |
| `notifications[]`, `reminders[]` | Each 100–20,000 rows; keys `id`, `dueMs`, `submittedMs`, `providerHealthy`; IDs unique within each kind; `dueMs` 0 through `durationMs - 120000`; `submittedMs` null for no submission or a timestamp from due through end of window; `providerHealthy` must be true for this whole predeclared cohort |
| `recovery` | `incidentUtc`, `usableUtc`, `sqlRecoveredThroughUtc`, `blobRecoveredThroughUtc`, `checks`; incident at/after load-window end, usable >= incident, both recovered-through <= incident |
| `recovery.checks` | Exactly seven booleans: `sqlIntegrity`, `blobContent`, `crossStoreConsistency`, `authorization`, `keyRing`, `queueReconciliation`, `externalEffectsRecorded` |

No successful fixture file is shipped. Build the dossier only from genuine
captured/derived measurements with the following provenance attached:

| Artifact role | Required review content; hashes alone do not prove it |
|---|---|
| `environment` | Approved deployment/source/artifact/config fingerprint, actual host/SQL/Blob sizing and used bytes, region/private path, data recipe/counts, approved identity policy, owner approvals and observation clock bounds |
| `generator` | Approved harness/version/source hash, seeded action schedule and mix, generator hardware/headroom, full attempt/cohort inventory and exclusions with reasons |
| `application` | Per-operation source spans and UI acknowledgement/correlation, monotonic elapsed durations, measured provider exclusion derivation, failure/timeouts and completeness reconciliation; raw privacy-safe isolation expected/actual checks |
| `circuits` | Authenticated circuit lifecycle/revalidation logs and 10-second census proving 300 distinct approved users, no synthetic/shared identities or gaps in the submitted window |
| `delivery` | Source eligible-work inventory, due/revision history, attempts/receipts, independent whole-cohort provider health, deadline derivation and duplicate/uncertain-send reconciliation |
| `recovery` | Actual approved SQL and Blob operation IDs/results/timelines, source/restored marker ledger, key-ring/authorization/cross-store validation, usable-time observation and external-effect decisions |

The six bounded files may be sanitized extracts plus **restricted immutable
references** to larger original evidence. The reviewer must access the originals
and verify extraction completeness. JSON rows themselves remain unauthenticated
local records. Do not rename an invented summary `application.log` to imply it
is a captured trace. Keep failed originals and evidence custody records.

## Evaluation rules

* Reject too few users/circuits/rows, idle users (<6 operations), any measurement
  minute with <300 operations, any class with <100 operations, or class mix
  outside ±5 **percentage points** of 25/25/20/15/5/10 respectively.
* Evaluate nearest-rank p95 globally and per class; **2,000ms fails**.
* Every ordinary operation must have `ok: true`; each isolation outcome must
  succeed. These conservative correctness guards are independent of p95.
* Ordinary notification origin is earliest eligibility/domain commit; reminder
  origin is immutable due time. Null submission stays in the denominator.
  Notifications need on-time ×100 >= total ×95; reminders need all on time.
  A delay of exactly 120,000ms is on time, 120,001ms is late.
* Mixed unhealthy/unknown-provider cohorts are rejected rather than filtered;
  retain the failure and rerun a predeclared healthy window.
* RPO uses incident minus the older SQL/Blob verified contiguous recoverable
  point; RTO uses usable minus incident. Exactly 3,600,000 / 14,400,000ms passes
  arithmetic, one millisecond more fails. Every actual-restore check must be
  true. Requested PITR timestamps and fabricated check booleans are not proof.
* This deliberately bounded format expects load followed by recovery of that
  approved corpus. Separately collected exercises still require owner review and
  linked provenance; do not change dates merely to force them into this format.

## Deterministic test evidence

`evaluate.test.mjs` creates in-memory **test-only generated data** and local
scratch files under the repository only for CLI tests, then removes those files.
It never runs app load, sends email or accesses Azure.

| Requirement | Exact test evidence |
|---|---|
| “exact thresholds” | `p95 strict two-second boundary 1999`, `p95 strict two-second boundary 2000`, `notification 95-percent boundary with 5 late submissions`, `notification 95-percent boundary with 6 late submissions`, `every reminder must meet its own two-minute deadline: 120000`, `every reminder must meet its own two-minute deadline: 120001`, `recovery boundary fails one millisecond outside sqlRecoveredThroughUtc`, `recovery boundary fails one millisecond outside usableUtc` |
| “missing/inconsistent/insufficient data and failures” | `rejects summary-only dossier`, `rejects insufficient reminders`, `rejects duplicate circuit`, `rejects idle measurement minutes`, `rejects digest mismatch`, `fails foreign read leak even with fast timings`, `failed actual-restore check keyRing cannot be hidden by timestamps` |
| “never accept invented summary claims as proof” | `rejects summary-only dossier`, `numeric conformity never grants release acceptance or validates invented provenance` |
| “keep all current live gates open” | `numeric conformity never grants release acceptance or validates invented provenance` |

Passing these tests verifies the evaluator's behavior, not application coverage
or any A24 observation.
