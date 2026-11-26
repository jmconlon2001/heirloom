# Backups and restoring

Everything the family has shared is in two places, and backups cover both:

| What | Where | In every backup |
|---|---|---|
| Stories, people, settings | PostgreSQL (`DATA_ROOT/postgres`) | ✓ as a `pg_dump` |
| Photos, recordings, documents | `DATA_ROOT/data/media` | ✓ |
| Sign-in encryption keys | `DATA_ROOT/data/keys` | ✓ (without them, everyone has to sign in again) |
| Settings files | `DATA_ROOT/data/config` | ✓ |
| Logs, finished exports | `DATA_ROOT/data/logs`, `…/exports` | ✗ (can be recreated) |

Family admin → **Backups** shows all of this for your own server: what the last backup held (the database size and counts of stories, people and files; the number and size of media files), every place a copy is kept, with the server's own folder names, and a warning if the backups are on the same disk as the stories.

## Where the copies are

1. **On the server**, encrypted: `BACKUP_ROOT/restic`. Put `BACKUP_ROOT` on a different disk from `DATA_ROOT` (a USB drive is fine). If they're on the same disk, the Backups page and `/status` say so.
2. **Offsite**, encrypted (optional, strongly recommended): `OFFSITE_RESTIC_REPOSITORY`. OneDrive, Google Drive and Dropbox work through rclone, which is in the image; Backblaze B2, S3 and SFTP work directly.
3. **A plain copy** (optional, `PLAIN_COPY=true`): `BACKUP_ROOT/plain/heirloom.sql.gz` and `BACKUP_ROOT/plain/media/`, refreshed after each nightly backup. No restic is needed to open it, and it's easy to mirror with `rsync -a --delete`. It isn't encrypted and keeps no history, so it's a convenience next to the restic backups, not a replacement for them.

## How backups work

- **Every night** at `BACKUP_TIME` (default 02:30, `APP_TIMEZONE`), the app dumps the database and runs `restic backup` of the table above into `/backups/restic`. That folder is `BACKUP_ROOT`, which must be on a **different physical disk** from `DATA_ROOT`.
- If `OFFSITE_RESTIC_REPOSITORY` is set (Backblaze B2, S3, SFTP, rclone…), the same backup also goes there.
- Old backups are thinned out: 7 daily, 4 weekly, 12 monthly (`BACKUP_KEEP_*`).
- **Every Sunday** the app reads back 5% of the stored data (`restic check --read-data-subset=5%`). It also restores the newest database dump into a throwaway database and checks that people and questions are in it.
- **Before every upgrade** that changes the database, and **before every restore**, an extra backup is taken automatically.
- If anything fails, every admin gets an email (and an ntfy alert if `NTFY_*` is set). The **Family admin → Backups** page shows when the last good backup and the last good check happened.

### The password

Backups are encrypted with `RESTIC_PASSWORD` from `.env`. **If that password is lost, the backups are useless.** Keep it in a password manager, and somewhere a trusted family member can find it (for example a sealed note with the family's important papers).

## Restoring from the admin pages (the app is running)

Use this to undo a mistake, e.g. someone deleted a lot of stories.

1. **Family admin → Backups → Restore from a backup**, and pick the backup from before the mistake.
2. Type `RESTORE` and press **Restore this backup**.
3. Everyone sees "Back in a few minutes" while the app does the following:
   - takes a safety backup of the current state;
   - replaces the database, photos and settings with the backup's copy;
   - upgrades the database if the backup is from an older version;
   - opens again.
4. Changed your mind? Restore the safety backup (it's tagged `prerestore`).

Restoring keeps today's sign-in keys as well as the backup's, so people who are signed in now stay signed in.

## Disaster restore (new disk, new server, or after a fire)

You need four things:
- a machine with Docker and the Compose plugin;
- this stack's files (`compose.yml`, or `heirloom.yml` on OpenMediaVault);
- your old `.env` (on OpenMediaVault, `heirloom.env` also works), or a new one from `.env.example` with the same `POSTGRES_*` values if you can;
- access to a backup repository and its password.

```sh
# 1. Put the stack files and .env in a folder, and point DATA_ROOT and BACKUP_ROOT at the new disks.
cd /appdata/heirloom

# 2a. From a local repository (a USB disk, or the old BACKUP_ROOT disk):
scripts/restore.sh --repo /mnt/usb/heirloom/restic --snapshot latest --password-file ~/restic-password.txt

# 2b. Or from the offsite repository (export the backend's credentials first;
#     for OneDrive or another rclone remote, copy rclone.conf to DATA_ROOT/data/config/rclone.conf first):
export B2_ACCOUNT_ID=…  B2_ACCOUNT_KEY=…
scripts/restore.sh --repo b2:my-bucket:heirloom --snapshot latest --password-file ~/restic-password.txt
```

The script does the following:
- starts only the database;
- restores photos, keys and settings into `DATA_ROOT/data`;
- replaces the database with the backup's dump;
- starts everything.

Once it's done:

1. Open the site and sign in (your old sign-in still works, because the keys were restored).
2. Go to **Family admin → Backups** and press **Back up now**, so the new machine has its own first backup.
3. If the old `BACKUP_ROOT` disk is gone, the next backup creates a new repository automatically.

To see which backups exist before choosing one:

```sh
docker compose run --rm --no-deps -e RESTIC_REPOSITORY=/backups/restic -e RESTIC_PASSWORD="$(cat ~/restic-password.txt)" \
  --entrypoint restic app snapshots
```

## Testing restores

Restores are tested in three ways:
- **Automated:** `BackupRestoreTests` adds people, stories and photos, backs them up, wipes the database and media folder, restores, and checks that file checksums and row counts match.
- **Weekly:** the check restores the latest database into a throwaway database.
- **By hand:** once or twice a year, do a disaster restore onto a spare machine or VM. It's the only way to be sure the password and the offsite copy really work.

The script was last rehearsed end to end on 2026-11-26. The setup was OpenMediaVault-style (`family-memories.yml` and `family-memories.env` only), with an empty data folder. The backup was the pre-upgrade snapshot of a v0.6.0 site. Every story, photo, person and version came back, and the app upgraded the restored database on its first start.
