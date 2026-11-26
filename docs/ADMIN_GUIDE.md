# Family admin guide

You're the family admin: the helpful person who sets things up, invites everyone, and answers the "Need help?" phone. This guide covers the day-to-day. Everything is under **Family admin** on your home page.

## The first week

1. **Backups:** Family admin → **Backups** → **Back up now**. Save the backup password (`RESTIC_PASSWORD`) in a password manager *and* somewhere a trusted relative can find it. Without it, backups can never be opened.
2. **Email:** Family admin → **Email** → **Send test email**. Check the spam folder if it doesn't arrive.
3. **Your "Need help?" number:** set `HELP_NAME` and `HELP_PHONE` in `.env`. They appear on every page and in every email.
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
