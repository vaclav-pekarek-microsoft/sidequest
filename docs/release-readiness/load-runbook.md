# Representative 300-user load and delivery runbook — NOT EXECUTED

This implements the measurement protocol for handoff §57 / A24; it does not
authorize deployment, generate approved identities, or supply a runnable browser
load harness. Those prerequisites are still outstanding. A local synthetic
throughput result, `/health/ready`, HTTP 200, 300 HTTP connections, or 300 tabs
sharing one cookie **cannot** establish acceptance.

## 1. Stop until prerequisites are approved

The deployment owner must record, in restricted evidence:

* Exact application/container artifact digest, full commit, migration set,
  configuration fingerprint, Azure region, App Service SKU/instance count,
  WebSockets/affinity/Always On, SQL SKU/max size/current used bytes, Blob
  configuration, VNet/private DNS/runner path and worker settings. The Bicep
  single-instance baseline is a starting configuration, not proven capacity.
* Cost, resource, test window, stop/rollback and operational-owner approval.
  Obtain private-runner and approved test-identity access through the existing
  process. Do not create resources, grants, credentials or firewall exceptions
  just to run this protocol.
* **300 distinct approved workforce test accounts**, scoped role assignment and
  consent to load. Authenticate through real approved Entra flows on managed
  devices/runners; no synthetic development mode, fabricated cookie, shared
  session, copied browser profile or auth-policy workaround. Retain only
  pseudonymous IDs in exported measurement rows; keep the mapping restricted.
* Approved Graph policy and ACS sender/recipients, sending quotas/rate limits,
  provider-health observation and a safe approved mail domain/list. No employee
  spam. No provider mock can close healthy-provider delivery acceptance.
* Approved synthetic-content profile and retention/local-cache policy.
* A pinned, already-approved browser engine and **real Blazor UI load driver**,
  with one isolated authenticated context per user and traceable server circuit.
  Existing CI fixtures are loopback/synthetic only and must not be repointed to
  a live tenant or weakened. A new approved harness is future work, not included
  here. Do not install browsers locally to bypass managed Edge policy.
* Privacy-reviewed **application operation spans** and authenticated circuit
  open/close/revalidation observations. The current aggregate queue gauges,
  port-operation metrics and HTTP outcomes do not supply that end-to-end evidence
  (see [operational semantics](../../src/Sidequest.Web/Operations/README.md)).
  Missing instrumentation is a blocker, not zero latency.
  The parent owns instrumentation, alerts and monitoring integration.

Record generator OS/CPU/RAM, browser/driver version and script hash, clock
synchronization bounds, source region/network latency and generator CPU/memory/
socket saturation. Use enough approved generators that their saturation does not
cap load. A representative deployment must exercise actual SQL, private Blob,
workers and identity/provider boundaries. Explain how the configuration/data
relate to intended production; labels in JSON are not evidence.

## 2. Reproducible data contract (baseline proposal; owner approval required)

Use a versioned seed recipe, deterministic identifier mapping and a recorded UTC
anchor date. Construct relationships through reviewed application commands or an
approved seed mechanism that preserves invariants/audit/outbox; do not bypass
membership or write hand-edited production rows.

| Entity/profile | Proposed repeatable workload |
|---|---|
| Users | 300 signed-in actors; 30 Event/Quest owners, 270 ordinary members; two separately approved administrators for operational checks (not extra load actors) |
| Events | 12: four Active current-week, two Draft, two Completed, two Cancelled and two Archived; retain publication history distinctions; use UTC and at least two approved IANA zones including DST |
| Current membership | All 300 are individual members of primary Event; divide 300 into three 100-user cohorts for restricted secondary Events; record exact member/nonmember matrix |
| Quests | 600 ordinary Quests, 50 per Event; on each Active Event 40 Active, five Draft and five Suspended. Active published ordinary Quests: 70% public/30% private; draft/terminal state follows accepted lifecycle. Add 300 Active private sentinel Quests in the primary Event (900 total) |
| Ownership/access | Eligible owners retained for every resource; the 30 owning actors drive the edit mix on their designated resources without shared accounts. A private sentinel per actor is readable only by that actor and its authorized owner, not the comparison actor; choose comparison users who are not that sentinel's owner |
| Participation | Each actor joined to 10 and following five accessible Quests; private invite-only resources with no participation; advised capacity 1, 5 and 20 including deliberate over-capacity joining; never grant access by group |
| Media | Sanitized approved generated images on half the Quests, with recorded byte/pixel distribution up to inclusive accepted limits; record stored versions/bytes and prior assets pending cleanup |
| History/queues | 100,000 audit rows, 30,000 retained notification rows, representative delivery/schedule/completion history produced by the seed recipe; also a fresh empty-queue baseline and separately declared backlog exercise |

These are workload choices, **not new product acceptance thresholds or a claim
that the dataset exists**. The owner must increase historical size where the
expected deployment retention/usage exceeds this baseline. Capture row counts
for Users, Events, memberships, Quests, participation, audits, notifications,
outbox, scheduled work, delivery/calendar state and media; record used/index/log
bytes before and after, SQL statistics/index state and Blob bytes/versions.
Retain seed script/content hashes and random seed. Do not represent a tiny
empty database as representative merely because the evaluator accepts a
positive `databaseBytes`.

Use unique run-scoped targets and audit correlation IDs. Restore a known approved
baseline before comparable repeats; otherwise record changes in corpus/queue/
cache state. No unapproved deletion/retention cleanup.

## 3. Traffic schedule and observation

1. Verify readiness, migrations, configured providers and empty/stable queues.
   Verify actual UI interaction establishes an authenticated server circuit;
   prerendered HTML or a WebSocket handshake alone is insufficient.
2. Authenticate accounts ahead of timing (respect their session expiry). Ramp in
   steps of 50 contexts per minute to 300; allow five minutes warm-up at full
   concurrency. Record ramp/warm-up errors, but keep their samples separate.
3. Hold **300 distinct active signed-in interactive circuits for 30 continuous
   minutes**. Renew no session via a bypass. Every actor drives actual UI events,
   waits for acknowledgement and checks its own returned state. Think time is
   seeded 5–15 seconds after completion; record actual offered/completed rates.
4. Use this operation mix, interleaved per actor and minute:

   | Class in evidence | Share | UI action and correctness check |
   |---|---:|---|
   | `discover` | 25% | Search/page/filter current published discovery, including nonmember summary-only views |
   | `quest-detail` | 25% | Authorized public/private details and permitted rosters; image read measured separately as provider-dependent |
   | `joined` | 20% | Dashboard/joined list/date filters including history and user-local times |
   | `join-leave` | 15% | Join/leave/follow transitions on authorized per-user targets; assert exclusivity and advisory capacity. Count each acknowledged command separately |
   | `edit` | 5% | Authorized owner's Quest edit with rowversion; verify persisted result. Do not share one owner account among 300 contexts |
   | `notifications` | 10% | Inbox page/unread count/mark-read with identity-scoped assertions |

   Expected traffic at a 10s mean think time is approximately 30 operations/s
   before operation time. Record actual rate and queuing; **do not claim that
   a closed-loop generator exposes overload without coordinated-omission bias**.
   Add a separately approved burst/open-arrival phase to expose stalled users,
   and report scheduling delay and unstarted/abandoned work. It is not a
   substitute for the steady-state interactive phase.
5. Retain continuous circuit logs and 10-second census observations showing
   pseudonymous user→circuit association, connected/authenticated/revalidated
   state and reconnect/expiry gaps. The evaluator's session intervals may only
   be derived from those logs. A disconnect, auth expiry or fewer than 300 users
   invalidates the continuous window; report it and rerun a complete window.
6. During the window, for **every** user: own private sentinel read succeeds,
   comparison user's private read yields no content/count/existence distinction,
   comparison user's mutation is denied with no state/audit/outbox mutation.
   Verify sentinel values/digests, not only HTTP codes. Keep expected-versus-
   actual data in restricted raw logs; `isolation` booleans are derived results.
7. Run revocation, reconnect, concurrent hot-resource edits, image upload,
   Graph bulk expansion and actual restart/failure exercises in separately
   labeled phases; expected conflicts/denials must not contaminate successful-
   action latency counts. Do not silently discard unexpected errors from the
   ordinary mix. Privacy or integrity failures stop the exercise immediately.
8. Drain work, capture resource/queue metrics, errors/timeouts/CPU/memory/GC,
   circuit counts, SQL waits/locks/deadlocks and generator headroom. All original
   attempts, failed runs, interrupted windows and exclusions stay in the dossier.
   Retest fixes with exact new source/sizing; do not pool revisions.

## 4. Timing and exact thresholds

Application latency is elapsed wall time around a query/command on the server,
including app scheduling/SQL/transaction work. Correlate it to the acknowledged
interactive UI action and record end-to-end client timing separately. Exclude
interactive sign-in and **only measured external provider spans**; do not subtract
guessed constants or SQL time. For overlapping external spans subtract their
union, not their sum; persist the span derivation and clock uncertainty.
Missing/unsampled/unmatched operations invalidate the cohort. Include failed/
timed-out attempts in the raw denominator and error report; never record a
timeout as fast success. The checker requires zero unexpected ordinary-action
failures as a conservative review guard, not a newly promised production SLA.

* p95 = sorted observed application duration at rank `ceil(0.95 × N)` (one-based,
  nearest rank). **p95 < 2,000 ms**, not <=. Report global and each class, plus
  query/command grouping, p50/p99/max, sample counts and timeout/error rates.
* Tool adequacy guards: 30–60 minute window, exactly 300 users, 18,000–100,000
  total operations, >=100 per class, each share within five percentage points of
  the table, >=300 operations in each measurement minute and >=6 per actor.
  These reject trivial dossiers; they do **not** prove representative sizing,
  steady offered rate, instrumentation completeness or sufficient statistical
  confidence. Owner review uses the full protocol and raw measurements.
* The UI's displayed stale/error/conflict state and data isolation are correctness
  requirements independent of response speed.

## 5. Notification and reminder cohorts

Predeclare eligible logical work by recipient and immutable revision/ID, using
at least 100 ordinary notifications and 100 reminders due during steady state.
Stagger and burst due times; include joined private/public attendees, varied
numeric hour leads and due times at start/end boundaries. Include near-window
joins/reschedules as separate correctness cohorts; followers/departed users are
negative controls, not eligible sends.

For each ordinary notification capture domain commit/eligibility UTC (the
latency origin), outbox ID, expansion/enqueue/claim/attempt UTC, recipient
pseudonym, durable logical delivery ID, provider acknowledgement UTC/receipt and
eventual worker completion. Measure from **earliest eligibility**, not from
last retry/claim, and do not reset origin on expansion/retry. Reconcile all
eligible work against source database and provider receipts. Count a logical
submission once; duplicates/uncertain acceptance get a separate incident record.

For reminders capture the immutable due UTC derived from the Quest start and
the attendee lead, start revision, actual provider acknowledgement and any
supersession/access-loss. Do not replace due time with job claim time.

* At least **95%** of all eligible notification work must be accepted/submitted
  to a healthy provider **<=120,000 ms** after eligibility. Exactly 95/100 at
  exactly 120,000ms qualifies arithmetically. Missing submissions are late, not
  removed from the denominator.
* **Every eligible reminder** in the healthy cohort must be submitted
  **<=120,000 ms** after due time; §57 does not give reminders a 5% allowance.
  No post-start send. Suppressed stale/obsolete work belongs in correctness
  evidence, never counted as a successful timely submission.
* Observe for at least two minutes after the final included eligibility/due
  time; predeclare the cohort cutoff. An incomplete observation is insufficient
  evidence, not a failure-free run.
* Record independent approved provider-health/quota evidence covering the entire
  cohort. Report provider outage/throttling separately. The conservative offline
  checker rejects mixed unhealthy cohorts; investigate and rerun a predeclared
  healthy window instead of selecting only favorable rows.
* Submission/receipt is not mailbox arrival or calendar insertion. Queue
  `oldest_due_age`, pending count and provider HTTP request duration cannot
  substitute for per-work latency. Missing/stale observations are unavailable.

## 6. Closeout

Freeze restricted raw evidence and hashes, reconcile totals with database/
provider/circuit logs, then produce the [bounded dossier](evidence-format.md).
Get an independent owner to verify environment, provenance, privacy, statistical
adequacy and all exclusions before deciding A24. Preserve failed results and
retest links. Restore the approved baseline or retain it under the approved
policy; stop generators and verify no queued mail can escape the test scope.
No measurements or approvals from these steps exist yet.
