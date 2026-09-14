# Sidequest

An internal, Quest-first event activity organizer. The accepted V1 specification and
decision log are in [the project handoff](docs/sidequest-project-handoff.md).

Implementation is in progress. The current milestone establishes shared contracts,
SQL persistence, Entra/development authentication, a Fluent UI shell, and CI. It is
not a production-ready release; live tenant, email, hosting, and data-policy approval
gates remain open.

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

Development sign-in uses conspicuously labeled synthetic accounts only when the
Development environment and explicit development authentication mode are both active.
It is not proof that live Entra integration is configured. Production must use Entra,
an approved workforce admission policy, and an explicitly configured bootstrap
administrator; there is no "first user becomes admin" behavior.
See `src\Sidequest.Web\AGENTS.md` for authentication/rendering configuration.

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
project. Do not bypass managed local browser policy to run these checks. M1 browser
compatibility scenarios are not substitutes for later full Event/Quest journeys.
Foundation CI requires nonempty unit, SQL integration, and browser results with
every discovered scenario executed and passed; skipped suites do not satisfy the gate.

## Architecture and contribution policy

- `Sidequest.Domain`: entity/state vocabulary and pure access, participation, and time rules.
- `Sidequest.Application`: use cases and shared identity, persistence, directory, and delivery contracts.
- `Sidequest.Infrastructure`: SQL Server mapping/migrations and external adapters.
- `Sidequest.Web`: per-page Interactive Server, authenticated UI, and composition.

Application persistence uses explicit EF Core query/transaction abstractions through
`ISidequestDbContext`, not a new generic repository framework. Domain has no EF/UI
dependency. Components call application services rather than database or provider SDKs.
All synchronous and asynchronous save overloads reject audit/status-history mutation
and translate stale rowversions and duplicate keys into domain conflicts. Event
ownership never bypasses active individual membership, including for draft Events.

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