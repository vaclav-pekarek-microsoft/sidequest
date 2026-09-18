# Operational signals and response procedures

`Operations:Monitoring:Enabled=true` opts in to aggregate monitoring. Missing/false
keeps activity observers and queue sampling inactive. The host registers monitoring
after Application services and before building the container. Registration performs
no SQL, Graph, Blob or email calls; it does not prove exporter ingestion or configure
live alerts. Azure hosting/export and its process-level SDK-statistics opt-outs remain
separate requirements.

## Export contract

Only these instruments on `Sidequest.Operations` are exported. Unknown instrument
names are dropped and all unlisted dimensions are removed by the SDK view.

| Instrument | Dimensions | Meaning |
|---|---|---|
| `sidequest.queue.pending`, `.due`, `.dead_letter` | `queue` | Existing aggregate work counts from a fresh successful SQL sample |
| `sidequest.queue.oldest_due_age` | `queue` | Seconds of retry/lease eligibility at sampling; absent when none is due |
| `sidequest.queue.observation_available`, `.observation_stale`, `.observation_age` | `queue` | Availability, staleness and elapsed seconds since the last successful sample |
| `sidequest.operation.completed` | `operation`, `outcome` | One completed invocation of an observed port, including failures |
| `sidequest.operation.duration` | `operation`, `outcome` | Monotonic elapsed seconds for that same invocation |
| `sidequest.http.completed` | `outcome` | One final HTTP request outcome after inner exception handling |

Queues are only `outbox`, `scheduled`, `delivery`. Operations are only
`directory_user_search`, `directory_group_search`, `directory_user_lookup`,
`directory_group_expansion`, `image_sanitization`, `email_submission`,
`authorize_user`, `authorize_administrator`, `authorize_event`, `authorize_quest`.
There are no identity, resource, query, recipient, payload, credential, correlation,
URL, exception-name or exception-message dimensions. Observers never inspect argument
or result content. They do not retry, translate errors, save changes, dispose
caller-owned streams/contexts or change authorization.

Operation outcomes:

- `succeeded`: the port returned normally, not proof of mailbox delivery.
- `rejected`, `denied`, `unavailable`, `conflict`, `dependency_failure`: the original
  domain failure category. `unavailable` deliberately combines missing and hidden
  resources; do not infer private resource existence.
- `permanent_failure`, `retryable_failure`, `uncertain_failure`: the original email
  transport classification. The durable dispatcher still owns retry/uncertainty.
- `cancelled`: an operation-cancellation exception with caller cancellation requested.
  A timeout without caller cancellation is `failed`, not a successful cancellation.
- `failed`: another propagated failure; raw exception details are never exported.

HTTP outcomes are `succeeded`, `redirect`, `unauthenticated`, `forbidden`,
`client_error`, `server_error`, `aborted`, `other`. These include static assets and
health probes; a redirect is not counted as an authorization denial. Blazor command
failures handled within a circuit are not HTTP errors.

## Interpretation limits

Queue observations are absent, not zero, after a failed or stale sample. Check
availability/staleness before interpreting backlog. Activity counters are process
measurements, not durable delivery records; restart/export failure can lose evidence.
No observations must not be read as zero failures or proof that a port was exercised.

An operation is one observed **port invocation**, not a unique user command or an
individual Graph HTTP request. Directory durations include internal pagination,
throttling delays and token acquisition. Repeated durable attempts produce separate
email observations. Authorization observes the default scoped `ResourceAccess`;
custom registrations are preserved and are not automatically instrumented.
Nested calls inside that implementation are not counted again, but separate calls
made by an application service are separate observations.

These durations do **not** establish the two-second end-to-end application SLO.
Neither queue age nor ACS acceptance proves reminder timing, notification submission
percentiles, Outlook interoperability or mailbox arrival. Keep representative
interactive load and provider/recovery evidence as independent release gates.

### Durable worker polling diagnostics

The worker's availability error records `FailureType` and a nullable `SqlNumber`,
not raw exceptions, messages, connection details or work payloads. These are
structured log fields, not new metric dimensions. A healthy web endpoint or queue
sample does not prove that the worker can claim or process work.

SQL error 650 identifies an isolation level incompatible with `READPAST`.
Queue claims explicitly establish ReadCommitted on their own connection before
executing the existing atomic autocommit updates. The `UPDLOCK`, `READPAST` and
`READCOMMITTEDLOCK` hints remain intact, including support for snapshot-enabled
read-committed databases. Application mutation transactions retain their existing
isolation; do not weaken them or disable pooling to work around a worker failure.

If polling errors recur, preserve the deployed source/configuration and numeric
classification, then investigate that failure. Do not assume every SQL error is
650, count retained work as processed, reset leases, or replay user commands.

## Operator response and staging rehearsal

Before enabling live alerts, an approved operational owner must verify actual metric
ingestion, retention, role access, notification routing and alert thresholds on the
target deployment. No alert has been installed or tested by committing this document.

| Signal | First response | Do not do |
|---|---|---|
| Missing/stale queue observation | Check private SQL connectivity, identity permissions, migration readiness and sampler health | Treat an absent backlog as an empty queue |
| Sustained due backlog / new dead letters | Inspect the authorized delivery/admin view and worker health; preserve failure classifications | Delete work, reset leases or replay indiscriminately |
| Directory dependency/other failures | Check approved Graph permission/workforce-policy configuration, provider availability and throttling | Add group-based authorization or shorten provider retry delays |
| Image rejection increase | Separate invalid/oversized inputs from dependency failures and sanitizer capacity | Relax size/pixel/decode rules or expose private image data in diagnostics |
| Email permanent/retryable failures | Check approved sender/configuration and provider status; follow durable retry policy | Claim email was delivered from an acceptance counter |
| Email uncertain failures | Reconcile approved provider evidence and logical delivery identity before authorized recovery | Automatically resubmit a possibly accepted message |
| Resource denial/unavailable increase | Check legitimate membership/eligibility changes and use authorized support procedures | Infer hidden resources, grant admin content access or weaken guards |
| HTTP server errors / unready health | Correlate existing safe server diagnostics, private dependencies and worker state | Export request paths, authentication proofs or exception bodies |

In staging, establish a normal baseline, trigger controlled failures using approved
test identities/data, verify the correct aggregate outcome and preserved user-visible
failure, and verify the alert reaches its named owner. Restore dependencies and check
recovery without replaying user commands. Record deployment/source, time window,
aggregate evidence and operator approval through the release evidence process; keep
credentials and private provider/user data out of repository files and CI artifacts.
