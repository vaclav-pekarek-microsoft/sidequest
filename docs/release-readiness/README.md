# Release acceptance dossier

Prepared against **613e7955b2bc1537f545d37ca7e45103d7a9056c** (verified M4
integration), 2026-09-16. This is preparation, **not release approval**.
The accepted requirements remain [handoff sections 52–58](../sidequest-project-handoff.md).
UI polish is explicitly deferred; functional accessibility is not.

## Deliverables and current disposition

| Deliverable | Purpose | Current result |
|---|---|---|
| [A01–A26 evidence matrix](acceptance-matrix.md) | Exact executed regression references and remaining proof | Existing bounded automated support; release rows not signed off |
| [Representative workload](load-runbook.md) | Data, distinct-user circuits, operation mix, timing and notification measurements | Not run; approvals and instrumentation outstanding |
| [SQL/Blob recovery](recovery-runbook.md) | Approved recovery exercise and end-to-end RPO/RTO measurement | Not run; no actual restored resources or recovery evidence |
| [Device/accessibility/Outlook](client-runbook.md) | Human-observed supported-client acceptance | Not run on approved physical devices/mailboxes |
| [Offline evaluator](evidence-format.md) | Bounded dossier consistency, hashes and exact arithmetic | Deterministic local tests only; never closes a release gate |

No application changes, cloud actions, credentials, authentication bypass,
resource creation, deployment workflow modification or release load generator are
included. There is deliberately no ready-made "passing" release dossier. Generated
unit-test records are not measurements. No full application suite was rerun here.

## Existing source-pinned execution evidence

[CI run 35071694703](https://github.com/vaclav-pekarek-microsoft/sidequest/actions/runs/35071694703)
is the supplied successful exact-source run. Retained `test-results` TRX files
were read, their result counts checked, and the matrix method names matched to
passed results. Preserve the original artifact before GitHub retention expires,
with run URL, attempt, full source SHA, workflow/job metadata and access-controlled
custody record. TRX files themselves do not independently authenticate the Git
revision; the trusted run-to-artifact association must remain with them.

| Artifact | Executed / passed | SHA-256 |
|---|---:|---|
| `unit.trx` | 1,923 / 1,923 | `709f2542fa2f3b700c7c84ba596f2f7e4e9e1b24517c86a29a9019b8091f59f1` |
| `integration.trx` | 1,032 / 1,032 | `d0aeae5d2958a6715c2b02defc9c1c4bd49a2a8ac75e7de1192c803a06c91d50` |
| `browser.trx` | 98 / 98 | `56c2b4a17c90fbeb8247af2fa1851b823f15c5e9894b2acd50f47f7ec4b044a2` |
| `node-step.log` (extracted step) | 56 / 56 | `cfe1cb54e1648b02a0bb436a0574635bb9b0941358667e759b2e17838d32c7e1` |

All three TRX files have zero failed/not-executed results. SQL cases run against actual
isolated SQL Server, not live Azure SQL. **98 is the browser-project case count,
not 98 browser journeys**: 52 runtime cases and 46 pure helper/configuration/routing
cases. Runtime classes contribute CoreWorkflow 17, FoundationCompatibility 7,
AuthenticationStartupBrowser 1, ExperienceJourney 5, HostLifecycle 5,
OfflineStorage 8 and WorkerNetwork 9. The remaining 46 are BrowserContextOptions 3,
SyntheticAppSettings 28, AuthenticationStartupRouting 14 and LoopbackWorkerProbe 1.
Parameterized cases count separately. Synthetic loopback Chromium is neither
live Entra nor certification of supported physical devices.

The retained `node-step.log` was read and its SHA-256 verified: **56 tests,
56 passed, zero failed/cancelled/skipped**. It is the parent's extracted Node-step
segment from GitHub job **104714463607**, retrieved through the job-log endpoint
for this same run, not a TRX result or a newly executed test run. Its source/run
association relies on the original GitHub job-log custody and parent extraction;
the local digest authenticates neither that association nor the Git revision.
Preserve the original job log and extraction/custody record alongside this
bounded segment. No newer run's counts are substituted for this historical run.

## Gates that remain OPEN

* Assigned operational/release owners and recorded approval authority.
* Workforce tenant/application/role, excluded-guest policy, Graph policy and
  consent, verified departure/recovery procedure, approved mailbox and ACS sender.
* Region, residency, retention, employee/device local-cache policy and recipient
  consent. Use only approved synthetic content and approved test identities.
* Protected deployment approval, federation, approved private-network runner,
  reviewed bootstrap/migrations, resource sizing/cost and actual deployment.
* Verified provider/identity behavior, operational signal ingestion, dashboards,
  alerts and on-call delivery. Queue gauges alone do not measure deadlines.
* Real supported-device, WCAG 2.2 AA and Outlook evidence.
* Representative 300-user load, actual SQL/Blob recovery, evidence provenance
  review and signed residual-risk decisions.
* Specific uncited automated combinations listed in the matrix. This slice adds
  evaluator regressions, not missing product acceptance scenarios.

The offline evaluator cannot approve any of these. An authorized owner must
independently compare raw evidence with the deployed environment and record
decisions, dates, limitations and linked evidence. An exception requires explicit
renegotiation of the accepted specification before release, not changing a JSON
number or converting a missing observation to zero.

## Safe local validation

From the repository root, using the already-installed Node runtime:

```powershell
node --test tools\release-acceptance\evaluate.test.mjs
```

No browser launch, runtime download, dependency restore, network access or live
service is involved. The dedicated offline CI workflow runs only this command.
It proves the checker, not application/release acceptance.
