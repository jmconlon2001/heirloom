# Progress

## Phase 0: Foundation ✅ (2026-11-25, awaiting review)

**Done criterion met:** `docker compose up` gives a working magic-link login via Mailpit behind a reverse proxy at a sub-domain. `scripts/smoke-proxy.sh` proves it end to end through a local Caddy at `https://memories.localhost:8443`: the link uses `PUBLIC_BASE_URL`, cookies are Secure because the forwarded proto is trusted, and the session is remembered for 180 days. A manual check also confirmed the session survives an app container restart, because Data Protection keys are persisted.

### Built
- **Solution:** `src/App` (Razor Pages, .NET 10), `src/App.Tests` (xUnit v3 + Testcontainers PostgreSQL 17), `src/App.E2E` (Playwright for .NET + axe). Central package pinning, warnings as errors, `dotnet-ef` local tool.
- **Data:** EF Core + Npgsql, migration `InitialIdentity`, auto-applied at startup after a `pg_dump -Fc` safety backup whenever the database already has data (D-011). Seeds the roles (Admin, Storyteller, Family) and the bootstrap admin from `ADMIN_EMAIL`.
- **Auth:** ASP.NET Core Identity, passwordless.
  - Magic links: single-use, 14 days, stored hashed, atomic redemption, safe against email scanners (D-002, D-003).
  - No user enumeration; per-user and per-IP rate limits.
  - Remembered devices: 180 days, sliding, and revocable per device (D-004).
- **Platform:**
  - Data Protection keys in `/data/keys`.
  - Serilog JSON to the console plus a daily rolling file in `/data/logs`.
  - `/healthz` and `/readyz` (database + Gotenberg).
  - Forwarded headers limited to `TRUSTED_PROXIES`.
  - Security headers: strict CSP with no inline scripts or styles, nosniff, DENY framing.
  - Antiforgery on every form; friendly error pages with no codes shown.
- **UX base:**
  - Layout: 20px base text, A / A+ / A++ (20/24/28px) and High contrast. These work without JavaScript, persist in a cookie, and render server-side with no flash (D-006).
  - "Need help?" box on every page; 48px+ tap targets; skip link.
  - Plain-language sign-in, check-email, link-used/expired and sign-out pages.
- **Deploy:**
  - Dockerfile: noble runtime + `postgresql-client-17`, runs as `PUID`/`PGID`, healthcheck.
  - `compose.yml`: app, `postgres:17.12-trixie`, `gotenberg:8.40.0` (internal network, 1GB limit), `mailpit` (dev profile), `caddy` (proxy profile). `.env.example` is fully commented.
- **CI:** `.github/workflows/ci.yml` (build, integration tests, e2e with browser deps, image build); `release.yml` (pushes to GHCR on `v*.*.*` tags).
- **Docs:** `README.md`, `docs/CADDY.md`, `docs/DECISIONS.md` (D-001 to D-014).

### Tests: 48 passing (`dotnet test`)
- 44 unit and integration tests:
  - Options parsing and display prefs.
  - Magic-link service: hashed storage, single use, concurrent redemption race, expiry, lockout, per-user limit.
  - HTTP sign-in flow: return-URL safety, no enumeration, antiforgery, device revocation on cookie replay, seeding.
  - Platform: health, CSP/headers, no inline script/style, Need help, no-JS text size, friendly 404, trusted vs untrusted forwarded headers, rate limit.
- 4 e2e tests:
  - iPad Pro 11 (WebKit): full magic-link login with axe on every step, and A++ with high contrast, remembered across reload. Both check tap targets ≥ 48px and no sideways scroll.
  - Chromium: 640px wide (200% zoom) at A++.
  - Text size with JavaScript off.
  - Zero serious/critical axe violations. The helper also asserts that axe actually ran.

### Left for later phases / not in Phase 0
- Optional password and passkey sign-in (Identity is ready for it; there's no UI yet).
- Admin UI for devices ("sign out Grandma's iPad"), users, and invites: Phase 1.
- Device-enrollment QR login cards: `MagicLinkPurpose.DeviceEnrollment` exists, but there's no flow yet (Phase 1).
- Hangfire (no jobs yet; D-013); `ffmpeg`, `pandoc`, `restic` in the image: added in the phases that use them.
- Deployment docs `DEPLOY_OMV.md`, `DEPLOY_PVE.md`, `RESTORE.md`, `ADMIN_GUIDE.md`, `HELP_FOR_STORYTELLERS.pdf`.
- Embedded web font (Atkinson Hyperlegible, OFL). The CSS already names it first and falls back to system fonts; vendor it in Phase 1.

### Known issues / notes
- The local Docker setup (colima) has no buildx, so the Dockerfile avoids BuildKit-only syntax. CI uses buildx and builds `linux/amd64`.
- On a fresh database, EF logs one "Failed executing DbCommand" error while checking for the migrations table that doesn't exist yet. This is expected and harmless; it appears only on first start.
- On macOS bind mounts (colima/virtiofs), files under `.data/` show as root-owned from inside the container. On Linux hosts (OMV/Proxmox) ownership follows `PUID`/`PGID`.
- The local `.env` (gitignored) is configured for the dev proxy profile with `admin@example.test`. The stack is still running (`docker compose --profile dev --profile proxy down` to stop it).
- The repo has been `git init`ed on `main`, with no commits yet.
