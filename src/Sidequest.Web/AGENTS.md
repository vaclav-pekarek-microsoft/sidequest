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
Bind Fluent input components' `Disabled` parameters explicitly as well as their
native fieldset. Fieldset-only transitions can leave the web component and its
shadow input disabled after the fieldset becomes enabled.

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
  Authentication changes clear/block device state before submission; storage failure
  must remain visible without preventing sign-out.
- Before enabling a surviving circuit, the session check must match the current
  HTTP cookie to a server-protected proof of that circuit's tenant/object identity,
  unique sign-in instance and deadline. A different authenticated cookie is not a
  successful reconnect. Keep this proof in bridge memory and the no-store check
  header, never offline storage; it cannot authenticate independently. Older
  tickets without a sign-in identifier require fresh sign-in.
- Administration navigation is available to authenticated users, but each screen
  and operation rechecks the persisted administrator assignment. Neither a visible
  link nor an Entra admission role grants administration or private resource access.

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

Optionally configure both `Authentication:BootstrapAdministrator:TenantId` and
`:ObjectId`. Only that identity receives an administrator row when first provisioned.
Bootstrap does not confer Event membership or ownership, and never restores a removed
administrator on later login. For an already-provisioned account, use the separately
authorized database administration process. No first-user-wins behavior.

Sign-in provisions/updates `(TenantId,ObjectId)` in a serializable transaction, retrying
duplicate-key/deadlock/concurrency conflicts at most twice with fresh contexts.
This includes `DomainException(Conflict)` translated by the persistence boundary;
Forbidden/Validation outcomes and ineligible accounts are not retried.
The integration-owned schema must enforce that unique key. Disabled or verified-departed
users remain disabled. Request cookies revalidate SQL eligibility on every request;
circuits revalidate every minute and fail closed on errors. Resource commands still
recheck persisted access independently. Cookies/circuits last at most one hour without
sliding renewal; Entra assignment changes require fresh sign-in to fetch fresh role claims.
Immediate workforce departure enforcement uses the persisted eligibility/departure fields.
Persist/protect Data Protection keys for hosting; review affinity/shared keys before scale-out.
No automatic database creation/migration runs on startup.

## Local synthetic development
`appsettings.Development.json` explicitly selects `Authentication:Mode=Development`
and `(localdb)\MSSQLLocalDB`, database `SidequestDevelopment`, integrated authentication.
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
Full-product browser acceptance and real-Entra release verification remain open.
`/health/live` is anonymous process liveness; `/health/ready` returns SQL availability
only, not migration readiness. Both expose status only. HTTP exception responses use
a safe HTML page or generic problem details with correlation IDs; never display exception
messages or secrets.

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
M3 dashboard, cover UI, authentication/reconnect and bounded offline composition remain
under combined browser acceptance. This is not live-provider or device-policy approval.
Do not introduce fake success adapters to satisfy external contracts.
