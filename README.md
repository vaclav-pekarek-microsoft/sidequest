# Sidequest

An internal, Quest-first event activity organizer. The accepted V1 specification and
decision log are in [the project handoff](docs/sidequest-project-handoff.md).

Implementation is in progress. The M1 foundation is verified: shared contracts,
SQL persistence, Entra/development authentication, a Fluent UI shell, and CI.
M2 Event/Quest workflows and delivery are integrated into the host and have passed
combined workflow and browser acceptance. Uploads, administration/templates, and
dashboard/PWA/offline basics have passed combined M3 acceptance, including reconnect
and access hardening. M4 release hardening remains. This is not a production-ready release;
live tenant, email, hosting, and data-policy approval gates remain open.

## Local development

Prerequisites: the SDK pinned in `global.json`, SQL Server (Windows LocalDB is supported),
and GitHub CLI authenticated as `vaclav-pekarek-microsoft` for publishing changes.
All package dependencies restore from the repository's public NuGet source configuration.

```powershell
dotnet restore Sidequest.slnx
dotnet tool restore
dotnet ef database update --project src\Sidequest.Infrastructure --startup-project src\Sidequest.Infrastructure
dotnet run --project src\Sidequest.Web -- --urls http://localhost:5078
```

The design-time default database is `SidequestDevelopment` on
`(localdb)\MSSQLLocalDB` with integrated authentication. Set `SIDEQUEST_SQL_CONNECTION`
for EF tooling against another explicitly chosen development database. Configure the
running app with `ConnectionStrings__Sidequest` for that same database.
Do not apply a development migration command to production accidentally.

`20260916112116_ReserveMembershipRequestHistory` adds a nonfiltered covering
`MembershipRequests(EventId, UserId, CreatedUtc)` index including `Status`; apply it
before running this build. Membership requests read pending and hourly-history facts
with one Infrastructure-owned write-intent reservation after authorization and the
Event lock. Empty adjacent ranges can wait, but do not acquire compatible shared
locks that later deadlock on insertion. Serializable isolation, inclusive hourly
limits, pending-request idempotency, and atomic audit/outbox commits are unchanged.
The migration changes no rows; rollback removes only the index and must be paired
with the prior application build. Readiness rejects an unapplied migration.

Development sign-in uses conspicuously labeled synthetic accounts only when the
Development environment and explicit development authentication mode are both active.
It is not proof that live Entra integration is configured. Production must use Entra,
an approved workforce admission policy, and an explicitly configured bootstrap
administrator; there is no "first user becomes admin" behavior.
See `src\Sidequest.Web\AGENTS.md` for authentication/rendering configuration.

If native sign-in starts before browser initialization, successful authentication can
reach a completion page without a device generation. That page deliberately shows
guidance and requires **Continue to Sidequest** rather than activating device storage
or navigating automatically. This is distinct from a failed authentication request
or a rejected nonempty generation; neither should be hidden by an automatic retry
or a generic continuation fallback.

The composed host starts durable SQL processing after checking that every supported work type
has exactly one handler. Running it can process existing queued work in the configured
database. Use an explicitly chosen development database, not a shared production catalog.
Missing email configuration causes explicit delivery failures, not simulated success.

Provider configuration is separate from sign-in configuration:

- `Directory:Graph`: tenant, approved workforce extension/value/policy and expansion limits.
- `Directory:Credentials`: matching tenant, Graph application client ID and protected secret.
- `Delivery:Email`: verified sender/organizer, ACS connection string or managed-identity
  HTTPS endpoint, optional managed identity client ID and submission timeout.
- `Events:Limits` and `Delivery:Work`: bounded workflow, polling, lease and concurrency settings.
- `Media:Storage`: private `ContainerName`, HTTPS `ServiceUri`, optional
  `ManagedIdentityClientId`, and `OperationTimeout`, or a secret-store `ConnectionString`.
  Preprovision the private container and disable account-level anonymous Blob access.
  Missing storage configuration causes explicit upload failure, not simulated success.
- `Administration:DepartureRecovery`: disabled by default. Enabling it requires an
  externally approved `ProcedureReference` and separately verified departure evidence;
  configuration does not constitute approval or create that evidence.

Directory tenants must match the authenticated tenant. Store credentials in user secrets
or the deployment secret store; never commit them. Missing Graph policy or credentials
fails directory operations explicitly without preventing existing Event access.
Calendar downloads require a configured organizer. CI uses a reserved synthetic organizer
address solely for local calendar rendering, with no ACS credentials or live email calls.

Explicit `Hosting:Azure:Enabled=true` selects managed-identity Blob/Key Vault Data
Protection and metrics-only Azure Monitor export. It requires non-Development Entra
hosting, stable key-ring settings, an Application Insights connection string and
the process-level SDK diagnostic opt-outs described in the infrastructure guide.
The default synthetic host never opts in. The [M4 infrastructure draft](infra/README.md)
documents these settings and its remaining deployment/approval gates; no cloud
resources are created by registering services or running the offline checks.

Readiness also verifies applied SQL migration history; migrations remain a separate
authorized deployment step. Optional `Operations:Monitoring:Enabled=true` collects
aggregate queue observations without provider calls or writes. Failed, missing and
stale samples are unavailable, not healthy zero backlogs; see the infrastructure
guide before configuring alerts.

## Verification

```powershell
dotnet build Sidequest.slnx --configuration Release
dotnet test tests\Sidequest.UnitTests --configuration Release
dotnet test tests\Sidequest.IntegrationTests --configuration Release
```

SQL integration tests create uniquely named disposable databases. On Windows the
default server is LocalDB; CI supplies `SIDEQUEST_TEST_SQL` for an isolated SQL Server
container. The test login needs permission to create/drop its test databases. Never
point this setting at a production server.
Browser checks run against an explicitly started synthetic local app using
`SIDEQUEST_BASE_URL` (loopback only). CI installs Chromium in its isolated Linux runner,
starts the development app against a disposable SQL database, and runs the browser
project. Do not bypass managed local browser policy to run these checks. The browser
project includes M1 compatibility scenarios and M2 membership, participation, private
access, moderation, calendar recovery, stale-editor and mobile interaction journeys.
The shared browser fixture blocks service workers by default. Dedicated M3 offline
scenarios can explicitly opt in without changing other contexts or their origin
routing. That harness option alone is not evidence that offline behavior is complete.
CI also runs the client lifecycle regressions with Node's built-in test runner.
Neither synthetic suite establishes approved live-provider or release acceptance.
CI requires nonempty unit, SQL integration, and browser results with
every discovered scenario executed and passed; skipped suites do not satisfy the gate.

The [M1 baseline CI run](https://github.com/vaclav-pekarek-microsoft/sidequest/actions/runs/34861029210)
passed 1,298 unit cases, 324 real-SQL cases, and 35 browser-project cases, including
seven actual Chromium journeys. These cover synthetic sign-in, Fluent binding/dialog
content, 360px keyboard interaction without horizontal overflow, protected navigation,
and logout. The strict Release build also enforces public XML documentation.

The [M2 combined acceptance run](https://github.com/vaclav-pekarek-microsoft/sidequest/actions/runs/34896985551)
passed 1,549 unit cases, 850 real-SQL cases, and 45 browser-project cases, including
17 actual Chromium journeys. Its 104 new composition cases exercise real production
services through durable lifecycle, membership, cancellation, reminder, and delivery
boundaries. They also verify that an early completion claim cannot acknowledge an
unfinished Quest: the original work completes at its immutable deadline. Browser
journeys verify real authorized navigation, private access, calendar recovery,
concurrency feedback, and 360px keyboard flows with prerender-safe controls.

The [M3 combined acceptance run](https://github.com/vaclav-pekarek-microsoft/sidequest/actions/runs/34967281852)
passed 1,845 unit cases, 1,019 real-SQL cases, 96 browser-project cases and 54 Node
regressions, with zero failures or skips. It covers the composed media,
administration/templates, dashboard and bounded offline features, current-cookie
reconnect verification, retained-view reauthorization and unsaved-input preservation.
The controlled native-startup journey actually exercised the missing-generation
completion guidance and explicit continuation, separately from initialized sign-in.
Real SQL also verifies write-intent scheduling before insertion, unchanged work
deduplication and caller-owned commit/rollback.

This evidence does not establish live Entra/Graph/ACS or Outlook approval, physical
device/screen-reader certification, the 300-user load target, SQL/Blob recovery
targets, operational ownership, or production deployment. Those remain M4 gates.

## Architecture and contribution policy

- `Sidequest.Domain`: entity/state vocabulary and pure access, participation, and time rules.
- `Sidequest.Application`: use cases and shared identity, persistence, directory, and delivery contracts.
- `Sidequest.Infrastructure`: SQL Server mapping/migrations and external adapters.
- `Sidequest.Web`: per-page Interactive Server, authenticated UI, and composition.

Application persistence uses explicit EF Core query/transaction abstractions through
`ISidequestDbContext`, not a new generic repository framework. Domain has no EF/UI
dependency. Components call application services rather than database or provider SDKs.
All synchronous and asynchronous save overloads reject audit/status-history and template-revision mutation
and translate stale rowversions and duplicate keys into domain conflicts. Event
ownership never bypasses active individual membership, including for draft Events.
Mutations acquire the parent Event lock first inside an explicit Serializable
transaction; SQL-specific locking stays behind the persistence port. SQL deadlocks
surface as safe conflicts, not automatic retries of partially executed commands.
Cancelled unpublished Quests remain owner-only even after archival.
Ordinary Quest-owner authorization short-circuits the history lookup it does not
need, avoiding history-range locks during independent lifecycle writes. Moderation
and nonowner access still check retained unpublished-cancellation history.
Completion scheduling reserves its pending-work key range with write intent before
insertion in the same transaction. This avoids compatible shared-range reads turning
into competing insert conversions during concurrent Quest publication; it does not
change completion deadlines, retry user commands, or commit outside the caller.

Event publication and Active Event date edits reserve the **exact** completion key
`event.complete.v1:<EventId>:<end UTC ticks>` through `HasScheduledWorkForUpdateAsync`.
Unlike Quest's pending-prefix check, every existing exact Event key suppresses a new
intent, including Completed, DeadLetter and Superseded rows. Revisited deadlines reuse
their retained intent; changing the end creates a different immutable key, and stale
work cannot complete an extended Event. Both contracts share the Infrastructure-owned
write-intent query and existing deduplication index, with no migration or isolation
change. Publication, history, audit, outbox and scheduling still commit or roll back
together; an already-published Event still rejects another publication.

Private Quest invitations reserve their exact `(QuestId, UserId)` key through
`FindQuestInvitationForUpdateAsync` before inserting or reactivating a grant.
Authorization still requires an eligible owner with current Event membership, but
does not read unrelated invitation grants for owner-only operations or moderation.
This avoids taking shared empty invitation ranges before the write-intent reservation.
Existing Active grants are unchanged; Revoked grants reuse their row without restoring
participation. The existing unique index is reused, with no migration, added retry,
or weaker isolation. Invitation, audit, outbox and Quest updates remain atomic.

Use isolated task branches and pull requests for every change under
`vaclav-pekarek-microsoft`. Verified PRs may be merged automatically; direct main pushes
are prohibited. Shared contracts and migrations have one integration owner. See
[AGENTS.md](AGENTS.md) and handoff sections 59–60.

C# changes follow pragmatic SOLID and the engineering standards in `AGENTS.md`.
The adopted external guide and project-specific application rules are linked in
[the C# instructions](.github/instructions/csharp.instructions.md); `.editorconfig`
records the formatting baseline.
All public C# types/members require meaningful XML documentation. Builds emit XML
documentation and treat missing public comments as errors, including handwritten tests.