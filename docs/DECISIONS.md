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

---
## Phase 3

### D-035 · One visibility rule set, applied in the query layer
**Decision:** `StoryAccess.Readable(user)` is the only way pages, APIs, `/media`, the Library, search, person pages and the tree get stories:
- The storyteller, whoever wrote it down, and helpers: always, including drafts.
- Anyone else: only once the story is **finished**, and then by visibility. *All family* = every signed-in person; *Selected people* = those ticked; *Private* and *Link* = nobody (Link stories are read through their link).
- Admins: everything. A view an admin couldn't make under the normal rules writes a `memory.admin-view` audit entry.

Unauthorised requests get 404, not 403, so they don't reveal that a story exists. Every visibility picker says "Family admins can always see your stories."

### D-036 · Admin "view as" swaps the identity, not the rules
**Decision:** "See the site as Rose" sets a data-protected cookie (admin id + target id). A middleware after authentication replaces `HttpContext.User` with Rose's principal, so every page and query applies Rose's rules with no special cases. It is read-only: any non-GET is refused, except **Stop**, which needs no CSRF check because it can only reduce what the admin is shown. Starting it is audited, and a banner is shown on every page.

### D-037 · Special links: random, hashed, optional passcode, short unlock cookie
**Decision:** Each link is 192 random bits; only the SHA-256 is stored, and the URL is shown to its creator once (with a QR code). A link works only while the story is finished, its visibility is still *Link*, and the link isn't revoked or expired (30 days / 1 year / until turned off). Optional passcodes are PBKDF2-hashed (Identity's `PasswordHasher`); a correct one sets a 12-hour data-protected cookie scoped to that link's path. Media goes through `/s/{token}/media/…`, checked against the link and its story. Passcode attempts share the sign-in rate limit, and link pages send `noindex`.

### D-038 · "Ask a follow-up" is a one-off prompt
**Decision:** A follow-up becomes a `Prompt` with `ForUserId` = storyteller, `AskedByUserId` and `FollowUpOfMemoryId`. It appears first in their list as "Lily asked:", and the email goes out on the next reminder (`NotifiedAt`, Phase 4). Hearts toggle; comments are plain text (max 2000). Authors, the storyteller and admins can remove a comment, which is soft-deleted. First reads are recorded per person (`MemoryRead`) for Phase 4's "3 family members read your story".

### D-039 · Anyone in the family can edit the tree; only admins delete or link accounts
**Decision:** The tree is collaborative: any signed-in person can add and edit people and connections, and every change is audited. Removing a person and linking a person to a sign-in account are admin-only. Adding a parent that would make someone their own ancestor is refused. Living people's birth dates show only the year to non-admins, in the person page, lists and `/api/tree`.

### D-040 · Tagging by name, with explicit "Add" buttons
**Decision:** "People in this story" and "Who's in this photo?" use a text box with a `<datalist>` of everyone in the tree and an **Add** button, rather than a 100-checkbox list. Existing tags are checkboxes, untick to remove, and those are autosaved. A name that isn't in the tree gets a plain message pointing to Family tree. It works without JavaScript.

### D-041 · family-chart 0.9.0 (MIT) + d3 7.9.0 (ISC), vendored; style attributes allowed on /tree only
**Decision:**
- Both libraries live in `wwwroot/lib`; nothing loads from a CDN.
- family-chart positions cards with `style` attributes, so `/tree` alone adds `style-src-attr 'unsafe-inline'`. Scripts stay `'self'` only.
- The default dark theme failed WCAG contrast, so it is restyled with light cards and dark text (also in high contrast).
- **Zoom in / Zoom out** buttons call the library's `manualZoom`, so pinch isn't required.
- Under the diagram, the same close family is a plain list of links ("Centre on Ann"), for screen readers and anyone who prefers lists.

### D-042 · GEDCOM 5.5.1: names, dates, places, families; nothing else
**Decision:**
- **Parser:**
  - INDI (NAME/GIVN/SURN/NICK, SEX, BIRT, DEAT/BURI, NOTE), FAM (HUSB, WIFE, CHIL, MARR, DIV), and CONC/CONT.
  - Date qualifiers (ABT, EST, CAL, BEF, AFT, BET…AND, FROM…TO) become "around".
  - Sources, citations and event notes are ignored (§7).
- **Import preview:** flags likely duplicates (same first name and surname or maiden name, birth years within 1, or the same GEDCOM id from an earlier import). The admin chooses per person: same as / new / skip. Matching never overwrites what the family typed; it only fills empty fields.
- **Export:** writes a birth-name record, and one FAM per couple plus one per single or unpartnered parent set.

### D-043 · Family Library search uses PostgreSQL full-text, no extra index
**Decision:** `to_tsvector('english', title ‖ body ‖ place) @@ websearch_to_tsquery(q)`, plus ILIKE on captions, places and tagged people's names. At family scale (hundreds to a few thousand stories) a GIN index isn't needed; add one if search ever takes over 100 ms.

### D-044 · Test fixtures that run in parallel must not share mutable state
**Context:** Two flaky e2e failures turned out to be cross-talk between parallel tests:
- Kestrel bound "localhost" on port 0 with different random ports for IPv4 and IPv6.
- Playwright's `Devices["…"]` returns a shared object whose `BaseURL` we were mutating.

**Decision:** e2e servers bind `127.0.0.1` only, and device options are copied before use.

---
## Phase 4

### D-045 · Scriban for editable email templates (BSD-2-Clause)
**Decision:** §8 allows Scriban or Razor; Scriban is built for templates edited at runtime, so admins can change wording without a rebuild.
- Every kind has a built-in default (in code). An admin override is stored per kind and can be reset.
- For the HTML part, every model value is HTML-encoded before rendering, so an admin's edit can't open an injection hole. The `{{ button url "Label" }}` helper draws the one big button.
- Broken templates are refused with Scriban's error message.
- **Preview** posts the draft into an iframe served with its own CSP: inline styles allowed, no scripts, framed only by our pages.

### D-046 · All mail goes through EmailService → EmailLog → Hangfire
**Decision:** Render, then write an `EmailLog` row, then enqueue `EmailDelivery`. It is retried 5 more times over about 4 hours (1, 5, 15 and 60 minutes, then 3 hours) and marked Failed after that. The admin **Sent emails** page lists every send with its error and a **Try again** button. With `JOBS_ENABLED=false`, delivery is immediate. Each message has a plain-text part, the help line, and an email-settings / unsubscribe link. Reminders, digests and read summaries also carry `List-Unsubscribe` and `List-Unsubscribe-Post` (RFC 8058) headers.

### D-047 · SMTP settings: admin UI over .env, password encrypted
**Decision:** The admin **Email** page is pre-filled from `.env`; once saved, the stored settings win. The password is encrypted with Data Protection (whose keys are in `/data/keys`, which is backed up), never shown again, and "leave empty to keep". **Send test email** reports success or the exact SMTP error.

### D-048 · Reminder scheduling
**Decision:**
- **Family-wide defaults:** weekly, Sunday 10:00, `APP_TIMEZONE`, quiet after answering, nudge after 3, Saturday digest. Each person can override frequency, day, time and time zone.
- **A tick every 15 minutes** finds each storyteller's latest scheduled slot (`ReminderSchedule`, pure and unit-tested). A local time skipped in spring moves to the first valid minute; a time repeated in autumn uses the first one. If that slot hasn't been handled, it is handled once: sent or deliberately skipped.
- **Skip when** the person is snoozed or off, everything is answered (declined counts), next week's question isn't unlocked yet, or they wrote something since the previous slot (the quiet period skips exactly one reminder; a bug that skipped every later one was caught by tests).
- **The reminder** carries the full question and hints, one button (a 14-day single-use `Reminder` sign-in link to `/answer/{prompt}`), a progress line, and the help contact. A follow-up question arrives this way ("Lily asked you a question").
- **The nudge:** after N reminders with no activity, admins get "Grandma Rose hasn't added a story in 3 weeks. Maybe give them a call?" once, plus ntfy.
- **If already signed in** on that device, the reminder link goes straight to the question without using up the link.

### D-049 · Email settings by signed link; changes need a tap
**Decision:** The link in every email (`/email/preferences/{token}`, a Data Protection–signed user id, no expiry) opens a page with big buttons:
- **Remind me in 2 weeks**;
- **Stop reminder emails** / **Start them again**;
- digest and read-notification checkboxes.

Changes are POSTs, so mail scanners that open links can't snooze or unsubscribe anyone. Mail clients' own unsubscribe button uses the RFC 8058 one-click POST endpoint. Its forms post to `?handler=…` on the page's own URL: ASP.NET's `LowercaseUrls` lowercases generated URLs, including the case-sensitive token. The e2e test caught that.

### D-050 · Read summaries and the digest
**Decision:**
- **Read summaries, daily at 17:00:** one email per storyteller about first reads since the last one ("Lily, Ed and Ann read your story “The farm”"). Opt-out per person.
- **The digest, Saturdays at 09:00:** stories finished since that person's last digest that *they* may read under the normal rules (admins are not shown private stories here), excluding their own; nothing is sent if there's nothing new. Opt-out per person.

---
## Phase 5

### D-051 · One set of Razor templates for website, PDF and book
**Decision:** `/Templates/Export/` holds the shared templates:
- `Book.cshtml` serves both the shareable PDF (`body.screen`) and the print interior (`body.print`).
- `Cover.cshtml` is the cover.
- `Site/*.cshtml` are the static website pages.

They're rendered to strings outside a request (`RazorRenderer`) from one `ExportContent` model, built by `ExportContentBuilder` from the scope: storytellers, chapters, statuses (default finished), Private excluded unless an admin ticks it *and* confirms, year range. Living people's birth dates are year-only in every export, because exports leave the app.

### D-052 · PDFs: Gotenberg (Chromium) + Paged.js 0.4.3 (MIT), fully self-contained
**Decision:**
- Every asset Gotenberg needs is sent with the request, and the CSS is *inlined* with the Literata fonts as data URIs. A separate `book.css` stopped Paged.js from finishing, because a `file://` document can't fetch it.
- Gotenberg waits for `window.pagedDone` (set in `PagedConfig.after`), outputs an outline (bookmarks), and runs with `failOnConsoleExceptions`, so a template mistake fails at once with the JavaScript error instead of after the 10-minute `--api-timeout`.
- Paged.js limits we found and worked around: no `leader()` (an unsupported function drops the whole rule; the contents page right-aligns numbers instead); `target-counter` rules must use simple selectors (`:first-of-type::after` broke its parser and hung the layout); and no CSS columns.

### D-053 · Print interior rules
**Decision:**
- Trim 6×9 / 8×10 / 8.5×11, optional 0.125 in bleed.
- Mirrored margins with a gutter (0.875–1 in inside).
- Title, copyright, dedication and contents pages as front matter without running heads; chapter dividers start on right-hand pages (`break-before: right`); running heads are the book title (left) and section (right); page numbers at the foot.
- Images re-encoded from the originals at up to 3000 px (300 dpi at the printed width); 1600 px for the screen PDF.
- The page count is measured from the PDF; if it isn't a multiple of the printer's number (default 2), the book is re-rendered with that many blank pages added.

### D-054 · Cover and spine
**Decision:** The cover is one spread: back + spine + front, plus 0.125 in bleed all round. Spine = pages ÷ pages-per-inch: KDP white 0.002252 in/page, KDP cream 0.0025 in/page, Lulu about 444 pages per inch, or a custom value. Spines under 0.25 in carry no text. The test checks the cover PDF's MediaBox against this formula. The preflight tells admins to compare with the printer's own cover calculator before ordering.

### D-055 · QR codes are "clip links"
**Decision:** Each recording or video in a PDF or book gets its own `LinkShare` with `AttachmentId` set. It opens a simple player for that one clip, whatever the story's visibility, until it is revoked (a printed book can't be updated). The media endpoint refuses anything except that clip for such links.

### D-056 · The static website works from a USB stick
**Decision:** Every page is at the root with relative links. No ES modules and no `fetch()`, since both fail from `file://`. Search uses a plain `<script>` index (`assets/search-index.js`) and a classic script. Media is copied in (web and thumbnail JPEGs, AAC, video originals and posters, documents). The same zip can go on Cloudflare Pages or any static host.

### D-057 · EPUB and DOCX through pandoc, from the same Markdown as `source/`
**Decision:** `source/book.md` (plus a file per story with YAML front matter, `metadata.yaml`, full-resolution images, `people.ged`) is the input to `pandoc` for EPUB 3 (with a cover image when one is chosen) and DOCX. If pandoc is missing (dev machines), the preflight says so and the rest of the package is still made. pandoc is in the app image and CI.

### D-058 · The portable archive is the "if this app disappears" copy
**Decision:** It contains:
- `data.json`: every story with every version, attachments, people, comments, hearts, questions, the tree, and the people who sign in (names and emails);
- `media/`: originals and copies, byte for byte;
- `people.ged`;
- a plain `README.txt` explaining the structure.

It always includes everything, private stories too, because it's the family's complete record. Downloads of any export are admin-only and audited.

### D-059 · Web fonts: Atkinson Hyperlegible and Literata, vendored (OFL)
**Decision:** The app now uses Atkinson Hyperlegible (designed by the Braille Institute for low-vision readers) for interface text and Literata for headings and story text. Only the Latin subsets are vendored (`wwwroot/fonts`, `Templates/Export/assets/fonts`), and nothing loads from a font CDN. Non-Latin names fall back to system fonts.

---
## Phase 6 (optional extras, all behind flags, off by default)

### D-060 · Speech-to-text runs locally and needs the storyteller's OK
**Decision:** With `FEATURE_TRANSCRIPTION=true`, a finished voice recording is sent to `onerahmet/openai-whisper-asr-webservice:v2.0.0` (MIT; faster-whisper, CPU, `small` model), compose profile `transcription`, 2 GB memory limit, model cached in `DATA_ROOT/whisper-models`. Nothing leaves the server. The text is *not* added automatically. The writing page shows "Here's what we heard. Fix anything that's wrong", with **Add these words to my story** or **No thanks**. Elders' speech, names and places are often misheard, so a person always checks first. Tests use a stand-in transcriber; the HTTP contract with Whisper is tested with a stub.

### D-061 · Answer by email reply: plus-addressing, sender check, draft for review
**Decision:**
- With `FEATURE_EMAIL_REPLIES=true` and `REPLY_ADDRESS`, each reminder's `Reply-To` is `local+CODE@domain`. The code is 8 characters from an unambiguous alphabet, stored per person and question. The email says "Or simply reply to this email…".
- An IMAP mailbox is polled every 5 minutes (MailKit, already in the stack). A reply is used only if it comes **from the person's own address**; anything else is ignored and audited.
- The quoted reminder, "On … wrote:", signatures and "Sent from my iPad" are stripped.
- The words are appended to that question's story, and photos and documents go through the normal magic-byte checks. The story becomes a **draft** with a banner. It counts as approved when the storyteller or an admin presses **I'm finished**; the admin home lists replies waiting for a look.
- Limits: sender addresses can be forged, so this trusts the mailbox provider's spam/DMARC filtering. The worst case is a draft an admin can see and delete.

### D-062 · Paper packets
**Decision:** With `FEATURE_PAPER_PACKETS=true`, a storyteller's admin page can print a Letter-size PDF (Gotenberg; Atkinson Hyperlegible, 15–21 pt): a warm cover letter, then up to 12 waiting questions, one per page, with hints and ruled lines. Completed pages come back through the existing proxy entry: "Write a story for Rose", pick the question, **Add photos** with a phone photo or scan, and optionally type it up.

### D-063 · Text typed before the page script runs is still saved
**Context:** A full-suite e2e flake showed that text typed before `answer.js` (a deferred module) attached its listeners was never marked unsaved, so autosave never ran. That's possible on a slow iPad, or when the browser restores a form after Back.
**Decision:** On start, the script compares every text field with its server-rendered value and saves at once if they differ.

---
## Beta release

### D-064 · A public landing page at `/`
**Decision:** Signed-out visitors to `/` see a landing page (`_Landing.cshtml`): "Every family has stories worth keeping.", a sample question card, how it works in three steps, what it does for each kind of family member, and a **Sign in** button. It uses the app's own stylesheet and fonts, has no scripts or third-party assets, and keeps the strict CSP. It contains no testimonials or claims we can't back up. Signed-in people still get their home page at `/`, and the header shows **Sign in** to signed-out visitors everywhere except the sign-in pages themselves.

### D-065 · Demo mode for beta testers
**Decision:** `DEMO_MODE=true` seeds a made-up family once, only when `rose@demo.family` doesn't exist yet: Grandma Rose (storyteller), Ann, Lily (family member and Rose's helper), a 7-person tree, six finished stories (one private, one written by Lily for Rose), generated sepia photos, hearts, a note, a follow-up question and a draft. The sign-in page then shows **Try the demo** buttons (admin, Rose, Lily) that sign in through the normal device flow. Those buttons are exempt from the sign-in rate limit, and every page shows a banner: "Demo site with a made-up family. Please don't add real family stories here." The demo sign-in handler returns 404 when the flag is off (tested), and the app logs a warning at startup when it's on.
**Risk:** anyone who can reach a demo site can sign in as the admin, so `.env.example` and BETA_TESTING.md say to put it behind basic auth or an allow-list and never to use it with real stories.

### D-066 · Status page and status API
**Decision:** One service (`SystemStatusService`) checks the website, database (latency, size), PDF maker, backups, email (24-hour sent/failed, and whether the latest attempt failed), background jobs (Hangfire workers and failures), storage space on the data and backup disks, and speech-to-text when it's on. Each part is **ok**, **warning** or **problem**.
- `/status` is a plain-language page. Anyone sees each part's name and state; admins also see the details, the version, uptime and monitoring hints.
- `/api/status` is JSON. Anyone gets only the overall state. Admins, or a monitor sending `Authorization: Bearer STATUS_TOKEN` (constant-time compare), get every component. It answers **503 when anything is a problem** (and during a restore), so monitors can alert on the status code alone. Warnings stay 200.
- Results are cached for 15 seconds, so an anonymous caller can't hammer the database or Gotenberg. Maintenance mode lets `/api/status` through and skips the database during a restore.
- `/healthz` and `/readyz` are unchanged (container health checks use them).
- The version comes from the image: the release workflow passes `APP_VERSION` as a build arg (`/p:Version`); local builds say `0.0.0-dev`.

### D-067 · Simple and Detailed views, on every page, remembered per person
**Decision:** Everyone who's signed in has a **View: Simple | Detailed** switch in the header, next to Text size and High contrast.
- **Simple** (the default) is the calm, large look the site was designed around: top menu, one thing at a time, 48px targets. Storytellers never need to touch the switch.
- **Detailed** works like a typical web app: a dark sidebar with every section (Family Library, Timeline, Photos, Storytellers, Search, Family tree, People; "My stories" for storytellers; and, for admins, the admin sections grouped as Admin, Questions, Email and Data), 16px base text, compact buttons, form fields and striped tables. On the admin home, a dashboard (stat tiles, the system table, recent activity) replaces the link list. Admin pages use a wide 80rem column, while reading pages stay at 52rem so story text keeps a comfortable line length. Text size and high contrast still apply. At 48rem (768px) and narrower (phones, iPad mini), the sidebar folds into a "Menu: <current page>" button (D-069).
- **Names:** "Simple" and "Detailed" (we tried "Basic"/"Complex" first). They describe what you get, and "Complex" sounded like a warning to the people this app is for.
- The choice is stored per person in the settings table (`view-mode:<user id>`), so it follows them to every device and never affects anyone else. "View as" always shows the Simple look. Signed-out visitors don't get a switch. `POST /view` needs sign-in and an anti-forgery token, and only redirects locally.
- Detailed targets are 40px (WCAG 2.2 AA needs 24px). Axe passes on both views.

### D-068 · Every POST form gets its own anti-forgery token
**Context:** "Print a login card" failed with a 400 for everyone: its form posted to an explicit `action` URL, and the form tag helper only adds the token automatically when there's no `action`. The integration test had passed because the test helper borrowed a token from another form on the same page.
**Decision:** The form now has `asp-antiforgery="true"`. A new test loads the main pages and fails if any `method="post"` form lacks its own `__RequestVerificationToken`, and the login-card test now posts only that form's fields. With the fix removed, both tests fail.

### D-069 · Detailed view on phones: the sidebar folds into a menu
**Context:** At phone width the sidebar became a wrapping bar that filled the first screen, and its long labels made "People and invitations" scroll sideways.
**Decision:** At 48rem and narrower, the sidebar is a `<details>` showing "Menu: <current page>". `side-nav.js` closes it on narrow screens (without JavaScript it stays open). 820–834px iPads in portrait keep the full sidebar. The header is tighter in Detailed on phones (just the name, smaller gaps), the dashboard tables scroll inside themselves, and `code` wraps. Checked by crawling every reachable page as admin, family member and storyteller at 320, 390 (normal and largest text), 768, 820 and 1280px. A new iPhone e2e test covers the menu and sideways scrolling.

### D-070 · Speech-to-text and paper packets are on by default
**Decision:** `FEATURE_TRANSCRIPTION` and `FEATURE_PAPER_PACKETS` default to on, and only `false`, `0` or `off` switches one off. Each starts working as soon as it has what it needs:
- **Paper packets:** needs Gotenberg, which is already in the stack.
- **Transcription:** needs `WHISPER_URL`.

`FEATURE_EMAIL_REPLIES` stays off by default. It lets mail from outside the site create story drafts, so turning it on should be the admin's deliberate choice. Once on, it needs `REPLY_ADDRESS`, and the inbox poll also needs `IMAP_HOST`.
The `whisper` service now runs in the standard stack (no profile), and compose gives the app `WHISPER_URL=${WHISPER_URL:-http://whisper:9000}`. Optional extras shows "On", "Waiting to be set up" or "Switched off".

**Whisper on another machine:** A `WHISPER_URL` ending in `/v1` is treated as an OpenAI-compatible server (`POST /v1/audio/transcriptions`, model `WHISPER_MODEL`, default `whisper-1`). That covers whisper.cpp's `whisper-server`, speaches and LocalAI, so a Mac with Apple silicon can transcribe on its GPU. Ollama has no speech-to-text API, so it can't do this job.

**Fixed in v0.9.0:** `whisper` was only on the `internal` backend network, so it could never download its model, and speech-to-text didn't work in the standard stack. It's now also on `frontend` (outbound only; no published ports). Whisper being unreachable is now a **warning**, not a problem, so `/api/status` stays 200: recordings still save and play.

### D-071 · Before v1: readiness work
- **OMV file names:** the compose file reads `.env` or `family-memories.env` (`env_file` entries with `required: false`, Compose ≥ 2.24). That covers the container's environment only. Compose still fills in `${DATA_ROOT}` and the rest from `.env`, so OMV still needs `ln -sf <stack>.env .env`; a real upgrade showed this. `restore.sh` also accepts `family-memories.yml`, `family-memories.env` and `compose.override.yml`, and defaults `POSTGRES_USER`/`POSTGRES_DB` like compose does. A rehearsal found that it failed when they weren't in the env file.
- **Chapters:** admins rename, move, add and remove (empty, admin-added only) chapters. `Chapter.EditedByAdmin` stops the seed overwriting a renamed or moved chapter. Moving one chapter marks all of them, so the family's order holds as a whole.
- **Videos:** each upload also gets `video.mp4`: H.264 4:2:0, longest side at most 1920px, AAC, faststart. H.264 + AAC MP4/MOV uploads are remuxed, not re-encoded. `/media/{id}/video` falls back to the original until the copy exists. An hourly `video-backfill` job converts up to five older videos per run. Exports use the copy.
- **Recordings survive a closed page:** each second of a recording goes into IndexedDB (`fm-recordings`) as it's made. The device copy is deleted only after the server confirms the whole upload. Opening the story again sends anything left over. `beforeunload` warns while recording or uploading. Without IndexedDB it records as before.
- **PDF previews:** `pdftoppm` (poppler-utils) renders page 1 into `web.jpg`/`thumb.jpg`. If it's missing or fails, the PDF is still added with its link.
- **Drag to reorder** photos and recordings on the writing page: pointer events (finger or mouse) with `touch-action: none` on the handle, and the listeners on `window` (moving the item releases pointer capture). The arrow keys work on the focused handle. Move up / Move down stay.
- **Family tree on phones** (narrower than 600px) starts at full size on the chosen person, not "fit everything".
- **Security:**
  - Reviewed every endpoint's authorization and the share-link, sign-in and upload paths.
  - A removed attachment is now served only to people who can edit the story.
  - CI scans the image with Trivy and fails on fixable HIGH/CRITICAL findings; it found none today.
- **Upgrade rehearsal:** a v0.6.0 demo site was upgraded to this build. The pre-migration backup was taken, the migration applied, and every row count matched.
- **Password and passkey sign-in:** dropped from v1. Sign-in links, login cards and 180-day sessions cover the people this app is for, and passwords would add reset flows and a weaker factor.
- **Still for the family to do before v1:** a restore drill on the real server and the offsite copy, a real iPad microphone check, and acting on beta feedback.

### D-072 · The product is called Heirloom

**Decision:** The product is **Heirloom**, tagline "Stories worth handing down." `APP_NAME` is still the family's own name for its site. It now defaults to "Heirloom", and when a family sets its own name a small "Kept with Heirloom" line appears at the foot of each page.

- **Mark:** a locket with a sprouting seed inside (`_Mark.cshtml`, inline SVG, decorative). It appears in the header, on the landing page and on the printed book cover. `favicon.svg`, `favicon.ico` and a 180px home-screen icon use it.
- **Colour:** the accent goes from navy `#1d4f91` to heirloom green `#2f5d46` (7.6:1 on white, 6.7:1 on the header background), with hover `#23463a` and focus ring brass `#8c5f16` (5.5:1). The page background, text and high-contrast mode are unchanged. Emails, the exported website and the book follow.
- **Type:** unchanged. Literata is the brand face (wordmark, questions, the book); Atkinson Hyperlegible does everything people read and tap.
- **Image:** published as `ghcr.io/<owner>/heirloom` and, with the same tags, under the old name `ghcr.io/<owner>/family-memories`, so older compose files keep pulling. Releases ship `heirloom.yml` for OpenMediaVault, plus `family-memories.yml` as the same file.
- **Not renamed, on purpose:** the Compose project name (`family-memories`), the restic `--host` tag, the archive format id, `family-memories.env` support, the C# namespace and data folders. Renaming any of them would break existing installs or backups for no visible gain.

### D-073 · Importing people and stories from a spreadsheet

**Decision:** Two admin pages, `/admin/import/people` and `/admin/import/stories`, read a CSV saved from any spreadsheet. They follow the GEDCOM import: upload, a preview with a choice per row, then import. The upload is kept in `uploads/` until it's imported.

- **One people sheet for both kinds of "people":** each row is a family-tree person; a row with an email also gets a sign-in account, linked to that person. Families keep one list, not two. Parents and partners are names, resolved first within the sheet, then against the tree. A name that's ambiguous or unknown is reported, not guessed.
- **Duplicates:** people use the GEDCOM matcher (same first name and surname or maiden name, birth years within one), and a match only fills blanks. Stories are duplicates when the storyteller already answered that question or has a story with that title; they default to Skip, so running the same sheet twice adds nothing.
- **Invitations are opt-in** at import time, so a bulk import can't email the whole family by accident. Story imports send nothing.
- **Imported words are the storyteller's own** (no "written by") unless the sheet names who wrote them down. Finished is the default status, since these are usually stories already written.
- **Forgiving reading:** headers match loosely ("First name", "first_name", "FirstName"); comma, semicolon (European Excel) and tab separators; quoted cells with line breaks; a UTF-8 BOM; dates in the usual US forms, ISO, decades, "about", and Excel's date numbers. A story's unreadable date is kept as words; a person's is dropped with a note.
- **Limits:** 10 MB / 5,000 people, 20 MB / 5,000 stories per file. Photos and recordings aren't in a CSV; add them to each story afterwards.

### D-074 · Site name, help contact and starting display are set in the app

**Decision:** Family admin → **Site name, help phone number and display** (`/admin/site`) sets the site's name, the help name and phone number, and the text size and contrast a new device starts with. They're stored as one `site.settings` row and applied to the `AppOptions` singleton at startup and on save, so every page, email, login card and export follows without a restart. An empty field falls back to `APP_NAME`, `HELP_NAME` or `HELP_PHONE`.

- The help number is checked to be a phone number, and is rendered as a `tel:` link; the email footer now uses the same wording as the site ("Call Jacob at …").
- The starting display only applies to a device that hasn't chosen; a choice made with the buttons is kept in its cookie. The script reads the page's current classes when there's no cookie, so pressing one button doesn't reset the other to the built-in default.
- The admin home shows "Add a phone number for help" until one is set.

### D-075 · Backups you can see, OneDrive through rclone, and a plain copy

**Decision:** The Backups page says what the last backup held and where every copy is, in plain words. It warns when the backups share a disk with the stories. Families can keep an offsite copy on the cloud drive they already have, and optionally an ordinary unencrypted copy.

- **What's in it:** after each successful backup, a `backup.manifest` setting records the dump size, the media file count and size, the key and config file counts, and counts of stories, files, people and accounts. These are measured at backup time rather than recomputed on every page view.
- **Where it is:** compose passes `DATA_ROOT_ON_HOST`/`BACKUP_ROOT_ON_HOST`, so the page can show the server's own folder names. Offsite repositories are named by kind ("Backblaze B2", "OneDrive (through rclone)", the latter read from `rclone.conf`), with any password in the URL masked.
- **Same disk:** found from `/proc/self/mountinfo` (the device behind `/data` vs. the local repository). It's a warning on the page, and a status warning only when there's no offsite copy as well. Where it can't tell (not Linux), it says nothing.
- **rclone** 1.76.0 is in the image, pinned and checked against the SHA-256 published on downloads.rclone.org and the GitHub release. `RCLONE_CONFIG=/data/config/rclone.conf`, which lives in the config folder and so is itself backed up. `restore.sh` stops early with a clear message if an `rclone:` repository has no config yet.
- **Plain copy** (`PLAIN_COPY`, off by default): `pg_dump -Fp -Z 6` to `heirloom.sql.gz`, a changed-files-only mirror of the media folder, and a README. It isn't encrypted and keeps no history, so it sits next to restic rather than replacing it. If it fails, the backup itself doesn't.

### D-076 · The family's genealogy program stays the master tree

**Decision:** Heirloom isn't a genealogy editor. Programs like Reunion already have far better models for people, places, events and sources. Heirloom reads them, through GEDCOM, and shows them next to the stories.

- **Read:** life events (birth, christening, residence, occupation, census, immigration, naturalization, military, burial, EVEN/FACT with TYPE, and more), the couple's events on both partners ("with Mary Walsh"), notes (including shared NOTE records), source citations (title, page, URL), and links (`WWW`/`_URL`, FamilySearch `_FSFTID`, web `OBJE` files). Stored as `PersonFact` (citations as jsonb) and `PersonLink`. Social Security numbers are deliberately not read.
- **"This file is our main family tree":** for matched people, the file's names, dates and places win wherever it says something. Its facts and links replace the earlier imported ones (`FromGedcom`). Stories, photos, accounts and hand-added links are untouched. Without the box ticked, facts are added once and only blanks are filled.
- **Notes from the file** become a "Notes" fact, no longer the person's About text, which stays the family's own words.
- **Shown** on the person page as "Life events and sources", open in Detailed view and collapsed in Simple view, with a "Sources (n)" disclosure per event. A site-wide main-tree link lives in Site settings.
- **Export** writes all of it back (SOUR records with TITL/WWW, citations with PAGE, OBJE links with titles, `_FSFTID`), and a round-trip test checks that nothing is lost.

### D-077 · Running it safely, and knowing when to update

**Decision:** `docs/SECURITY.md` explains what's exposed, how sign-in works, what's scanned, how to update, and what's left to the family. The admin home shows "Version X is available" from a once-a-day check of GitHub's latest release (`UPDATE_CHECK=false` turns it off). The request carries only a generic User-Agent. Development builds never prompt. The README now says how to customise without a fork, since everything a family changes lives in its own database.

### D-078 · Formatting and inline media, in plain text

**Decision:** Stories stay plain text, written with a few marks people know from email: `**bold**`, `*italic*`, `## heading`, `- list`, `1. list`, `> quote` and `[words](https://…)`. A line `![caption](media:ID)` places a photo, recording or video at that point. `StoryMarkup` turns this into HTML blocks for the site, the PDF/book and the website export, into Markdown for pandoc (EPUB/DOCX), and into plain text for excerpts and search.

- **Why not TinyMCE or stored HTML:** plain text stays readable for decades, in the archive export, in emails and in version history. It needs no inline scripts, so the strict CSP stays as it is. Existing stories need no migration, and an iPad keyboard works exactly as before.
- **Safe by construction:** everything is HTML-encoded first, and the only tags produced are bold, italic and http(s)/mailto links, always with `rel="noopener"`. Tests cover `<script>`, `javascript:` links, raw tags and attribute-breaking URLs.
- **Who sees the toolbar:** Detailed view, and anyone writing for someone else. Simple view keeps the plain box, so storytellers see no change. The toolbar (`format.js`) only edits the textarea, so autosave and the keep-on-device copy work unchanged.
- **Placement:** media not placed in the text still shows after it, as before. A placed item that's later removed simply doesn't render.

### D-079 · Raw material next to the finished story

**Decision:** `MemorySource` keeps the words as they first arrived, next to the story, while the story itself is the edited version. It holds:

- an email reply, whole, as received;
- what speech-to-text heard before anyone fixed it, with its recording;
- spreadsheet imports;
- anything pasted in: a letter's transcription, notes, a first draft.

Only people who can edit the story see raw material, on the writing page in Detailed view or when writing for someone. "Copy into the story" appends it to the story, and it's in the complete archive export. Paper packet pages are scans, so their transcription is added the same way, as "a transcription of a letter or paper page".

### D-080 · The family archive

**Decision:** Family Library → Archive holds what isn't a story. `Collection` is a folder, and folders nest. `ArchiveItem` is a letter, certificate, album page or recording, with a title, description, fuzzy date, place, transcription and provenance ("where the original is now"). An item can sit at the top or in a folder, and can be linked to stories ("Related documents").

- **Files reuse the story pipeline:** `Attachment.MemoryId` is now optional and `ArchiveItemId` is new, with a check constraint that exactly one is set. Upload checks, processing (web copies, PDF previews, playable video), storage (`media/archive-{item}/…`), serving and backups are all shared.
- **Who can do what:** every signed-in family member can see and add. Whoever added an item, and admins, can change or remove it. Folders are admin-only, and only an empty folder can be removed. Removing an item deletes its files; earlier backups keep them.
- **Photo metadata:** read with Magick.NET before the web copies are stripped. That covers EXIF DateTimeOriginal, IPTC caption and keywords, and XMP `dc:description`, `dc:subject` and Lightroom's `lr:hierarchicalSubject` ("Family|Conlon|William").
  - A flat keyword that only repeats the end of a hierarchical one is dropped.
  - Camera boilerplate ("OLYMPUS DIGITAL CAMERA") is not a caption.
  - The metadata fills an empty caption, an item's empty description and date, and a title that's only the file name. Originals keep all of it.
  - Story photos get keywords too, and the Photos page has the same keyword browser.
- **Folders in the database, files on disk:** the hierarchy, search and permissions live in the database, where they can be managed. The files stay ordinary files that restic, the plain copy and rsync all handle well.
- **Exports:** the website gets an Archive page grouped by folder. The complete archive adds folders, items, transcriptions and file paths to `data.json`; the files are already under `media/`.

### D-081 · On this day

**Decision:** `OnThisDayService` gathers what happened on a month and day in earlier years. The sources are:
- stories (only those the viewer may read);
- births and deaths from the tree;
- MARR events from the main tree, shown once per couple;
- archive items;
- photos dated by their EXIF date taken.

Only dates with a known month and day count. February 29 is remembered on the 28th in other years.

- **Privacy (§7):** a living person's birthday is shown only to admins; everyone sees the birthdays and remembrance days of those who have died. Writing this turned up a leak from D-076: the person page's life events showed a living person's full birth date to the family. Birth, christening and baptism events for living people now show only the year to non-admins.
- **Where it shows:** `/library/on-this-day` (today, with arrows for other days and "coming up this week"); a home card when today has anything; and the last part of the weekly digest ("Coming up this week in the family's history", without photos). The digest is still only sent when there are new stories, so this adds no new emails.
- **Today** is the family's date (`APP_TIMEZONE`), not the server's.

### D-082 · Validated against a real Reunion 14 export

**Decision:** The GEDCOM import was checked against a real family's Reunion 14 export: 1,434 people, 487 families, 1,425 sources, 3,354 events. Three checks were run against it:

- the reader compared to a separate, plain reading of the file, person by person;
- the import through the admin pages compared to the file's own counts and to each person's events, notes, citations and links;
- a "main tree" re-import and an export round trip.

The file and the person-level spot-checks stay outside the repository. `RealGedcomTests` runs against any file named in `HEIRLOOM_GEDCOM_SAMPLE`, and is skipped otherwise.

What the real file showed, now fixed and covered by tests with made-up data:
- **Line endings:** Reunion ends lines with a bare CR, so the reader saw one line and imported nothing. Lines now split on CR, LF or CRLF.
- **Form size:** the check-before-importing form posts one choice per person, past ASP.NET Core's 1,024-value limit, so the final Import step answered 500. The GEDCOM page and both spreadsheet imports now allow 100,000 values.
- **Old dates:** `FuzzyDate.MinYear` was 1800 and dropped 18th-century dates. It's now 1000.
- **Unusual dates:** ranges and choices without "AND" (`bet 1885  1887`, `1871/1874`) keep their first year as "around". A day and month with no year (`7 SEP`) and parenthesised phrases are kept as words, stored in a new `PersonFact.DateWords` column and written back as GEDCOM date phrases.
- **Reunion's own events:** CITN, EMPL, HIST, NAMR, CAUS, ALIA, HAIR, HEIG, CLER and SEPA are read. Event values that point at a shared note (`1 MILI @N225@`) bring the note. A `CAUS` under a death becomes "Cause: …" in its note. RACE and SSN are deliberately not read.
- **FamilySearch IDs and name citations:** Reunion keeps the FamilySearch ID in `REFN` (`LJLG-H2H`), and it becomes a FamilySearch link. Citations on the NAME count among the person's sources.
- **Untitled sources:** 145 Reunion sources have no TITL (they're built from templates), and all showed as "Source", so different sources merged. They now get a title from TYPE, AUTH, PERI/PUBL (or the attached file's title), DATE and PAGE, falling back to the first line of TEXT. `LOCA` is used as the link when there's no URL.
- **"(Living)" placeholders:** 317 people were exported unnamed. They're imported, so the tree still connects, but never matched by name, and the preview explains them.
- **Couple events:** these now record `PartnerId`, because two "(Living)" partners share a name. Export and "On this day" use the id.
- **Room for long text:** life-event notes go up to 200,000 characters (one was 20,282 and had been cut at 8,000), and values to 4,000.
- **Export:** a second birth or death entry, an engagement on one person, and "died" implied only by a burial now round-trip unchanged.
- **Reunion family files:** a `.familyfile14` bundle (zipped) or its `familyfile.familydata` is Leister's undocumented binary format. It's recognised on upload, and the admin is told to use File › Export › GEDCOM. Reading it directly isn't attempted: there's no published specification to be correct against.

Timings for the 1,434-person tree on a laptop: about 11 s for the first import, 5 s for a main-tree re-import. The people, tree, on-this-day and person pages each answer in under 60 ms.

