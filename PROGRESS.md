# Progress

## Phones, extras on by default, Whisper on a Mac (2026-11-26, v0.8.0)

- **Detailed view on phones** (D-069): the sidebar folds into a "Menu: <current page>" button at 768px and narrower. The header is tighter, and dashboard tables scroll inside themselves. Every reachable page was crawled as admin, family member and storyteller at 320, 390 (normal and largest text), 768, 820 and 1280px, with no sideways scrolling.
- **Extras on by default** (D-070): speech-to-text (the `whisper` container is now part of the standard stack) and paper packets. Answering by email stays off until `FEATURE_EMAIL_REPLIES=true`.
- **Whisper anywhere:** a `WHISPER_URL` ending in `/v1` talks to OpenAI-compatible servers, such as whisper.cpp's `whisper-server` on a Mac mini (ADMIN_GUIDE → "Speech-to-text on a Mac").
- **Tests (222):** an iPhone e2e test (folded menu, no sideways scroll, axe), feature defaults, the OpenAI-style Whisper request. A flaky photo-load check now waits for the image.

## Status, admin views and fixes (2026-11-25)

- **Status page and API** (D-066): `/status` in plain language for anyone, with details for admins; `/api/status` JSON for monitors (`STATUS_TOKEN`, 503 when something is broken). The version is baked into release images.
- **Simple / Detailed view** (D-067): a switch in the header on every page for everyone signed in, remembered per person; Simple is the default. Detailed adds a sidebar with every section (plus the admin sections for admins), denser type and tables, and an admin dashboard.
- **Landing page:** a "Run it for your own family" section linking to GitHub (`PROJECT_URL`), and a link to the status page.
- **Fixed:** "Print a login card" returned a 400 for everyone (no anti-forgery token on that form). There's a new guard test for every POST form (D-068).
- **Tests:** status API (public, admin, token, wrong token, family member, 503), status page, GitHub link, view switch (on every page, per person, across devices, family members too, no admin sections for them, no off-site redirect, not for signed-out visitors), and an iPad e2e test with axe on both views (admin home, people, Family Library) and the status page.

## Beta release (2026-11-25)

- **Landing page** at `/` for signed-out visitors (D-064), checked on iPad with axe.
- **Demo mode** (`DEMO_MODE`, D-065): a sample family plus one-tap sign-in as the admin, Grandma Rose or Lily, for beta testers.
- **Docs:** a rewritten README with screenshots; [BETA_TESTING.md](docs/BETA_TESTING.md) (admin, family member and storyteller walkthroughs, plus a feedback template); [DEPLOY_OMV.md](docs/DEPLOY_OMV.md); [DEPLOY_PVE.md](docs/DEPLOY_PVE.md); [ADMIN_GUIDE.md](docs/ADMIN_GUIDE.md); and a one-page large-type [HELP_FOR_STORYTELLERS.pdf](docs/HELP_FOR_STORYTELLERS.pdf).
- **README screenshots** retaken from demo mode (no test data): storyteller, family and admin views.
- **Demo photos** now look like old sepia snapshots (farmhouse, county fair, wedding, recipe card) instead of flat yellow drawings.
- **Fixed:** Photos-page and person-page thumbnails were stretched to 400px tall on iPad (the `height` attribute beat `aspect-ratio`); there's now an e2e check that they're square.
- **Tests:** demo seeding, the demo buttons work only with the flag on, a non-helper can't see Rose's private demo story, and the landing page (anonymous, no redirect, axe on iPad).

## Phase 6: Optional extras ✅ (2026-11-25), all built, each off by default

- **Speech-to-text** (`FEATURE_TRANSCRIPTION`, D-060): a local Whisper container (compose profile `transcription`). The storyteller checks and edits the words before they join the story.
- **Answer by email reply** (`FEATURE_EMAIL_REPLIES`, D-061): plus-addressed Reply-To, IMAP polling, sender check, quoted text stripped, photos attached, a draft with a review banner, and a list on the admin home.
- **Paper packets** (`FEATURE_PAPER_PACKETS`, D-062): a printable letter plus one question per page with writing lines; scans come back through "Write a story for …".
- **Family admin → Optional extras** explains each one and how to switch it on.
- **Tests (207):** reply parsing, Whisper HTTP contract, transcript approval through the page, the full reply flow (Reply-To in the reminder, poll, wrong sender rejected, draft with photo, banner, admin list, finish clears it), and a packet PDF from real Gotenberg (1 + 12 pages).
- **Fixed:** text typed before the page script loaded wasn't autosaved (D-063).


## Phase 5: Exports ✅ (2026-11-25)

**Built** (admin **Exports** page: one form, Hangfire job, progress bar, versioned files in `/data/exports`, an "export is ready" email, audited admin-only downloads):
- **Scope:** storytellers, chapters, finished or also unfinished, private only with a tick plus confirmation, and a year range.
- **Family website** (zip, D-056): stories, timeline by decade, photos, people, the family tree as SVG, and offline search. It works from a USB stick and on any static host; photos, recordings and videos are included.
- **Shareable PDF** (D-052): US Letter; contents page with page numbers; PDF bookmarks; section dividers; photos with captions and who's in them; QR codes and links for recordings (D-055); family tree page; people index with page numbers.
- **Book package** (zip, D-053/D-054/D-057):
  - `interior.pdf`: trim size, bleed, mirrored margins and gutter, 300 dpi images, right-hand chapter starts, running heads, page numbers, title/copyright/dedication/contents pages, people index, padded to the printer's multiple;
  - `cover.pdf`: back + spine + front spread, spine from page count and paper;
  - `book.epub` and `book.docx` (pandoc);
  - `source/`: Markdown per story, `book.md`, `metadata.yaml`, full-resolution images, `people.ged`;
  - `preflight.txt`: low-dpi photos at printed size, missing captions, empty chapters, page count, spine and cover size.
- **Portable archive** (D-058): data.json, all media, GEDCOM, README.
- The app itself now uses the vendored Atkinson Hyperlegible and Literata fonts (D-059).

### Tests: 201 passing
- **Unit:** spine width (KDP white/cream), cover size, page padding, printed-size dpi, tree SVG (living people shown with year only).
- **Integration with a real Gotenberg 8 container:**
  - scope rules;
  - a shareable PDF (Letter MediaBox, outline, page count, a clip link created, the ready email);
  - a book package (every file present; 6×9 interior; page count a multiple of 4; the cover's measured width equals 12 in + pages/400 + 0.25 in; preflight numbers; EPUB is a zip; no private story in the Markdown);
  - a website (relative links only, no modules, every referenced image or audio file inside the zip, search index, no private stories);
  - the archive (all stories including private ones, every original present);
  - admin form, private confirmation, download permissions.
- **e2e:** the Exports page on iPad (axe, no sideways scroll, local font loaded).
- **Checked by eye:** pages of a rendered book (contents with page numbers, story, photos, QR card, tree, index, running heads and page numbers).

### Bugs found and fixed
- Paged.js never finished (external CSS from `file://`; a `:first-of-type` target-counter rule broke its parser; CSS columns).
- `leader()` silently removed the contents page numbers.
- A diagnostic probe found these in minutes, and now Gotenberg fails fast on any JavaScript error (D-052).


## Phase 4: Email reminders and notifications ✅ (2026-11-25)

**Built:**
- **SMTP settings** in the admin UI (D-047): encrypted password, pre-filled from `.env`, **Send test email**.
- **Reminders** (D-048):
  - family defaults plus per-person overrides (frequency, day, time, time zone);
  - DST-safe schedule; quiet period; stops when everything is answered; waits for next week's question;
  - snooze and pause by link (D-049); nudge to admins plus ntfy.
- **The reminder email:** greeting, the full question and hints, one **Answer this question** button that signs in and opens it, a progress line, and the help contact. Follow-up questions arrive the same way.
- **Other emails:** invitation, sign-in link, the "family read your story" summary, the weekly digest, admin alerts, and the test email. "Export ready" lands with Phase 5. (D-050)
- **Templates** (Scriban, D-045): every email's wording is editable in admin, with a preview, a test send and a reset. Every message has a plain-text part and an email-settings/unsubscribe link, plus one-click unsubscribe headers on bulk mail.
- **EmailLog** with retries and back-off through Hangfire, and a **Sent emails** page with **Try again** (D-046).
- **Home** shows "Next question by email: Sunday, November 29 at 10:00 AM · Email settings".

### Tests: 186 passing
- **Unit:** weekly and monthly slots, both New York DST changes (the skipped 02:30 and the repeated 01:30), due rules, next occurrence.
- **Integration:**
  - The reminder arrives at the right time, once, with exactly one link besides settings; that link signs in and opens the question.
  - The quiet period skips exactly one week.
  - Snooze and stop from the link without signing in; RFC 8058 one-click unsubscribe; stops when everything is done.
  - The nudge comes once, after 3 reminders.
  - A follow-up arrives as "Lily asked you a question".
  - Read summary once; the digest honours visibility and opt-out.
  - Template edit, HTML-escaping, broken-template refusal, preview CSP and admin-only, reset.
  - SMTP password stored encrypted and kept when the field is left empty; a failure is logged, then retried successfully.
  - Per-person schedule shown on the home page.
- **e2e:** the email settings page on iPad (axe, tap targets, snooze); the admin wording editor's live preview (axe).

### Bugs found and fixed
- The quiet period silenced every later reminder.
- The nudge's "N weeks" was wrong.
- `LowercaseUrls` broke the email settings token in form posts.
- With Hangfire on, sign-in emails arrive a moment later; the test helper now waits.


## Phase 3: Sharing and family tree ✅ (2026-11-25)

**Built:**
- **Visibility** (D-035): Private / Selected people (names with photos) / All family / Link.
  - Every picker says "Family admins can always see your stories."
  - Drafts stay with the storyteller until finished.
  - Enforced in the query layer for pages, API, media, Library, search, people and tree.
  - Admin views of stories outside the normal rules are audited.
- **Special links** (D-037): optional passcode and expiry; turn off any time; QR code shown on creation; view counts; a read-only public view, with media served only through the link.
- **Admin "view as"** (D-036): read-only, with a banner and audit.
- **Family Library:**
  - newest stories, by chapter, storytellers, a timeline by decade;
  - a photo grid with a large viewer (Previous / Next buttons);
  - full-text search (D-043);
  - newest stories also on the home page.
- **Conversation** (D-038): ♥ with names ("Lily and Ed love this"), notes, removing notes, **Ask a follow-up** (first on the storyteller's list as "Lily asked:"), and read tracking.
- **Family tree** (D-039, D-041):
  - People and relationships: parents (biological, adoptive, step), partners (married, partnered, divorced).
  - Cycle protection.
  - Person pages: photo, life dates (year-only for living people unless you're an admin), close family, "Stories told by them", "Stories about them", "Photos they appear in", **Write a story about …**.
  - Profile photo picked from photos they're tagged in.
  - Tree view: family-chart with large high-contrast cards, tap to re-centre, zoom buttons, and an accessible close-family list.
- **Tagging** (D-040): people in a story, and who's in each photo.
- **GEDCOM 5.5.1** (D-042): import with a duplicate-detection preview and per-person choices; export.

### Tests: 165 passing
- **Integration:**
  - the visibility matrix through pages, media, Library and search;
  - admin audit; the picker wording; selected-people sharing through the form; drafts kept private;
  - view-as (sees what Ed sees, can't post, Stop works);
  - special links (passcode, wrong passcode, expiry, revoke, media only through the link, dead when visibility changes);
  - hearts, notes, delete permissions, follow-ups, reads;
  - GEDCOM: parsing, dates, names, round trip, duplicate rules, and the import UI end to end;
  - people pages (living privacy, admin sees full dates, write-about, photo tags, person sections);
  - tree CSP and JSON; the ancestor-cycle guard; admin-only delete and account linking.
- **e2e:**
  - on iPad, a family member: home, Library, story, ♥, note, photo viewer, timeline, search, tree (zoom, re-centre), person page, with axe at each step;
  - a relative opening a passcode link;
  - admin view-as.

### Bugs found and fixed
- Autosave was rejected once it sent `visibility` as a name (the JSON API now accepts enum names).
- The Library chapter query couldn't be translated to SQL.
- family-chart failed contrast, and the app's `.card` style leaked into the tree.
- Two e2e cross-talk flakes (D-044).

### Not done
- Choosing a different profile photo straight from the photo viewer.
- Editing tree relationships from the diagram itself (it's done on the person's Edit page).
- Merging two people who are already in the tree.


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
