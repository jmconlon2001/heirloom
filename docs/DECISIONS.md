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

---
## Phase 1

### D-015 · Hangfire runs media processing; inline when jobs are off
**Decision:** Upload completion enqueues `MediaProcessor.ProcessAsync` on Hangfire (PostgreSQL storage, schema `hangfire`, long-polling so jobs start within a second). `JOBS_ENABLED=false` swaps in `InlineMediaJobs`, which processes immediately. Tests use that for determinism, and one test class (`HangfireTests`) exercises the real Hangfire path. The dashboard is at `/admin/jobs`, admin-only. It ships inline scripts, so it alone gets `'unsafe-inline'` in its CSP; every other page keeps the strict policy.

### D-016 · Pin Newtonsoft.Json 13.0.4 (MIT)
**Context:** Hangfire.Core depends on Newtonsoft.Json ≥ 11.0.1, which NuGet resolves to a version with a known high-severity advisory (GHSA-5crp-9r3c-p9vr).
**Decision:** Reference Newtonsoft.Json 13.0.4 directly so the patched version is used. It isn't called by our code.

### D-017 · Prompt seeding by stable key; built-in sets follow the list
**Decision:** `seed/prompts.json` is upserted at startup by `key`. Seed text changes flow through until an admin edits that prompt (`EditedByAdmin`); after that, the admin's wording wins. "Core 26" and "Full 52" are not stored lists. They are live views ("all active core prompts", "all active prompts") in chapter order, so a new or turned-off question updates them automatically. Only custom sets store their items.

### D-018 · Version history: every save kept, autosaves coalesced
**Decision:**
- Each **Save**, **Finish** and **Restore** keeps a `MemoryVersion`.
- Autosaves (every 5 s) by the same person within 30 minutes update one "saved automatically" version rather than adding hundreds.
- A save whose content is identical to the latest version adds nothing.
- Restoring copies an old version into a *new* version, so the words it replaces are still in history.

### D-019 · `Memory.Revision` protects the words, not the attachments
**Decision:** Autosave sends the revision it last saw. A mismatch (an old tab or another device) returns 409 instead of silently overwriting, and the page keeps the words in `localStorage` and asks for a reload. Adding photos or captions doesn't bump the revision. An earlier build did, and every photo upload caused a false "changed in another window" warning; the e2e test now covers that case.

### D-020 · Our own small chunked-upload protocol
**Decision:** `POST /api/memories/{id}/uploads` → `PUT /api/uploads/{id}?offset=N` (4 MB chunks) → `POST …/complete`. A 409 tells the client how many bytes the server has, so it resumes after a dropped connection (exponential back-off, up to 30 s). The type is checked by **magic bytes** on the first chunk, together with the per-kind size limit, so a 1 GB upload of the wrong kind is refused at 4 MB. tus was considered, but it would need a server library and a JS client, which is more than this needs. Without JavaScript, the same `AttachmentService` handles a plain multipart form post. Stale `.part` files are cleaned up daily.

### D-021 · Derivatives drop *all* metadata; originals keep everything
**Decision:** The 2048 px and 400 px JPEGs are EXIF-rotated, then `Strip()`ped. This removes GPS, and also camera data, which the web copies don't need. Originals are stored byte-for-byte (SHA-256 recorded) and are never served inline except to people who can see the story. "Remove" is a soft delete with "Put it back".

### D-022 · `.mp4`/`.webm` are classified by ffprobe, not by name
**Context:** Safari records voice as audio-only MP4, Chrome as WebM; both brands also hold video.
**Decision:** Browser recordings are flagged as voice and always processed as audio. Other MP4/WebM/MOV uploads are probed, and if there is no real video stream (cover art doesn't count) they become audio. All audio is normalised to AAC `.m4a` (voice mono 96 kbps). Video keeps its original file for playback and gets a poster frame. Transcoding video for older Safari is not in v1.

### D-023 · Proxy writing = admins + named helpers
**Decision:** Besides admins, an admin can name **helpers** for a storyteller (`StorytellerHelper`), e.g. a grandchild doing an interview. The first time someone other than the storyteller saves, `WrittenById` is set and an audit entry written. The story reads "Told by Grandma Rose · written by Lily".

### D-024 · Pacing rules
**Decision:**
- **Weekly:** prompt *n* in the set unlocks *n−1* weeks after the storyteller's start date.
- Anything already started stays open regardless of pacing.
- One-off questions are always open and listed first.
- The next suggestion is the first unlocked prompt that is unanswered or a draft; skipped prompts come back after everything else.
- Declined counts as done.
- Locked prompts show their opening date and can't be opened by URL.

### D-025 · Phase 1 visibility is "owner, helpers and admins"
**Decision:** Until sharing lands in Phase 3, `StoryAccess` lets only the storyteller, their helpers and admins see or edit a story. Every page, API and `/media` request goes through it (others get 404). `Visibility` is stored now (default `AllFamily`, configurable via `DEFAULT_VISIBILITY`) so Phase 3 needs no data migration.

### D-026 · QR login cards are POST-only and single-use
**Decision:** "Print a login card" is a POST that mints a `DeviceEnrollment` link (single use, `LOGIN_CARD_DAYS`, default 30) and renders the card. A GET only redirects, so reloads, link previews and prefetching can't mint extra working links. Every card print is audited. The QR is a PNG `data:` URI (allowed by the CSP's `img-src`).

---
## Phase 2

### D-027 · restic 0.20.0 from the upstream release, pinned by checksum
**Decision:** Ubuntu's restic package (0.16) is years behind, so the image and CI download the official 0.20.0 binary and check it against SHA-256 values written in the Dockerfile and workflow. A tampered download fails the build. Passwords and repository locations reach restic only through environment variables or a password file, never command-line arguments.

### D-028 · What a backup contains
**Decision:** Each snapshot (`--host family-memories`, tagged `nightly`/`manual`/`premigration`/`prerestore`) holds:
- a `pg_dump -Fc` of the database, *excluding the `hangfire` schema* (the job queue rebuilds itself, and must not be rewound underneath a running restore);
- `/data/media`, `/data/keys` and `/data/config`.

Logs, exports and half-finished uploads are left out. Retention is `forget --prune` with 7 daily, 4 weekly and 12 monthly snapshots. The offsite repository gets an independent `restic backup`, not `restic copy`, so either repository can be used on its own. If the offsite copy fails, the run is marked failed and admins are alerted, even though the local copy succeeded.

### D-029 · How a restore works
**Decision:**
1. Maintenance mode on (an in-memory flag; the middleware runs *before* authentication, because the database is being replaced).
2. Safety backup of the current state. If it fails, nothing is restored.
3. `restic restore` into a staging folder.
4. `DROP SCHEMA public CASCADE`, then `pg_restore`. Starting from an empty schema means tables from newer migrations don't linger.
5. Swap the media and config folders.
6. *Merge* the Data Protection keys (backup's ∪ current), so both old cookies and today's sessions stay valid.
7. Run migrations. Maintenance mode off.

Only one backup, check or restore runs at a time.

### D-030 · Weekly verification
**Decision:**
- `restic check --read-data-subset=5%`.
- `restic restore` of the newest dump into a throwaway database (`fm_verify_*`, dropped afterwards `WITH (FORCE)`), which must contain users and prompts. The row counts are recorded.
- Any failure emails every admin, plus ntfy (`NTFY_URL`, `NTFY_TOPIC`, `NTFY_TOKEN`) if configured.

The admin home and **Backups** page show the last *good* backup and check, and turn red after 36 hours or after a failure.

### D-031 · Repository probing
**Decision:** `restic cat config` decides whether to `init`, and only exit code 10 ("no repository here") triggers it. Any other error (wrong password, unreadable disk) is reported in plain words instead of attempting an `init`. The probe has a 3-minute limit, because restic retries some backend errors for up to 15 minutes, and an unmounted disk must not hang the nightly job.

### D-032 · Disaster restore uses the app image's own tools
**Decision:** `scripts/restore.sh` needs only Docker on the new host. It runs `restic` and `pg_restore` inside a one-off `app` container on the stack's network:
- **Local repositories** are mounted read-only, and restic uses `--no-lock`.
- **URL repositories** (b2:, s3:, sftp:…) are passed through as-is.
- **The password** goes into a temporary file inside `DATA_ROOT` (mode 600, deleted on exit). The script was tested end to end by deleting the local data folder and restoring it: database rows and photo checksums matched.

### D-033 · Portable archive moves to Phase 5
**Context:** §10 lists a portable `.zip` (JSON, media, GEDCOM, README). GEDCOM and the people data arrive in Phase 3, and the zip shares its pipeline with the other exports.
**Decision:** Build it with the exports in Phase 5.

### D-034 · Every release ships a pullable image and a pinned compose file
**Decision:** Pushing a `vX.Y.Z` tag does the following:
- Runs the full test workflow.
- Builds the image for `linux/amd64` and `linux/arm64` and pushes `ghcr.io/jmconlon2001/family-memories:{X.Y.Z, X.Y, latest}`.
- Publishes a GitHub Release with `compose.yml` and `family-memories.yml` (the same file, named for OMV), pinned to that version and with no build section, plus `.env.example`, `restore.sh` and a tarball. These are generated from the repo's `compose.yml` by removing the `build:` block.

From a source checkout, `docker compose up --build` still builds locally. The repository is private, so the GHCR package is private too: hosts must `docker login ghcr.io` with a token that has `read:packages`, unless you make the package public.
