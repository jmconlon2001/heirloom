# Family admin guide

You're the family admin: the helpful person who sets things up, invites everyone, and answers the "Need help?" phone. This guide covers the day-to-day. Everything is under **Family admin** on your home page.

## The first week

1. **Backups:** Family admin → **Backups** → **Back up now**. Save the backup password (`RESTIC_PASSWORD`) in a password manager *and* somewhere a trusted relative can find it. Without it, backups can never be opened.
2. **Email:** Family admin → **Email** → **Send test email**. Check the spam folder if it doesn't arrive.
3. **Your "Need help?" number:** Family admin → **Site name, help phone number and display**. Type the name storytellers know and the number to ring; it appears on every page, in every email and on login cards. (`HELP_NAME` and `HELP_PHONE` in `.env` work too; the page wins.) The same page sets the site's name and the text size and contrast a new iPad starts with, so a family of mostly A++ readers can start everyone at A++.
4. **Invite the storytellers:** People → **Invite someone**. Choose **Core 26** (a gentle start) or **Full 52**, and **one new question each week**.
5. **For anyone who struggles with email:** open their page and **Print a login card**. Pointing the iPad's camera at the QR code signs them in, and the iPad then remembers them. A card works once and expires after 30 days, so treat it like a house key.
6. **Invite the readers** (children, grandchildren, cousins) with just the **Family** role.

## Simple or Detailed view

At the top of every page, next to Text size, there's a **View** switch:
- **Simple** (everyone starts here) is large and calm: the top menu, and one thing at a time.
- **Detailed** looks like a typical web app: a sidebar with every section (and all the admin pages for you), smaller text, and compact tables. Your admin home becomes a dashboard with numbers, the health of each part, and recent activity.

Each person's choice is remembered for their account on every device, and doesn't change what anyone else sees. Grandchildren who like a busier screen can pick Detailed. Storytellers can simply leave it on Simple.

## Is everything working?

**System status** (on the admin page, or `/status`) checks the website, database, PDF maker, backups, email, background tasks, disk space and (if on) speech-to-text. Anyone can open `/status` and see whether each part is working. Only admins see the details.

For a monitor such as Uptime Kuma, point it at `/api/status`. It answers **503** when something is broken. Set `STATUS_TOKEN` in `.env` and have the monitor send `Authorization: Bearer <token>` to get the details as JSON.

## People

| Role | Can |
|---|---|
| **Storyteller** | answer their questions, write their own stories, choose who sees each one |
| **Family** | read what's shared with them, ♥, leave notes, ask follow-up questions, add to the family tree |
| **Admin** | everything, including seeing *every* story (the app tells people this on every sharing choice) |

On a person's page you can also:
- **Write a story for** them: for when you sit together and type while they talk. The story says "Told by Rose · written by you".
- **People who can write for** them: let a grandchild do the same.
- **Questions just for** them: one-off questions that go to the top of their list.
- **Reminder emails**: their own day, time, time zone or frequency.
- **Devices signed in**, with **Sign out this device**: for a lost iPad.
- **Pause** the account: they can't sign in, and nothing is deleted.
- **See the site as** them: check exactly what they can see. It's read-only, and it's recorded in the activity log.

### If the family tree lives in a genealogy program

Heirloom isn't a genealogy program, and it doesn't try to replace one. If someone in the family keeps the tree in Reunion, RootsMagic, Family Tree Maker, Gramps or similar, that stays the master copy:

1. In the genealogy program, export a GEDCOM file of the whole tree.
2. Family admin → **Import or export the family tree** → choose the file → check the list.
3. Tick **This file is our main family tree** and press **Import**.

Each person's page then shows their **life events** (residences, occupations, immigration, military service, burial…) with the **sources** for each one, the program's notes, and links such as their FamilySearch record. Do it again whenever the tree has grown, once a year is plenty: people are matched by their id in the file, names, dates, events and sources are updated, and stories, photos, accounts and links you added by hand stay as they are.

If the tree is also published on the web (like a Reunion website), put its address in **Site name, help phone number and display** → **The family's main tree**; it's shown on the family tree and People pages. Any person's page can have its own links too (Edit → **Links**): their page on the family website, a Find a Grave memorial, an obituary.

### The family archive

Family Library → **Archive** is for things that aren't stories: letters, certificates, newspaper clippings, album pages, recordings. Anyone in the family can add to it. Whoever added something, and admins, can change or remove it. Admins make the folders.

- **Add to the archive:** choose the scans (every page of a letter at once) and give it a title. If you can, add when it's from, what it says (a transcription) and **where the original is now**. The originals are kept exactly as uploaded, and they're in every backup and in the archive export.
- **A box of photos or a photo CD:** tick **Each file is its own item**. Each photo is named from its own caption, dated from when it was taken, and filed under its keywords. Keywords from Lightroom, Apple Photos or digiKam keep their hierarchy (Family › Conlon › William), and **Browse by keyword** walks it. The Photos page has the same keyword browser for photos in stories.
- **Link it to a story** from the item's page. The story then lists it under **Related documents**.
- The family website export has an **Archive** page, and the complete archive export includes everything with its folders.

### Formatting and photos in the middle of a story

In Detailed view, and whenever you write for someone, the story box has a small toolbar: **Bold**, *Italic*, **Heading**, **List**, **Quote** and **Link**. It adds plain marks you can see (`**bold**`, `## Heading`, `- item`), so the words always read well as plain text. Under each photo or recording, **Place in the story** puts it where your cursor is; anything not placed still shows after the story. Simple view keeps the plain box, so storytellers see no change. The PDF, the book, the website and the e-book all follow the formatting and the placement.

### Raw material

Next to the story box (in Detailed view, or when writing for someone) is **Raw material**: the words as they first arrived, kept exactly as they were. Email replies, what speech-to-text heard before anyone corrected it, and imported spreadsheet text are kept there automatically. You can add your own too, like the transcription of a letter. **Copy into the story** what you need, then tidy it there. Only people who can edit the story see the raw material, and it's included in the complete archive export.

### Adding many people or stories at once

If you already have the family in a spreadsheet, or stories typed up somewhere, you don't have to add them one at a time. Save the spreadsheet as **CSV** (in Excel: File → Save As → CSV UTF-8; in Google Sheets: File → Download → CSV; in Numbers: File → Export To → CSV). Each import page has a ready-to-fill spreadsheet to download, and nothing changes until you've checked the list and pressed the button.

- **Family admin → Add people from a spreadsheet:** one person per row. Everyone goes into the family tree, with their **Parents** and **Partner** joined up by name. Anyone with an **Email** also gets an account (**Role**: Storyteller, Family or Admin; Family if blank), linked to their place in the tree. Invitations are only emailed if you tick the box; otherwise send them later from each person's page. Someone who looks like a person already in the tree is offered as "Same person as …", and then only their blanks are filled in.
- **Family admin → Add stories from a spreadsheet:** one story per row. **Storyteller** (their name or email; they must be on the People list) and **Story** are needed. **Question** makes it their answer to a question on the list; **When**, **Place**, **People** (names in the tree), **Written by**, **Status** (Finished or Draft) and **Who can see** (Everyone or Only me) are optional. Stories that look like they're already here are set to Skip. No emails are sent.

Dates can be written the usual ways: 1931, about 1931, the 1930s, June 1931, June 12, 1931, 6/12/1931 (month first) or 1931-06-12. A genealogy program's GEDCOM file goes in **Import or export the family tree** instead.

## Questions

Family admin → **Questions and chapters**: edit wording, reorder, turn questions off, or add your own. Under each chapter, **Change this chapter** renames it or moves it up or down. **Add a chapter** at the bottom starts a new one; then use **Add a question** and choose it. A chapter you added can be removed once it's empty. The built-in chapters can be renamed and moved, but not removed. **Question sets** lets you hand-pick a set for one person. Your edits are never overwritten by app upgrades.

## Reminders

Family admin → **Email and reminders → Reminder schedule**. The default is Sundays at 10 AM. Someone who has just written something skips the next reminder, so the emails never nag. After three reminders with nothing written, you'll get a gentle email suggesting a phone call. Each email has a settings link where people can **Remind me in 2 weeks** or stop the emails.

**Email wording** lets you change any email's words, with a live preview. **Sent emails** shows every email and whether it was delivered, with **Try again** for any that failed.

## Privacy, in plain words

- Each storyteller chooses, for every story: **All the family**, **Only certain people**, **Only me**, or **Anyone I send a special link to**.
- Unfinished stories are only seen by the storyteller, their helpers and admins.
- **Admins can always see everything.** Opening a story you couldn't otherwise see is written to the **Activity log**.
- Living people's birth dates show only the year to everyone except admins, and in every export.

## Exports

Family admin → **Exports**:
- **A PDF to share:** Letter-size, with photos, the family tree, and QR codes that play the recordings.
- **A book to print:** a zip for Lulu, Amazon KDP or a local printer. Read `preflight.txt` first: it lists photos that may print blurry, photos without captions, the page count and the spine width. Always check the printer's own cover calculator and preview before ordering.
- **A family website:** a zip; unzip it onto a USB stick and give one to each household. It works with no internet.
- **A complete archive:** everything, in open formats, to keep for decades.

Private stories are left out unless you tick the box and confirm.

## Backups and restoring

See [RESTORE.md](RESTORE.md). In short:
- Backups run every night and are checked every Sunday; you're emailed if anything fails.
- To undo a mistake: Backups → pick a backup → type `RESTORE`. A safety backup is taken first, so even a restore can be undone.
- Once or twice a year, practise a full restore onto a spare machine.
- The **Backups** page lists exactly what each backup held and every place a copy is kept.

### Back up to OneDrive or another cloud drive

A second copy away from the house survives a fire, a theft or a dead disk. OneDrive, Google Drive, Dropbox, Box and pCloud all work through **rclone**, which is built into the app.

1. On any computer with a web browser, install rclone (rclone.org/install) and run `rclone config`. Choose **n** (new remote), name it `onedrive`, pick **Microsoft OneDrive**, and sign in when the browser opens. Accept the defaults.
2. Find the file it wrote (`rclone config file` prints where) and copy it to the server as `DATA_ROOT/data/config/rclone.conf`. It holds the sign-in token, so treat it like a password. It's included in the encrypted backups.
3. In the environment, set `OFFSITE_RESTIC_REPOSITORY=rclone:onedrive:heirloom-backups` and restart the app.
4. Backups → **Back up now**. The "Where the backups are kept" table should list **OneDrive (through rclone)**, and the result should say "and to rclone:onedrive:heirloom-backups".

The copy is encrypted with the same password before it leaves the house, so Microsoft can't read it. To restore from it on a new server, see "Disaster restore" in RESTORE.md (copy `rclone.conf` across first).

### A plain copy you can open without the app

Set `PLAIN_COPY=true` to also keep `BACKUP_ROOT/plain`, refreshed every night: the whole database as `heirloom.sql.gz` and a mirror of every photo, recording and document. Anyone can open it, and you can `rsync` it to another machine. It isn't encrypted and has no history, so keep it private and keep the regular backups too.

## Optional extras

Speech-to-text and paper packets are on unless you switch one off with `FEATURE_…=false` in `.env`; answering by email is off until you choose it. Family admin → **Optional extras** shows whether each one is working, or what it still needs:
- **Write down voice recordings** (speech-to-text on your own machines). Works straight away with the stack's `whisper` container.
- **Answer by replying to the email.** Off by default. Set `FEATURE_EMAIL_REPLIES=true`, plus `REPLY_ADDRESS` and the mailbox's `IMAP_*` settings.
- **Paper question packets** for storytellers who won't use a device. Works straight away.

### Speech-to-text on a Mac

The `whisper` container runs on the server's processor. An N100 takes a few minutes for a 5-minute recording. A Mac with Apple silicon (a Mac mini, for example) does the same job in seconds on its GPU:

1. On the Mac, install whisper.cpp and download a model:
   `brew install whisper-cpp`
   `curl -L -o ~/ggml-large-v3-turbo.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin`
2. Run its server so other machines on your network can reach it:
   `whisper-server -m ~/ggml-large-v3-turbo.bin --host 0.0.0.0 --port 8178 --inference-path /v1/audio/transcriptions --convert`
   (`--convert` needs ffmpeg: `brew install ffmpeg`.)
3. In `.env` on the server, set `WHISPER_URL=http://<the Mac's address>:8178/v1`, then restart the stack.
4. Optional: stop the server's own `whisper` container by adding this to `compose.override.yml`:
   ```yaml
   services:
     whisper:
       profiles: [off]
   ```

Any OpenAI-compatible speech-to-text server works the same way: end the address in `/v1`, and set `WHISPER_MODEL` if the server wants a model name. Ollama can't do this, because it doesn't offer speech-to-text. Keep the Mac on your home network; recordings should never go over the internet.

## When someone calls for help

| They say | Try |
|---|---|
| "The link doesn't work" | Links work once and last 14 days. Have them tap **Sign in** and ask for a new one, or print a login card. |
| "I lost what I wrote" | It's almost certainly saved: open their story (People → their name → Write a story for). Admins can restore any earlier version from **Earlier versions** on the story. |
| "The writing is too small" | The **A++** button at the top of every page, and **High contrast**. |
| "I don't want to answer that one" | **I'd rather not answer** at the bottom of the question. It won't come back. |
| "It keeps emailing me" | The settings link at the bottom of the email, or change their schedule on their page. |
