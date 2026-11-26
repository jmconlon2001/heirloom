# Running Heirloom safely

Hosting the family's stories yourself means nobody else can read them, sell them or shut them down. It also means somebody has to look after the server. This page says what Heirloom does for you and what's left to you, in plain words.

## What's exposed to the internet

- **Only the app**, and only through your reverse proxy (Caddy, Nginx Proxy Manager, OPNsense…), which handles HTTPS. See [CADDY.md](CADDY.md).
- **The database, the PDF maker and speech-to-text** sit on a private Docker network (`internal: true`) that can't be reached from outside, and they don't connect to the internet themselves. The one exception: the speech-to-text container downloads its language model the first time.
- The app itself runs as an ordinary user (`PUID`/`PGID`) with no extra Linux privileges. It only starts as root for a moment to fix folder ownership.

## Signing in

- **No passwords** to guess, reuse or leak. People sign in with a one-time link sent to their email, or a printed QR login card that works once. A device then stays signed in for up to 180 days.
- Sign-in attempts are rate-limited (`AUTH_RATE_LIMIT`).
- An admin can see every device that's signed in for each person, and sign out a lost iPad from that person's page.
- Every admin action is written to the **Activity log**.

## In the browser

- A strict Content-Security-Policy: no inline scripts, no third-party scripts, fonts or trackers. The page can't be shown inside another site's frame.
- Cookies are `HttpOnly` and `SameSite=Lax`, and every form is protected against cross-site requests.
- Uploaded files are checked by their contents, not their names. Photos are re-encoded, and their location data is removed from the copies people see.

## The software

- Every build is scanned with Trivy, and the build fails if a fixable high or critical vulnerability is found.
- Tools bundled in the image (restic, rclone) are pinned to exact versions and checked against their published checksums.
- The code is open. Anyone can read exactly what it does.

## Keeping it up to date

The admin home says when a newer version is out. It checks GitHub once a day, sending nothing but the request; set `UPDATE_CHECK=false` to switch that off. To update:

- **OpenMediaVault:** paste the new release's `heirloom.yml` over the old one (or change `APP_VERSION`), then press **Pull** and **Up**.
- **Docker Compose:** set `APP_VERSION` to the new version (or download the new `compose.yml`), then run `docker compose pull && docker compose up -d`.

A full backup is taken automatically before any database change, so an upgrade can always be rolled back. Read the release notes first: anything you need to do by hand is listed under **Upgrading**.

## Customising without a fork

Most families never need to change the code. The questions, chapters and question sets, email wording, reminder schedule, site name, help phone number, starting text size and optional extras are all settings in the app. Because your changes live in your own database, not in a copy of the code, updates never need merging, and nobody else's changes can overwrite yours.

## What's left to you

- **Keep the backup password safe** (`RESTIC_PASSWORD`), in a password manager and somewhere a relative can find it. Without it, the backups can't be opened.
- **Keep an offsite copy** (OneDrive, Google Drive, Backblaze B2…). The **Backups** page says where every copy is and warns if they're all on one disk. See [RESTORE.md](RESTORE.md).
- **Keep the server's own operating system updated**: OpenMediaVault, Proxmox or Ubuntu, as you'd do anyway.
- **Invite only family.** Anyone with an account can read what's shared with "everyone in the family".

## Reporting a problem

If you find a security problem, please don't open a public issue. Use GitHub's **Report a vulnerability** button on the repository's Security tab, so it can be fixed before it's widely known.
