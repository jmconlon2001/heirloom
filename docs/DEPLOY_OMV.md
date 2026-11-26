# Deploying on OpenMediaVault (Compose plugin)

This uses OMV 7's **Compose** plugin (`openmediavault-compose`). You'll need about 2 GB of RAM free for the app, database and PDF renderer (4 GB with speech-to-text).

## 1. Disks and folders

Use **two different physical disks**: one for the family's data, one for backups. A backup on the same disk doesn't survive that disk failing.

- **Data disk**: create a shared folder, e.g. `appdata`. `DATA_ROOT` will be `/srv/dev-disk-by-uuid-XXXX/appdata/family-memories`.
- **Backup disk**: create a shared folder, e.g. `backups`. `BACKUP_ROOT` will be `/srv/dev-disk-by-uuid-YYYY/backups/family-memories`.

Find your user's ids under **Users → Users** (or `id <user>` over SSH). Most OMV setups use `PUID=1000`, `PGID=100`.

## 2. The stack

1. **Services → Compose → Settings**: set the shared folder where compose files live (e.g. `appdata/compose`).
2. **Services → Compose → Files → Create**:
   - **Name:** `family-memories`. Use exactly this name: the plugin saves the environment as `<name>.env`, and the compose file reads `family-memories.env` (or `.env`). If you've already used another name, such as `heirloom`, link the file once over SSH: `ln -s heirloom.env .env` in the compose folder.
   - **File:** paste the contents of `family-memories.yml` from the latest [release](https://github.com/jmconlon2001/heirloom/releases). It pulls `ghcr.io/jmconlon2001/family-memories:<version>`, so no building is needed.
   - **Environment:** paste `env.example` from the same release and fill it in (each line is explained). At minimum:

     ```dotenv
     PUBLIC_BASE_URL=https://memories.example.com
     ADMIN_EMAIL=you@example.com
     POSTGRES_PASSWORD=<openssl rand -base64 32>
     RESTIC_PASSWORD=<openssl rand -base64 32>   # store this in your password manager!
     DATA_ROOT=/srv/dev-disk-by-uuid-XXXX/appdata/family-memories
     BACKUP_ROOT=/srv/dev-disk-by-uuid-YYYY/backups/family-memories
     PUID=1000
     PGID=100
     SMTP_HOST=smtp.gmail.com   # plus SMTP_USERNAME / SMTP_PASSWORD (a Gmail app password)
     TRUSTED_PROXIES=192.168.1.1   # your OPNsense address
     ```
3. If the image is private: SSH in and run `docker login ghcr.io` once, with a GitHub token that has `read:packages`.
4. Select the file and press **Up**. The first start takes a minute: it creates the database and seeds the 52 questions.
5. Check: `curl http://<omv-ip>:8095/readyz` should answer `Healthy`.

The plugin also lets you keep a `compose.override.yml` next to the file. Use it for local tweaks (for example a different `APP_PORT` mapping) so release upgrades don't overwrite them.

## 3. HTTPS

Point your reverse proxy at `http://<omv-ip>:8095`: see [CADDY.md](CADDY.md) for Caddy on OPNsense.

## 4. First sign-in

Open `PUBLIC_BASE_URL`, tap **Sign in**, type `ADMIN_EMAIL`, and tap the link in the email. Then:

1. **Family admin → Backups**: press **Back up now**, and confirm you've saved the backup password.
2. **Family admin → Email**: send a test email.
3. **People → Invite someone**.

## Upgrading

1. Download the new release's `family-memories.yml` and paste it over the old one, or just change `APP_VERSION` in the environment.
2. Press **Pull**, then **Up**.

**Upgrading to v0.9.0 or later:** paste in the new `family-memories.yml`; changing `APP_VERSION` alone isn't enough. It fixes speech-to-text, which couldn't download its model before, and reads `family-memories.env` directly. If your stack is named `family-memories`, you can then remove the `.env` symlink.

The app takes a full backup automatically before it changes the database.

## Speech-to-text

It's on by default: the stack runs a `whisper` container, and the first recording downloads the "small" model (about 500 MB) into `DATA_ROOT/whisper-models`. It needs about 2 GB of memory. To use a Mac on your network instead, see "Speech-to-text on a Mac" in the admin guide. To switch it off, add `FEATURE_TRANSCRIPTION=false` to the environment, and add `services: { whisper: { profiles: [off] } }` in compose.override.yml.
