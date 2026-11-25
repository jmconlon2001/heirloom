# Build Prompt — Family Memories (self-hosted)

> Paste this into your VS Code agent (Claude Code / Copilot agent mode) from an empty repo,
> or save it as `docs/BUILD_PROMPT.md` and tell the agent: "Read docs/BUILD_PROMPT.md and execute Phase 0."

HEIRLOOM 
---

## 0. Role and working rules

You are building a self-hosted web app called **Family Memories** (display name configurable via `APP_NAME`).
Family members answer a series of guided prompts about their lives. They write or record answers and attach photos and documents. The finished collection is published as a family website, a shareable PDF, and a print-ready book package.

Working rules:
- Build **one phase at a time** (section 12). At the end of each phase, stop, run every test, update `PROGRESS.md` (what was built, what's left, known issues), and wait for my review.
- Log every non-obvious decision in `docs/DECISIONS.md` (one short ADR-style entry each).
- Use only the dependencies listed in section 2. If you need another one, add it to DECISIONS.md with the reason and its license.
- Prefer boring, readable code over clever code. Usage is family-sized: fewer than 100 users and tens of GB of media.
- Never put secrets in the repo. Everything configurable goes through `.env` (provide `.env.example` with comments).
- The **primary users are older adults (70–90+), many on iPads**. When a UX choice is unclear, pick the option a 85-year-old can use without help.

---

## 1. Goals

1. Admins load 26–52 predefined prompts (seeded below). They can edit, reorder, and assign them, and add custom ones.
2. Each storyteller answers prompts by typing or voice recording, and can attach photos, scans, PDFs, audio, or video.
3. Configurable email reminders bring people back. Each email contains the next prompt and a one-tap link straight to the answer page.
4. Storytellers control who sees each memory. **Admins can always see everything**, and the UI says so plainly.
5. Basic family tree: people, relationships, photos, a tree view, and GEDCOM import/export. Memories can tag people from the tree.
6. Outputs: **(a)** a shareable family website, live and as a static export, **(b)** a PDF, **(c)** a print-ready book package.
7. Ships as a self-contained Docker Compose stack for OpenMediaVault (Compose plugin) or Proxmox (LXC/VM).
8. Automatic, encrypted, restorable backups, with restores tested.

### Explicitly out of scope (v1)
Native mobile apps, social/OAuth login, multi-family tenancy, payments, AI-written stories, rich-text editing, Immich/Google Photos integration, real-time collaboration.

---

## 2. Tech stack (fixed unless I approve a change)

| Concern | Choice |
|---|---|
| Runtime | .NET 10 (LTS), ASP.NET Core **Razor Pages** (server-rendered, progressive enhancement) |
| Front-end JS | Vanilla JS ES modules only where needed (autosave, recorder, uploads, tree view). No SPA, no npm build step; vendor third-party JS into `wwwroot/lib` |
| Database | PostgreSQL 17 (pinned major), EF Core + Npgsql, migrations auto-applied at startup **after a pre-migration backup** |
| Auth | ASP.NET Core Identity + passwordless magic links (primary). Optional password and optional passkeys (.NET 10 Identity) |
| Background jobs | Hangfire + Hangfire.PostgreSql (dashboard at `/admin/jobs`, admin-only) |
| Images | Magick.NET (handles **HEIC from iPhones**, EXIF orientation, resizing) |
| Audio/video | `ffmpeg` in the app image (normalize recordings to AAC `.m4a`, make video poster frames) |
| HTML→PDF | **Gotenberg 8** container (Chromium). Use **Paged.js** in book templates for TOC page numbers, running heads, and recto/verso |
| EPUB / DOCX | `pandoc` CLI in the app image |
| Family tree view | `family-chart` (MIT, vendored) |
| QR codes | QRCoder |
| Email | SMTP via MailKit; Mailpit container in the `dev` compose profile |
| Backups | `restic` + `pg_dump`/`pg_restore` in the app image |
| Logging | Serilog → console (JSON) + rolling file in `/data/logs` |
| Tests | xUnit, Testcontainers (Postgres), Playwright for .NET (e2e), axe-core accessibility checks in e2e |

---

## 3. Users, roles, and privacy

- **User** = someone who can log in. **Person** = a node in the family tree (may be deceased, may be linked to a User).
- Roles (combinable):
  - **Admin**: sees and edits everything, manages users, prompts, reminders, exports, and backups. Every admin view of a private memory is written to the audit log.
  - **Storyteller**: gets assigned prompts and creates memories.
  - **Family (viewer)**: reads what has been shared with them, reacts, comments, and asks follow-up questions.
- Memory visibility (set per memory; the default is configurable, suggested **All family**):
  - `Private`: only me (and admins)
  - `Selected people`: pick from a list of names with photos
  - `All family`: every logged-in user
  - `Link`: tokenized read-only link for relatives without accounts (optional passcode and expiry, revocable)
- Show a short line on every visibility picker: *"Family admins can always see your stories."*
- **Proxy entry ("interview mode")**: a user can write on behalf of a storyteller. The memory is attributed as "Told by Grandma Rose · written by Jacob".
- **Admin "view as"**: a read-only impersonation that shows exactly what a given user sees, used to check sharing settings. Audit-logged.
- Exports **never include `Private` memories** unless an admin ticks an explicit "include private" box, which shows a confirmation.

---

## 4. Accessibility and UX for older users (hard requirements)

- Base font 20px, scalable via a persistent **A / A+ / A++** control. Line height ≥ 1.5. WCAG 2.2 AA contrast minimum, plus a high-contrast toggle.
- Tap targets ≥ 48×48px. Buttons have words, never icon-only. No hover-only interactions, no drag-and-drop-only actions.
- **One prompt per screen.** Layout: prompt in large type, 2–4 gentle hint questions, a big auto-growing text box, then **Record my voice**, **Add photos**, and **Save** (Save is still there even with autosave).
- **Autosave every 5 seconds and on blur**, stored server-side as a draft. Show "Saved ✓ 2:41 PM". No session timeout ever loses typed text: if the session expired, keep the draft in `localStorage` and resubmit after login.
- Plain-text answers only. Paragraphs are preserved, there's no formatting toolbar, and native spellcheck stays on. Add a tip: "You can also tap the microphone on your keyboard to talk instead of type."
- Login: magic link by email (valid 14 days, single-use, then a **remembered device for 180 days**). Admins can print a **login card with a QR code** (a single-use device-enrollment link) to hand to an elder. No CAPTCHA and no password-complexity rules.
- Every page shows a **Need help?** box with the admin's name and phone number (configurable).
- Progress shown gently: "You've shared 12 stories" plus a simple chapter checklist. No streaks, no pressure.
- Every prompt offers **Skip for now** and **I'd rather not answer** without making the user feel guilty.
- Error messages in plain language that say what to do next. Never show stack traces or codes to users.
- Test targets: iPad Safari (primary), iPhone Safari, Chrome and Edge desktop, Android Chrome; at 200% browser zoom and A++ text size.

---

## 5. Memories (answers)

Fields: storyteller, prompt (nullable for free-form "Add a story"), title (defaults to the prompt title), body text, status (`NotStarted | Draft | Complete | Skipped | Declined`), visibility, fuzzy date, place (free text), tagged Persons, attachments, written-by (for proxy entry), version history (keep every saved version, restorable by admins).

- **Fuzzy dates**: year, optional month, optional day, `circa` flag, or decade, plus an optional free-text label ("summer after the war"). Shown as "around 1965", "1970s", "June 1972".
- **Attachments**: photos (jpg/png/heic/webp), PDFs/scans, audio, video. Captions, a per-photo "who's in this photo" picker linked to Persons, and drag-to-reorder **plus** up/down buttons.
  - Validate by magic bytes, not by extension. Limits are configurable (defaults: 25MB per image/PDF, 200MB audio, 1GB video).
  - Store originals untouched. Generate web derivatives (2048px and 400px thumbnails, EXIF-rotated). **Strip GPS EXIF from derivatives** by default.
  - Uploads show a big progress bar and resume after a dropped connection (chunked upload).
- **Voice recording**: in-browser MediaRecorder with a large Start/Stop button and a visible timer and level meter. Safari produces mp4/aac and Chrome produces webm/opus; normalize both to AAC `.m4a` with ffmpeg. A recording can be the whole answer.
- **Follow-up questions**: a family member reading a memory can tap **Ask a follow-up**. That creates a custom prompt assigned to the storyteller ("Your granddaughter Lily asked: …") and emails them on the next reminder cycle.
- **Reactions** (a single "♥") and short comments on shared memories. The storyteller gets an email: "3 family members read your story about the farm."
- **Memories about a person**: any user can write a memory *about* a Person, including deceased relatives. These appear on that Person's page.

---

## 6. Prompts

- Prompts belong to **Chapters**. Prompt fields: title, question text, 2–4 hint questions, suggested attachments, `core` flag, sort order, active flag.
- **Prompt sets**: admins assign a set to each storyteller. Built-in sets are **Core 26** (prompts marked ★ below) and **Full 52**; admins can also build custom sets. Admins can add one-off custom prompts to a specific person.
- **Pacing** per storyteller: "one new prompt per week" (the default; it unlocks progressively) or "all prompts available now".
- Seed data lives in `seed/prompts.json` (idempotent upsert by a stable key). Write 2–4 warm, simple hint questions per prompt in the same plain voice as the prompt.

### Seed prompts (52; ★ = Core 26)

**1. Roots & Early Years**
1. ★ Where and when were you born? Do you know how your name was chosen? *(Upload: baby photo, birth announcement)*
2. ★ Describe the home you grew up in: the rooms, the smells, the sounds, the neighborhood. *(Upload: photo of the house)*
3. ★ Tell us about your parents. What were they like, and what work did they do? *(Upload: photo of your parents)*
4. ★ What is your earliest memory?
5. What do you know about your grandparents and where our family came from? *(Upload: old documents, immigration papers, letters)*

**2. Growing Up**
6. ★ What games did you play, and who were your childhood friends?
7. What chores did you have, and what were the house rules?
8. Tell us about a time you got into trouble as a kid.
9. ★ Which holidays and family traditions do you remember most?
10. ★ What did your family do for fun: trips, Sunday dinners, radio or TV shows?

**3. School Days**
11. ★ Tell us about your schools. Was there a teacher who made a difference? *(Upload: class photo, report card)*
12. Which subjects did you love, and which did you dread?
13. What did you and your friends do on weekends as teenagers?
14. ★ What were you proud of in your school years: sports, clubs, music, a job? *(Upload: yearbook page)*

**4. Becoming an Adult**
15. ★ What was your first job, and what did you do with your first paycheck?
16. When did you leave home, and where did you go?
17. Did you serve in the military or another kind of service? What do you want us to know about it? *(Upload: service photo, discharge papers)*
18. When you were young, what did you dream of becoming?
19. What was your first car, or how did you get around?

**5. Love & Partnership**
20. ★ How did you meet your spouse or partner? What was your first impression? *(Upload: early photo together)*
21. Tell us about your wedding day, or the moment you knew. *(Upload: wedding photo, invitation)*
22. ★ What's the secret to a lasting relationship?
23. Describe your first home together.

**6. Raising a Family**
24. ★ What do you remember about becoming a parent?
25. Share a funny or memorable story about your children when they were young.
26. What was an ordinary week like when the kids were little?
27. ★ What did you most hope to pass on to your children?
28. What was the hardest part of being a parent, and what was the best part?

**7. Work & Purpose**
29. ★ Tell us about your career or main life's work. *(Upload: work photo, ID badge, award)*
30. What accomplishment are you most proud of?
31. Who was a mentor or boss who shaped you?
32. How have you been involved in your community, volunteering, or faith?

**8. Places & Adventures**
33. ★ List the places you've lived, and why you moved each time.
34. ★ What was the best trip or vacation of your life? *(Upload: travel photos, postcards)*
35. What place feels most like home to you, and why?
36. Tell us about a time you were brave or tried something completely new.

**9. History You Lived Through**
37. ★ What world or national event do you remember most vividly? Where were you when it happened?
38. How has everyday life changed since you were young: technology, communication, how people shop and travel?
39. What did things cost when you were young: a movie, gas, a loaf of bread, your first house?
40. Tell us about a hard time our family got through together.

**10. Food, Traditions & Heirlooms**
41. ★ Share a family recipe and the story behind it. *(Upload: photo of the handwritten recipe card)*
42. ★ Tell us the story of an heirloom or treasured object. *(Upload: photo of the object)*
43. What songs, sayings, or expressions did your family always use?
44. How have birthdays and holidays changed from when you were a child to now?

**11. People Who Mattered**
45. ★ Tell us about your brothers and sisters and what growing up with them was like.
46. Who was your best friend over the years?
47. ★ Is there someone you miss? Tell us about them. *(Upload: their photo)*
48. Who in the family do you think you're most like?

**12. Wisdom & Legacy**
49. ★ What's the best advice you ever received, and what advice would you give?
50. ★ What are you most grateful for?
51. ★ What do you want your grandchildren and great-grandchildren to know about you?
52. ★ Write a letter to future generations of our family.

---

## 7. Family tree (basic)

- Person fields: given names, surname, birth surname (maiden name), nickname, sex (optional), fuzzy birth and death dates plus places, living/deceased, profile photo (pick from any attachment), short bio, linked User (optional).
- Relationships: parent–child (biological, adoptive, or step) and partnership (married, partnered, divorced; optional start and end dates).
- Views: interactive tree (family-chart; centered on any Person, tap to re-center, large nodes, zoom buttons rather than relying on pinch), plus a Person page showing photo, dates, relationships, "Stories told by them", "Stories about them", and "Photos they appear in".
- **GEDCOM 5.5.1 import and export** (individuals, families, names, dates, places; ignore sources and citations). Import shows a preview with duplicate detection before committing.
- Privacy: living Persons' birth dates show only the year to non-admins.
- No source citations, DNA, or research-log features.

---

## 8. Email reminders and notifications

- SMTP settings in the admin UI (stored encrypted), pre-filled from `.env`, with a **Send test email** button. Works with a Gmail/Google Workspace app password, SES, Mailgun, or any SMTP relay.
- **Reminder schedule**: global defaults plus per-user overrides.
  - Frequency: weekly / every 2 weeks / monthly / off. Day of week, time of day, and timezone (default `America/New_York`).
  - Quiet period after the user answers something (default: skip the next reminder).
  - Stops automatically when the assigned set is complete. Pause/snooze by one-click link ("Remind me in 2 weeks").
  - **Nudge to admin**: after N reminders with no activity (default 3), email the admin: "Grandma Rose hasn't added a story in 3 weeks — maybe give her a call."
- **Reminder email content**: greeting by name, the **full text of the next prompt and its hints**, one big **Answer this question** button (a signed magic link straight to that prompt, single-use, 14-day expiry), a progress line, and the help contact. No more than one link besides unsubscribe.
- Other emails: invitation, login link, follow-up question received, "family read your story", weekly **"New family stories" digest** for viewers (opt-out), export ready, and admin alerts for backup failures.
- Templates (Scriban or Razor) are editable in admin with live preview and test-send. Every email gets a plain-text alternative and an unsubscribe/preferences link.
- Log every send in an EmailLog table (status, error). Retry failures with backoff via Hangfire.
- Optional: send admin alerts to an **ntfy** topic URL as well (`NTFY_URL`, `NTFY_TOPIC`, `NTFY_TOKEN`).

---

## 9. Outputs and exports

All exports run as Hangfire jobs with a progress bar. They're stored under `/data/exports`, versioned, and downloadable by admins. Each has a **scope**: storytellers, chapters, statuses (default: Complete only), visibility (default: excludes Private), and date range.

### 9a. Family website
1. **Live "Family Library"** inside the app: chapters, storyteller pages, a timeline (memories by fuzzy date), the family tree, people pages, search, and photo galleries with a large-photo viewer. Read-only `Link` shares open this view.
2. **Static site export** (`.zip`): a fully self-contained, offline-capable HTML site with relative links, no server needed, and client-side search via a JSON index. It must work when opened from a USB stick and when deployed to Cloudflare Pages or any static host. Audio and video are included as files.

### 9b. Shareable PDF
- Letter size, RGB, screen-optimized (images ~150dpi). Clickable TOC and bookmarks, chapter dividers, photos with captions, a family tree page (rendered SVG), and an index of people with page numbers.
- Audio and video appear as a poster image plus a **QR code** linking to the clip (a signed `Link` share, `PUBLIC_BASE_URL` + token).

### 9c. Book package (for Lulu, Amazon KDP, Blurb, or a local printer)
A `.zip` containing:
- `interior.pdf`: selectable trim size (6×9, 8×10, 8.5×11). 0.125" bleed toggle, mirrored margins with a gutter, embedded fonts, 300dpi images, recto chapter starts, running heads, folio numbers, title page, copyright page, dedication (editable), TOC, and people index. Page count padded to the printer's multiple (configurable, default 2).
- `cover.pdf`: front, spine, and back as one spread. **Spine width is calculated from page count × paper caliper** (configurable PPI; presets for Lulu and KDP white/cream). Cover title, subtitle, and front/back photos are chosen in admin.
- `book.epub` (EPUB 3) and `book.docx` (editable, via pandoc).
- `source/`: Markdown per memory, `metadata.yaml`, full-resolution images, and `people.ged`, so anyone can re-lay it out elsewhere.
- `preflight.txt`: images below 300dpi at their printed size, missing captions, empty chapters, final page count, spine width.

Templates: a single set of Razor HTML templates and CSS (`/Templates/Export/…`) used by the website, the PDF, and the book, so all three stay visually consistent. Typography: a readable serif for the book body (embed an OFL font such as Literata or Source Serif) and a humanist sans for the web.

---

## 10. Backup and restore

- **Nightly scheduled backup** (time configurable) plus **Back up now** in admin.
  1. `pg_dump -Fc` to a temp file.
  2. `restic backup` of the DB dump, `/data/media`, `/data/keys` (ASP.NET Data Protection keys), and `/data/config`.
  3. Retention via `restic forget --prune` (defaults: 7 daily, 4 weekly, 12 monthly).
- Repositories: a **primary local repo** at `/backups` (map it to a *different physical disk* than `/data`) and an **optional offsite repo** (any restic backend: B2, S3, SFTP, rclone). The repo password comes from `.env`; on first run, show a one-time banner telling the admin to store it in a password manager.
- **Weekly verify job**: `restic check --read-data-subset=5%`, plus a test restore of the latest DB dump into a throwaway database with row-count sanity checks. Email/ntfy the admin on any failure, and show the last-success timestamp prominently on the admin dashboard.
- **Restore from the admin UI**: pick a snapshot → confirm by typing `RESTORE` → app enters maintenance mode (friendly page for users) → takes a safety backup of the current state → restores the DB and files → runs migrations → exits maintenance mode.
- **Disaster restore CLI** `scripts/restore.sh` for a fresh host. It takes the repo path or URL, password, and snapshot ID, brings up only `db`, restores everything, then starts the stack. Document the steps in `docs/RESTORE.md`.
- **Portable archive**: admins can download a full `.zip` (JSON of all data, media, GEDCOM, README) that doesn't depend on this app, for long-term preservation.
- Automatic backup before every migration and before every UI restore.
- An integration test must cover: seed data → backup → wipe volumes → restore → checksums and row counts match.

---

## 11. Deployment

### Containers (`compose.yml`)
- `app`: the .NET app (non-root, `PUID`/`PGID` support, includes ffmpeg, pandoc, restic, postgres-client). Healthcheck at `/healthz` (liveness) and `/readyz` (DB + Gotenberg reachable).
- `db`: `postgres:17`, internal network only, healthcheck.
- `gotenberg`: `gotenberg/gotenberg:8`, internal network only, memory limit 1GB (target hardware includes an Intel N100).
- `mailpit`: `dev` profile only.
- One exposed HTTP port (`APP_PORT`, default 8095). **No TLS in the stack**: TLS is terminated by an existing **Caddy reverse proxy on OPNsense**. Honor `X-Forwarded-For` and `X-Forwarded-Proto` (configure ForwardedHeaders with a `TRUSTED_PROXIES` env list). All absolute links use `PUBLIC_BASE_URL` (e.g. `https://memories.example.com`).
- **Persist Data Protection keys** to `/data/keys`, or auth cookies and magic links will break on every container restart.
- Volumes: `${DATA_ROOT}/data` → `/data` (media, keys, logs, exports, config), `${DATA_ROOT}/postgres`, and `${BACKUP_ROOT}` → `/backups`.
- Images are built from a multi-stage Dockerfile (amd64). Pin every image tag. Also provide a GitHub Actions workflow that builds and pushes to GHCR on tag.

### OpenMediaVault (Compose plugin) layout
- Stack folder `/appdata/family-memories/` containing `family-memories.yml` and `compose.override.yml` (the plugin's convention), plus `.env`.
- `DATA_ROOT` on the data disk and `BACKUP_ROOT` on a *different* disk.

### Proxmox alternative
- Document both: (a) an unprivileged Debian 13 LXC with `nesting=1` running Docker, data on a ZFS dataset bind-mounted in; (b) a small Debian VM. Note the UID mapping for bind mounts in unprivileged LXCs.

### Docs to produce
`README.md` (quick start), `docs/DEPLOY_OMV.md`, `docs/DEPLOY_PVE.md`, `docs/CADDY.md` (an example reverse-proxy block), `docs/RESTORE.md`, `docs/ADMIN_GUIDE.md`, and a **one-page printable `docs/HELP_FOR_STORYTELLERS.pdf`** in large type with screenshots (how to log in, answer, record, add a photo).

---

## 12. Phases (stop and report after each one)

**Phase 0 — Foundation**
Solution layout (`src/App`, `src/App.Tests`, `src/App.E2E`), compose stack, `.env.example`, EF Core + Postgres, Identity with magic-link login, remembered devices, persisted Data Protection keys, Serilog, health checks, base layout with the A/A+/A++ control and high contrast, CI build.
*Done when:* `docker compose up` gives a working magic-link login (via Mailpit) behind a reverse proxy at a sub-domain.

**Phase 1 — Prompts and memories (the core loop)**
Seed chapters and prompts, prompt sets and assignment, the one-prompt-per-screen answer page with autosave, uploads (HEIC→JPEG derivatives, chunked, progress), voice recording + ffmpeg normalization, fuzzy dates, version history, storyteller home with progress, admin user management and invites, printable QR login cards, proxy entry, audit log.
*Done when:* an e2e test on an iPad-Safari viewport completes a prompt with text, a photo, and a voice clip, and the axe check passes.

**Phase 2 — Backup and restore** *(before real family data goes in)*
Everything in section 10, including the restore integration test and `docs/RESTORE.md`.

**Phase 3 — Sharing and family tree**
Visibility levels, link shares, "view as", the Family Library (live), reactions, comments, follow-up questions, Persons/relationships, tree view, person pages, photo tagging, GEDCOM import/export.

**Phase 4 — Email reminders and notifications**
SMTP settings, schedules and overrides, reminder emails with prompt text and a deep link, nudge-to-admin, digest, editable templates, EmailLog, ntfy.

**Phase 5 — Exports**
Shared templates, static site zip, PDF, book package with preflight, portable archive.

**Phase 6 — Optional extras (each behind a feature flag; build only the ones I confirm)**
- Local speech-to-text for voice answers via a `whisper` container (faster-whisper, CPU, `small` model) in a compose profile. The transcript is shown for the storyteller to approve or edit before it becomes the text.
- **Answer by email reply**: poll an IMAP mailbox; a reply to a reminder becomes a draft for that prompt (text + attached photos), and the storyteller or admin approves it.
- **Paper packets**: a printable PDF of assigned prompts with writing lines, for storytellers who won't use a device. The admin scans the pages back in and attaches them via proxy entry.

---

## 13. Quality bar

- Unit tests for domain rules: visibility resolution, reminder scheduling (including DST changes), fuzzy-date formatting, spine-width math, and export scope filtering.
- Integration tests with Testcontainers for Postgres, backup/restore, and Gotenberg rendering of a small book.
- Playwright e2e for: magic-link login, answering a prompt, sharing to one person, the viewer seeing only what they should, and admin seeing everything. Run them at the iPad viewport and at A++ text size, with axe and **zero serious/critical violations**.
- Security: antiforgery on all forms, login and magic-link endpoints rate-limited, signed and expiring tokens stored hashed, upload validation, authorization checks in the query layer (not just the UI), security headers (CSP without `unsafe-inline` scripts), and Hangfire dashboard locked to admins.
- Performance target on an Intel N100: answer page TTFB under 300ms; a 100-memory book PDF renders in under 5 minutes.