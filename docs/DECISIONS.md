# Decisions

Short ADR-style log of non-obvious choices. Newest at the bottom.

---

### D-001 · .NET 10 LTS packages, not .NET 11
**Context:** .NET 11 (STS) packages are out, but the stack pins .NET 10 LTS.
**Decision:** All Microsoft/Npgsql packages are pinned to the latest 10.x (`Directory.Packages.props`, central package management). `global.json` pins SDK 10.0.400+ (latestFeature).
**Consequence:** Upgrade deliberately, with a decision entry, when .NET 12 LTS ships.

### D-002 · Magic-link GET shows a button; only POST signs in
**Context:** Outlook Safe Links, Gmail and corporate scanners open every link in an email. A single-use link consumed on GET would be burned before Grandma taps it.
**Decision:** `GET /account/magic-link` only checks the token and shows one big **Sign me in** button; the token is used up by the POST (antiforgery-protected).
**Consequence:** One extra tap. It's a large, single, obvious button, and it means links reliably work.

### D-003 · Tokens are random and stored hashed, not signed
**Context:** The spec says "signed and expiring tokens stored hashed".
**Decision:** Magic-link tokens are 256 random bits (base64url) sent only in the email; the DB stores SHA-256(token), expiry, purpose and consumed-at. Redemption is an atomic `UPDATE … WHERE ConsumedAt IS NULL`, so exactly one concurrent caller wins.
**Why not signed:** A server-side row is needed anyway for single-use and revocation. With a 256-bit random secret, a signature adds nothing; a hash means a database leak can't be replayed.

### D-004 · Remembered devices are rows, and the 180 days slide
**Decision:** Each magic-link sign-in creates a `UserDevice` row; the persistent auth cookie (`fm_auth`, 180 days, sliding) carries its id. Every request checks the device isn't revoked, expired, or the user locked out (one indexed query). `LastSeenAt`/`ExpiresAt` are bumped at most hourly.
**Consequence:** "Sign out" (and, in Phase 1, admin "sign out this iPad") revokes exactly one browser. A device used at least every 180 days never has to log in again, which is what the users need. Identity's security-stamp refresh is told to keep the device claim.

### D-005 · No user enumeration on the login page
**Decision:** Known and unknown addresses get the same "Check your email" page. Per-user limit of 5 login links/hour, plus a per-IP limit on sign-in form posts (`AUTH_RATE_LIMIT`, default 10 per 10 minutes). Reading pages is never rate limited.

### D-006 · Accessibility prefs live in a cookie, rendered server-side
**Context:** A/A+/A++ and high contrast must persist and apply before sign-in, with no flash of small text, and with a CSP that forbids inline scripts.
**Decision:** Cookie `fm_display` (`s{0-2}.c{0|1}`, not HttpOnly, 5 years). The layout writes the classes on `<html>`. The buttons are a real form posting to `/display` (works without JavaScript); `display.js` intercepts and applies instantly. Sizes are 125/150/175% of the browser default (20/24/28px), so browser zoom and iOS text settings still work. `prefers-contrast: more` also strengthens colours.

### D-007 · Flat UPPER_SNAKE env vars, read once into `AppOptions`
**Decision:** All settings use the `.env` names directly (`PUBLIC_BASE_URL`, `TRUSTED_PROXIES`, `POSTGRES_*` …) rather than ASP.NET's `Section__Key` style, so one `.env` serves both Compose and the app. `ConnectionStrings:Default` overrides `POSTGRES_*` (used by tests).

### D-008 · Forwarded headers: For + Proto only, trusted networks only
**Decision:** `X-Forwarded-For` and `X-Forwarded-Proto` are honoured only from `TRUSTED_PROXIES` (IPs/CIDRs). `X-Forwarded-Host` is ignored; absolute links always come from `PUBLIC_BASE_URL`, which also closes Host-header injection in emails. Cookies use `SecurePolicy=SameAsRequest`, so they're Secure exactly when the proxy says the request was HTTPS.

### D-009 · Ubuntu 24.04 (noble) runtime + PGDG apt repo for pg_dump 17
**Context:** .NET 10 images are Ubuntu-based (no GA Debian 13 image). Ubuntu ships `postgresql-client-16`, which refuses to dump a 17 server.
**Decision:** Runtime image `aspnet:10.0.15-noble`, adding `postgresql-client-17` from apt.postgresql.org (signed-by key). The build is framework-dependent and RID-less, so it builds on amd64 and arm64, with or without BuildKit.

### D-010 · Container starts as root only to chown, then drops to PUID/PGID
**Decision:** `docker/entrypoint.sh` creates `/data/*`, fixes ownership of anything not already owned by `PUID:PGID` (OMV/linuxserver convention), then `exec setpriv --reuid --regid --clear-groups --inh-caps=-all`. The app process never runs as root.

### D-011 · Pre-migration backup is a plain pg_dump until Phase 2
**Decision:** At startup, if migrations are pending *and* the DB already has migrations applied, the app runs `pg_dump -Fc` to `/data/backups/pre-migration/` and refuses to migrate if that fails. `PRE_MIGRATION_BACKUP=false` turns it off (local dev without pg_dump only). Phase 2 swaps this for the restic pipeline.

### D-012 · Extra test dependencies
| Package | Why | License |
|---|---|---|
| `Deque.AxeCore.Playwright` | Runs axe-core inside Playwright pages (the spec's "axe-core accessibility checks in e2e") | MPL-2.0 |
| `Microsoft.AspNetCore.Mvc.Testing` | `WebApplicationFactory` for in-process integration tests, and its .NET 10 `UseKestrel()` for e2e | MIT |
| `Testcontainers.PostgreSql` | Real PostgreSQL 17 in tests (spec'd) | MIT |
Test runner is xUnit v3 on Microsoft.Testing.Platform (the .NET 10 SDK default; opted in via `global.json`), so `Microsoft.NET.Test.Sdk`/VSTest are not used.

### D-013 · Email sends inline until Hangfire arrives
**Decision:** Phase 0 sends login emails synchronously via MailKit and logs failures; the user still sees "Check your email". Phase 4 moves all email to Hangfire with retries and `EmailLog`. Hangfire itself is not wired up yet because nothing in Phase 0 needs a background job.

### D-014 · Local dev reverse proxy in Compose
**Decision:** A `caddy` service in the `proxy` profile serves `https://memories.localhost:8443` (Caddy internal CA) and forwards to the app exactly like the OPNsense Caddy will. `scripts/smoke-proxy.sh` proves the Phase 0 "done" criterion against it. The frontend network has a fixed subnet (`FRONTEND_SUBNET`) so `TRUSTED_PROXIES` can name it.
