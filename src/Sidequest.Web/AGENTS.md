# Sidequest.Web

| Setting | Value |
|---------|-------|
| **Interactivity Mode** | Server |
| **Interactivity Scope** | Per-page |

## Rendering configuration
Based on the installed create-blazor-project `server-per-page.md` template.
The initial scaffold used `dotnet new blazor -int Server -au Individual`; generated
Identity account pages, SQLite and samples have been removed in favor of Entra.
Pages use static SSR by default. Only components with `@rendermode InteractiveServer`
become interactive, with prerendering. Never make `Routes` globally interactive.
Keep actions and form controls disabled until `RendererInfo.IsInteractive` is true.
Prerendered HTML has no event handlers; an enabled-looking button can otherwise lose
an early click. Preserve prerendering and server reauthorization rather than adding
arbitrary client delays or retrying mutations to hide this handoff.
Query-dependent Quest routes bind query values in static `*Route` components and
pass serializable parameters explicitly to their interactive views. An enhanced
navigation can finish after `StartCircuit` captures the previous URL but before
the interactive renderer attaches; that circuit can miss the navigation and
supply obsolete query values. Do not move query binding into those interactive
roots or parse their `NavigationManager.Uri` as a fallback. Keep route authorization,
prerendering, service reauthorization and explicit parameter transfer together.
The browser regression holds the unchanged source-URL startup frame until the
target SSR page arrives, then verifies the enabled interactive moderation view.
Bind Fluent input components' `Disabled` parameters explicitly as well as their
native fieldset. Fieldset-only transitions can leave the web component and its
shadow input disabled after the fieldset becomes enabled.
Quest management reasons bind on input with no debounce, before confirmation can
dispatch a command. Do not depend solely on a later Fluent blur/change event to
capture required reasons; server validation still applies.
The detail page owns its unsent management draft separately from its authorized
projection. Revalidation hides/removes controls and waits for pending commands,
but preserves the reason, selected person and original version in circuit memory.
Confirmed revocation, navigation, explicit reload and successful commands clear
the draft; validation failures retain it without restoring confirmation. An
indeterminate revalidation failure hides the projection and retains the draft
until access is verified again. Only actual retained management input (a reason
or selected person) owns a stale-intent version fence; empty drafts and ordinary
viewers adopt newly authorized versions without blocking participation.
Ownership loss discards management intent without fencing remaining ordinary
access. Retained-intent version changes require explicit reload even after an
indeterminate failure.
Route ownership and authorization generations are separate. Every disconnect
invalidates the displayed projection; overlapping reconnect reports await the
newest serialized read-only check, never a mutation replay. Detail, history and
member queries must all finish for that generation before projection publication.
Captured participation, management, paging, reload and draft callbacks are scoped
to their originating route and authorization generation. Late mutation completion
cannot refresh or clear a replacement Quest's draft. The coordinator readiness
contract is unchanged. The controlled bUnit regression includes an authorization read
that yields while a queued callback causes an intervening render; a purely
synchronous read alone did not reproduce that loss. This does not establish the
cause of the separate intermittent Quest-creation text-field loss; bounded
failure-only browser diagnostics distinguish retained elements from replacement
and compare native, host and attribute value lengths without recording contents.
Browser input automation must respect the bridge's inherited `inert` gate as
well as disabled/readonly controls. Playwright `FillAsync` and `ToBeEditableAsync`
do not establish that an element can receive events: keyboard-filled text can
remain empty inside an inert ancestor while native date assignment still succeeds.
Use the shared browser-test `FillWhenActionableAsync` helper for ordinary input;
its trial click checks actionability without clicking, forcing, changing the
application gate, or replaying a submission. Direct `FillAsync` is reserved for
the helper implementation and the standalone synthetic negative controls.

## Adding components and data access
- Routable pages live in `Components\Pages`; shared UI in `Components`.
- Keep read-only pages and sign-in entry static. Use per-page interactivity for forms/dialogs.
- Components call application services, **never DbContext or Graph SDK**. No HTTP layer
  is required between server components and application services.
- Application operations use short-lived `ISidequestDbContextFactory` contexts and
  `IResourceAccess`; hiding UI is not authorization. Entra roles grant admission only.
- Never use HttpContext or SignInManager inside an interactive circuit. `ICurrentUser`
  uses AuthenticationStateProvider. HttpContext is available only during static SSR/HTTP.
- Use Fluent providers inside an interactive subtree when needed. App-owned interop
  is limited to the Experience connection/authentication bridge and standalone offline
  modules; Fluent supplies its own web-component modules.
- Keep one global connection bridge. Existing views use the scoped
  `ExperienceCoordinator` and `ExperienceViewSubscription` to disable offline actions,
  reauthorize protected projections and defer Joined-snapshot refreshes until rendering.
  Reconnects must preserve unsaved input, never replay mutations, and retain explicit
  reload after version conflicts. Event pages retain their scoped circuit revalidation.
  Administration and notification bases also await pending operations and reauthorize
  retained views before making them usable. Keep unverified projections hidden,
  preserve valid local drafts and original versions, and clear confirmed revoked data.
  Authentication changes clear/block device state before submission; storage failure
  must remain visible without preventing sign-out.
  Native authentication forms can submit before the App initializer exists. A delayed
  sign-in completion must check its protected sign-in-instance proof against current
  HTTP cookies before activating the matching device generation; the generation alone
  cannot detect an unintercepted native logout. Missing/denied checks stay visibly
  blocked with explicit continuation, never automatic navigation or activation.
- Before enabling a surviving circuit, the session check must match the current
  HTTP cookie to a server-protected proof of that circuit's tenant/object identity,
  unique sign-in instance and deadline. A different authenticated cookie is not a
  successful reconnect. Keep this proof in bridge memory and the no-store check
  header (or transient no-store sign-in completion HTML), never offline storage; it
  cannot authenticate independently. Older
  tickets without a sign-in identifier require fresh sign-in.
- The earliest scoped circuit handler invalidates online readiness on transport
  down/up without invoking view callbacks or JavaScript. Revalidation during
  `ConnectCircuit` must not await snapshot interop before the reconnect handshake
  returns. Only the cookie-verified browser bridge may restore readiness.
- Administration navigation is available to authenticated users, but each screen
  and operation rechecks the persisted administrator assignment. Neither a visible
  link nor an Entra admission role grants administration or private resource access.
- Do not instantiate `InputFile` for an unsaved Quest; use an inert disabled input
  until a persisted draft can accept uploads. Retain cancellation tokens across
  awaits and reject late query results before starting dependent work after disposal.

## Production / real Entra configuration
Default mode is Entra; invalid or absent configuration fails startup. There is no fallback.
Set `Authentication:Mode=Entra`, `AzureAd:TenantId` and `AzureAd:ClientId` to real,
nonempty tenant-specific GUIDs using deployment configuration (not source control).
Set `AzureAd:ClientSecret` through user secrets locally or a Key Vault-backed deployment
secret store. Configure `ConnectionStrings:Sidequest` for SQL Server/Azure SQL and
`AllowedHosts` for the deployment hostname. Use HTTPS and registered `/signin-oidc`
and `/signout-callback-oidc` redirect URIs. Issuer/audience validation is Microsoft.Identity.Web's.
When using real Entra in the Development environment, also override both synthetic
bootstrap administrator settings with the approved real pair, or set both to empty.

Create a **single-tenant workforce** Entra app registration and an explicit app role.
Set `Authentication:WorkforceRole` to its exact role value, emitted as an ID-token
`roles` claim; `tid` and `oid` claims are required. Require enterprise-application
assignment and approve a workforce-only assignment process/policy that excludes
guests/external identities. **A tenant ID, email domain or role name alone does not
prove employment.** A guest mistakenly assigned this role is a tenant policy failure.
Entra owners must verify guest exclusion and role issuance with live accounts before release.
No Graph call, email check or group membership grants application permissions.
Keep both the OpenID Connect option and its actual configured JSON token handler's
inbound claim mapping disabled. Microsoft.Identity.Web replaces the framework
handler; setting only `MapInboundClaims` on the options does not update that
replacement. Admission requires the validated raw `tid`, `oid` and `roles` names.
Exercise the configured handler with signed tokens, not only hand-built principals.

The explicitly approved D41 hackathon profile selects
`Authentication:AdmissionPolicy=hackathon-assigned-users` only in Staging or real-Entra
Development. It requires both the dedicated participant role and immutable configured
object-ID allowlist. The same list governs enabled Member/Guest directory eligibility.
Keep enterprise-app assignment and the list synchronized; removal must revoke local
eligibility, not merely wait for role claims to expire. Production/default workforce
rules above are unchanged. Follow `infra\budget-staging\APPLICATION.md` for the exact
initial owner, permissions, credential and deployment gates.

Optionally configure both `Authentication:BootstrapAdministrator:TenantId` and
`:ObjectId`. Only that identity receives an administrator row when first provisioned.
Bootstrap does not confer Event membership or ownership, and never restores a removed
administrator on later login. For an already-provisioned account, use the separately
authorized database administration process. No first-user-wins behavior.

Sign-in provisions/updates `(TenantId,ObjectId)` in a serializable transaction, retrying
duplicate-key/deadlock/concurrency conflicts at most twice with fresh contexts.
This includes `DomainException(Conflict)` translated by the persistence boundary;
Forbidden/Validation outcomes and ineligible accounts are not retried.
After trusted admission, `FindUserForUpdateAsync` performs the first account lookup
with an Infrastructure-owned `UPDLOCK,HOLDLOCK` reservation using the existing unique
tenant/object index. It returns current tracked eligibility and rowversion, protecting
both absent-key inserts and existing-account updates before shared locks can convert.
Adjacent empty ranges may wait; unrelated existing identities are not globally locked.
No schema migration is required, and the reservation neither admits users nor grants roles.
The integration-owned schema must enforce that unique key. Disabled or verified-departed
users remain disabled. Request cookies revalidate SQL eligibility on every request;
circuits revalidate every minute and fail closed on errors. Resource commands still
recheck persisted access independently. Cookies/circuits last at most one hour without
sliding renewal; Entra assignment changes require fresh sign-in to fetch fresh role claims.
Immediate workforce departure enforcement uses the persisted eligibility/departure fields.
Persist/protect Data Protection keys for hosting; review affinity/shared keys before scale-out.
`AddSidequestAzureHosting` is explicitly enabled by `Hosting:Azure:Enabled`; it rejects
Development/synthetic authentication. The Hosting folder owns stable application-name
validation, private native Blob/Key Vault endpoints, and the versionless wrapping key.
The system-assigned identity persists and wraps the key ring; failures must not fall
back to local/plaintext keys. Metrics-only export uses named Azure Monitor options,
managed identity and no local spool or automatic trace/log exporter. Require the
Statsbeat/customer SDK statistics opt-outs in the actual process environment, not
just IConfiguration, before constructing the exporter. Keep telemetry
privacy and actual cloud permissions/ingestion as separate release gates.
No automatic database creation/migration runs on startup.

## Local synthetic development
`appsettings.Development.example.json` explicitly selects `Authentication:Mode=Development`
and `(localdb)\MSSQLLocalDB`, database `SidequestDevelopment`, integrated authentication.
The build copies this template to an ignored `appsettings.Development.json` only when
that local file is absent; existing settings are never overwritten. Neither file
is published. A real local Entra configuration may instead target the approved
hackathon staging SQL database with encrypted Azure-credential authentication.
That shared database is not a test fixture; keep all automated tests isolated.
This mode additionally requires environment **Development** and loopback connections
(including circuit traffic). Do not proxy it publicly or enable forwarded headers for it.
No synthetic login endpoint is mapped in Entra/Production. Cookies are mode-isolated.
HTTP localhost is supported for this mode; production cookies always require HTTPS.

Synthetic tenant: `11111111-1111-4111-8111-111111111111`.

| Persona | Synthetic object ID | Contact |
|---------|---------------------|---------|
| Admin | `22222222-2222-4222-8222-222222222221` | `admin@sample.invalid` |
| Alice | `22222222-2222-4222-8222-222222222222` | `alice@sample.invalid` |
| Bob | `22222222-2222-4222-8222-222222222223` | `bob@sample.invalid` |
| Carol | `22222222-2222-4222-8222-222222222224` | `carol@sample.invalid` |

Development settings explicitly configure the fixed Admin tenant/object pair; without
that configuration nobody is bootstrapped. Only this Admin object is accepted in
development bootstrap configuration. It is bootstrapped at first provisioning. Persona sign-in
and logout are antiforgery-protected POSTs. Return URLs are local-only.
Apply integration-owner SQL migrations before signing in; only the anonymous home
page works without SQL. The authenticated dashboard performs authorized SQL queries.
Synthetic sign-in is not evidence of live Entra correctness.

## Compatibility / operations
Author UI styles in `.scss` and `.razor.scss`, not inline attributes or generated CSS.
The Web build compiles Sass before Razor CSS isolation/static asset discovery.
Global theme tokens live in `wwwroot\_tokens.scss`; generated CSS is ignored by Git.
Home defaults to the authorized all-Quest board, Joined first and then Event-local
civil start time (UTC instant/ID ties). Reuse existing membership/private/draft
authorization and reconnect handling. Full cards remain page-bounded.
Compatibility and Switch account links are intentionally absent from navigation;
the direct diagnostic route and protected sign-out still exist.
Keep a single Quests navigation link before Events. Direct Entra links must use
the shared `SignInLink` and retain the `data-authentication-change` handshake;
synthetic mode alone uses the persona picker. Completion hides its continuation
while verification is pending, then reveals it on a missing/failed/superseded
binding rather than bypassing the device-generation boundary. The one connection
bridge lives in the footer's collapsed device tools. Hide only healthy connection
feedback, never its authorization/inert gates or failure notices. Global `[hidden]`
styling must win over component/button display rules.
Box form sections and separate compact filters from page actions. New Event
create/update validation requires EndDate > StartDate; leave inclusive legacy
`TimeRules.EventWindow` reads intact. Use the bundled zone selector and inherited
Quest zone, with contextual first/second-occurrence choices for ambiguous local
times, never free-form numeric offsets or silent DST resolution. Display contact
labels only within existing authorization; never user GUIDs or newly exposed
attendee email rosters.

`/foundation` is an authenticated compatibility screen (not product logic): Fluent
4.14.4 with .NET 10, text-field binding, EditForm/DataAnnotations errors, modal dialog
and close feedback. No database writes occur from this screen. Fluent is MIT licensed;
Microsoft.Identity.Web 4.14.2 and EF Core 10.0.12 are MIT licensed. bUnit verifies
empty/overlong validation, 1/80-character binding and opening/closing the real Fluent
components. Live local HTTP verification also exercised SQL-backed Alice sign-in,
authenticated SSR, antiforgery rejection and logout. Actual Edge keyboard/mobile
verification was blocked by the managed browser's forced-sign-in policy; no policy
bypass was attempted. Subsequent isolated Linux Chromium CI
[34861029210](https://github.com/vaclav-pekarek-microsoft/sidequest/actions/runs/34861029210)
passed all seven M1 browser journeys: four persona sign-ins with exact Fluent dialog
content, unauthenticated redirect, 360px keyboard/no-overflow interaction, and logout.
These historical compatibility checks do not establish full release acceptance
or real-Entra verification.
`/health/live` is anonymous process liveness; `/health/ready` checks SQL access and
ordered applied migration history against the nonempty compiled migration set.
Missing, pending or unknown migrations are unready; this does not detect manual
schema damage hidden behind intact history. Always register operational persistence
adapters, even when optional monitoring is disabled. Web health/sampling depends on
Application operational ports; SQL commands, migration metadata and provider failure
classification belong in Infrastructure. No startup migrations or provider probes
are permitted. Both health endpoints expose status only. HTTP exception responses use
a safe HTML page or generic problem details with correlation IDs; never display exception
messages or secrets.

`Operations:Monitoring:Enabled` explicitly enables sequential queue sampling, using
a fresh scope/context per attempt. The interval defaults to 30 seconds (5–300 seconds);
an observation becomes stale only after twice the interval. Export only allowlisted aggregate
queue gauges, fixed port-operation outcomes/durations and HTTP outcome counts from
`Sidequest.Operations`, never payloads, identifiers, URLs or exception content. Interpret
backlogs only with availability/staleness: failures and stale/missing samples are
not healthy zero queues. Queue age does not measure delivery latency or reminder
business deadlines. Activity durations include internal provider retries and are not
end-to-end command latency. Resource access observation wraps only the default scoped
implementation, never replaces custom authorization, and never changes its result,
exception, cancellation or transaction ownership. Register monitoring after Application
composition; provider observers are chosen lazily during host resolution. HTTP outcome
counts include health/static traffic and do not represent Blazor circuit commands.
See `Operations\README.md` for metric semantics and response procedures, and
`infra\README.md` for deployment boundaries.

From the repository root:
```
dotnet build src\Sidequest.Web\Sidequest.Web.csproj
dotnet test tests\Sidequest.UnitTests\Sidequest.UnitTests.csproj --filter FullyQualifiedName~FoundationWeb
dotnet run --project src\Sidequest.Web --launch-profile http
```

Graph, email, Event/Quest workflows and durable delivery now have real implementations.
The host verifies handler completeness before starting its SQL worker; Graph policy and
provider credentials remain external configuration/approval gates. M2 combined acceptance
passed in Linux CI34896985551, including actual production composition, real SQL workflows,
and authenticated Chromium journeys. Media and Administration backends are integrated;
M3 dashboard, cover UI, authentication/reconnect and bounded offline composition passed
[combined Linux acceptance 34967281852](https://github.com/vaclav-pekarek-microsoft/sidequest/actions/runs/34967281852):
1,845 unit, 1,019 real-SQL, 96 browser-project cases and 54 Node regressions.
Ordinary browser sign-in helpers observe initialized connection UI; a separate
controlled native-startup journey verifies missing-generation guidance and explicit
continuation. Route barriers must resolve fingerprinted assets through the rendered
import map and use Playwright-compatible regular-expression options.
Moderation navigation verifies the actual selected View and Event, not only the
URL. Its failure diagnostics report bounded state categories and booleans, never
titles, resource identities, authentication proofs or raw alert content; do not
replace missing-state evidence with automatic retries or relaxed access checks.
This is not live-provider, device-policy, load/restore or production release approval.
Do not introduce fake success adapters to satisfy external contracts.
