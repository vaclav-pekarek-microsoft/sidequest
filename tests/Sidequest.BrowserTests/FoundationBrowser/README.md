# M1 foundation browser compatibility checks

These checks exercise synthetic authentication, actual Fluent input/dialog binding,
keyboard interaction at a 360px viewport, protected-route redirects and POST logout.
They are **not release A21 coverage**: no product Event/Quest flows, offline caching,
full accessibility audit, live Entra, or production sign-in is claimed.

## Required parent-managed CI setup

1. Use the integration base containing the foundation Web implementation and its provisioning-retry fix.
2. Start the actual Web application in its documented **synthetic development**
   authentication mode, using `src/Sidequest.Web/AGENTS.md` configuration instructions and a disposable
   SQL test database. It must expose Alice/Bob/Admin/Carol at `/signin` and the
   `SYNTHETIC DEVELOPMENT` banner on `/foundation`. Do not point it at production or
   a shared development database.
3. Wait for application readiness in the CI service setup. These tests neither start
   an application/server nor create or delete its database.
4. In a clean Linux GitHub Actions job, build this project and install the matching
   bundled Playwright Chromium:

   ```powershell
   dotnet build tests/Sidequest.BrowserTests/Sidequest.BrowserTests.csproj -c Release
   pwsh tests/Sidequest.BrowserTests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium
   $env:SIDEQUEST_BASE_URL = 'http://127.0.0.1:5078'
   dotnet test tests/Sidequest.BrowserTests/Sidequest.BrowserTests.csproj -c Release --filter 'FullyQualifiedName~FoundationBrowser'
   ```

   Use the port actually assigned to the running synthetic app. GitHub supplies
   `GITHUB_ACTIONS=true`; runtime browser fixtures require it.

`SIDEQUEST_BASE_URL` is mandatory and must be a root HTTP(S) URL on localhost or a
literal loopback IP, without credentials/query/fragment. HTTPS requires a trusted
certificate; tests do not disable certificate validation. Each scenario uses a fresh
browser context. Off-origin HTTP requests are aborted; no Entra/CDN fallback is allowed.
Missing configuration, an unavailable app, a missing browser, or failed assertions
produce failures, not skips.

**Do not run/install browsers locally to bypass the managed Edge sign-in policy.**
Local compilation and pure URL-guard tests are safe and do not start Playwright:

```powershell
dotnet test tests/Sidequest.BrowserTests/Sidequest.BrowserTests.csproj --filter 'FullyQualifiedName~SyntheticAppSettingsTests'
dotnet test tests/Sidequest.BrowserTests/Sidequest.BrowserTests.csproj --list-tests
```

Browser runtime evidence is pending the parent CI job; a successful build or discovery
is not proof that these browser scenarios passed.
