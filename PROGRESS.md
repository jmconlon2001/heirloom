# Progress

## Phase 2: Backup and restore ✅ (2026-11-25)

**Done:** everything in §10 except the portable archive, which moves to Phase 5 with the other exports (D-033).
- **Nightly backup** (`BACKUP_TIME`, `APP_TIMEZONE`) and **Back up now**:
  - `pg_dump`, then restic of the database dump, media, keys and config, then retention;
  - optional offsite repository (D-027, D-028);
  - automatic backup before migrations and before every restore.
- **Weekly check:** restic reads back 5% of the data, and the latest database is test-restored into a throwaway database (D-030). Failures email every admin, plus optional ntfy.
- **Admin:**
  - the **Backups** page shows the last good backup and check, recent runs and every snapshot, with **Restore this one…** (you type `RESTORE`);
  - a one-time "save the backup password" banner;
  - the admin home shows the backup status prominently.
- **Restore** with maintenance mode, a safety backup, a schema-clean database restore, a media and config swap, merged keys, and migrations (D-029).
- **Disaster restore:** `scripts/restore.sh` plus `docs/RESTORE.md` (D-032).
- **Releases:** every `v*` tag publishes a multi-arch image to GHCR and a GitHub Release with a pinned compose file (D-034).

### Tests: 133 passing
- `BackupRestoreTests.Backup_wipe_restore_brings_back_identical_files_and_rows`, the round trip §10 requires:
  - 3 stories with HEIC photos and voice clips;
  - back up, drop the schema and delete the media folder, restore;
  - media SHA-256s and row counts in 9 tables match;
  - Rose can sign in and sees "You've shared 3 stories".
- Plus:
  - the weekly check, including the throwaway database being cleaned up;
  - a check with no backups, which alerts admins;
  - a wrong-password repository, which fails clearly and alerts admins;
  - the admin page (banner, Back up now, status);
  - the RESTORE confirmation;
  - the maintenance page while health checks still answer.
- **By hand, in the Linux image:**
  - Back up now through the admin UI;
  - delete the whole data folder;
  - `scripts/restore.sh --repo .data/backups/restic --snapshot latest`;
  - photo checksums match, rows match, and the stack is healthy.

### Bugs found and fixed
- `pg_dump` was dumping the default database, not the app's. Caught by the round-trip test.
- restic retried a bad path for 15 minutes. Fixed with a probe time limit and exit-code handling.
- `restore.sh` failed on read-only repositories (now uses `--no-lock`) and on Docker hosts that don't share `/tmp` (the password file now lives in `DATA_ROOT`).


## Phase 1: Prompts and memories (the core loop) ✅ (2026-11-25, awaiting review)

**Done criterion met:** `AnswerTests.IPad_storyteller_answers_with_text_a_photo_and_a_voice_clip` runs on the iPad Pro 11 profile (WebKit), with these steps:
- sign in by magic link;
- tap **Answer this question**;
- type two paragraphs, and autosave shows "Saved ✓";
- add a photo through **Add photos** and give it a caption;
- **Record my voice** for 2.5 s with WebKit's mock microphone, then **Stop recording**; ffmpeg turns the clip into AAC `.m4a`;
- keep typing (autosave still works);
- **I'm finished with this story** shows the thank-you page, then the story is read back.

axe runs at every step with zero serious/critical violations. Also checked inside the Linux container image: HEIC → JPEG and WebM → M4A through Hangfire, and the pre-migration `pg_dump` ran when the Phase 1 migration was applied to the Phase 0 database.

### Built
- **Prompts:**
  - 12 chapters and 52 prompts with 2–4 hints each, in `seed/prompts.json` (★ = Core 26). The seed is an idempotent upsert by key that respects admin edits (D-017).
  - Prompt sets: built-in Core 26 and Full 52, plus custom hand-picked sets.
  - Per-person assignment and weekly or all-at-once pacing (D-024).
  - One-off questions for one person, shown first with "Lily asked:".
- **Writing page** (`/answer/{promptId}` → `/stories/{id}/write`):
  - One question per screen: large serif question, gentle hints, suggested photos, an auto-growing plain-text box with spellcheck, and the keyboard-microphone tip.
  - **Record my voice** and **Add photos** buttons, optional when and where (fuzzy date + place), **Save** and **I'm finished with this story**.
  - Gentle **Skip for now** and **I'd rather not answer**.
  - Works without JavaScript (the full form posts, files included).
  - `answer.js` adds autosave every 5 s and on blur, plus a `localStorage` copy: if the session expires, the words come back after signing in again (covered by an e2e test).
- **Uploads:** chunked and resumable (D-020), validated by magic bytes with per-kind limits. Originals are stored untouched with a SHA-256. Captions, **Move up / Move down**, and **Remove** with **Put it back**.
- **Media processing** (Hangfire, D-015):
  - Magick.NET turns photos (including iPhone HEIC) into EXIF-rotated 2048/400 px JPEGs with metadata stripped (D-021).
  - ffmpeg normalises Safari mp4/aac and Chrome webm/opus to AAC `.m4a`; ffprobe works out whether an mp4/webm is audio or video (D-022); videos get a poster frame.
  - `/media/{id}/{variant}` is access-checked and supports range requests.
- **Fuzzy dates:** year, month, day, "around", decade, or a phrase, e.g. "around 1965", "1970s", "June 3, 1972", "summer after the war (around 1945)". Impossible parts are dropped rather than rejected.
- **Version history:** every save kept, autosaves coalesced (D-018). Admins can restore from `/admin/stories/{id}/history`. Old tabs can't overwrite newer words (D-019).
- **Storyteller home:**
  - "Hello, Rose", the next question (or "That's everything for now, your next question arrives Tuesday"), and "You've shared 12 stories".
  - A chapter checklist with "3 of 5"; no streaks.
  - **See all my questions**, **Read my stories**, **Add a story of your own**.
- **Proxy entry:** admins and named helpers can write for a storyteller, attributed "Told by … · written by …" and audited (D-023).
- **Admin:**
  - People: invite with an emailed link, edit name/email/roles/set/pacing, resend invitation, pause/unpause, signed-in devices with **Sign out this device**, helpers, and one-off questions.
  - **Printable QR login cards** (D-026).
  - Questions: edit, reorder, turn on/off, add. Question sets. Activity log. Version history and restore. Hangfire dashboard.
- **Audit log:** invites, user changes, pauses, device sign-outs, login cards, helpers, question and set changes, proxy writing, restores.
- **Deploy:** image now includes `ffmpeg` and `libgomp1` (for Magick.NET); CI installs ffmpeg. New settings are in `.env.example`.

### Tests: 126 passing (`dotnet test`)
- **119 unit and integration tests**, including:
  - fuzzy-date formatting and normalising, magic-byte detection, device names;
  - seeding (counts, idempotency, admin edits kept), pacing and next-question rules, custom sets;
  - versioning, coalescing, conflict, restore, proxy attribution;
  - HEIC → JPEG, EXIF rotation, GPS stripped from copies but kept in the original;
  - Safari and Chrome voice → m4a, audio-only mp4 → audio, chunk resume, early refusal by type and size;
  - media access control and range requests, antiforgery on the API;
  - the no-JavaScript save path with a photo, finish, skip and decline, locked prompts, autosave with captions, expired-session 401;
  - cross-user access denied, helper writing;
  - all admin pages (access, invite, duplicate email, login card, device sign-out, pause, question editing, restore);
  - the real Hangfire path.
- **7 e2e tests:** the 4 from Phase 0, plus:
  - the done-criterion test above;
  - the writing page at A++ on iPad (axe, tap targets, no sideways scroll, skip);
  - words surviving an expired session.

### Not done / deviations
- **Drag-to-reorder:** the reorder API exists, but the page offers only **Move up / Move down** for now. Those buttons are the required non-drag alternative, so drag would be an extra.
- **"Who's in this photo" picker:** needs People from the family tree (Phase 3).
- **Uploads in flight:** if the tab is closed mid-upload, that upload has to be redone (resume works only within the same page). A recording that hasn't finished uploading is lost if the page is closed.
- **PDFs:** shown as a download link; no preview.
- **Video:** not transcoded for older Safari.
- **Admin chapter editing:** chapters come from the seed file; admins can't rename or add chapters in the UI yet.
- **Web font:** Atkinson Hyperlegible still isn't vendored; system fonts are used.
- **Optional password and passkeys:** still to do.

### Known issues / notes
- Voice recording in e2e uses WebKit's mock microphone on macOS. Playwright's Linux WebKit can't record at all, so on Linux CI the recording test runs the same iPad profile in Chromium with a fake microphone (which also covers the Chrome/webm path). A real iPad mic test is a manual check before family use.
- Test databases each get a small pool, and the test PostgreSQL runs with `max_connections=500`, because many test classes run in parallel.


## Phase 0: Foundation ✅ (2026-11-25, reviewed and pushed)

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
