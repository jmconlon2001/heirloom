#!/usr/bin/env bash
# Disaster restore onto a fresh host (see docs/RESTORE.md).
#
#   scripts/restore.sh --repo <path-or-url> --snapshot <id|latest> [--password-file <file>]
#
# Run it from the stack folder (where compose.yml and .env live). It:
#   1. starts only the database,
#   2. restores media, keys and config from the restic snapshot into DATA_ROOT,
#   3. replaces the database with the snapshot's dump,
#   4. starts the whole stack (the app runs any newer migrations itself).
#
# The restic password comes from --password-file, or RESTIC_PASSWORD in the environment,
# or RESTIC_PASSWORD in .env. It is never put on a command line.
set -euo pipefail

REPO=""
SNAPSHOT="latest"
PASSWORD_FILE=""

while [ $# -gt 0 ]; do
  case "$1" in
    --repo) REPO="$2"; shift 2 ;;
    --snapshot) SNAPSHOT="$2"; shift 2 ;;
    --password-file) PASSWORD_FILE="$2"; shift 2 ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

# OpenMediaVault's Compose plugin names the files after the stack: heirloom.yml/.env, or family-memories.yml/.env from before the rename.
COMPOSE_FILE_NAME=""
for f in compose.yml heirloom.yml family-memories.yml; do [ -f "$f" ] && { COMPOSE_FILE_NAME="$f"; break; }; done
[ -n "$COMPOSE_FILE_NAME" ] || { echo "Run this from the stack folder (compose.yml not found)." >&2; exit 2; }
ENV_FILE=""
for f in .env heirloom.env family-memories.env; do [ -f "$f" ] && { ENV_FILE="$f"; break; }; done
[ -n "$ENV_FILE" ] || { echo ".env not found. Copy your old .env here first (or create one from .env.example)." >&2; exit 2; }
[ -n "$REPO" ] || { echo "--repo is required (e.g. /mnt/usb/family-memories/restic or b2:bucket:path)" >&2; exit 2; }

# The compose file and env file this stack actually uses (OMV names them after the stack).
COMPOSE=(docker compose)
if [ "$COMPOSE_FILE_NAME" != compose.yml ]; then
  COMPOSE+=(-f "$COMPOSE_FILE_NAME")
  [ -f compose.override.yml ] && COMPOSE+=(-f compose.override.yml)   # only picked up automatically next to compose.yml
fi
[ "$ENV_FILE" = ".env" ] || COMPOSE+=(--env-file "$ENV_FILE")

env_value() { grep -E "^$1=" "$ENV_FILE" | tail -1 | cut -d= -f2- || true; }

if [ -n "$PASSWORD_FILE" ]; then
  RESTIC_PASSWORD="$(cat "$PASSWORD_FILE")"
elif [ -z "${RESTIC_PASSWORD:-}" ]; then
  RESTIC_PASSWORD="$(env_value RESTIC_PASSWORD)"
fi
[ -n "${RESTIC_PASSWORD:-}" ] || { echo "No restic password: use --password-file, or set RESTIC_PASSWORD." >&2; exit 2; }

DATA_ROOT="$(env_value DATA_ROOT)"
[ -n "$DATA_ROOT" ] || { echo "DATA_ROOT is not set in $ENV_FILE" >&2; exit 2; }
mkdir -p "$DATA_ROOT/data" "$DATA_ROOT/postgres"

# OneDrive, Google Drive and other rclone remotes need rclone's settings in the config folder first.
if [[ "$REPO" == rclone:* ]] && [ ! -f "$DATA_ROOT/data/config/rclone.conf" ]; then
  echo "$REPO is an rclone remote: copy your rclone.conf to $DATA_ROOT/data/config/rclone.conf first." >&2; exit 2
fi

# A local repository path is mounted into the helper container; URLs (b2:, s3:, sftp:…) are used as-is.
MOUNT_ARGS=()
CONTAINER_REPO="$REPO"
if [[ "$REPO" != *:* ]]; then
  [ -d "$REPO" ] || { echo "Repository folder $REPO not found" >&2; exit 2; }
  MOUNT_ARGS=(-v "$(cd "$REPO" && pwd):/restore-repo:ro")
  CONTAINER_REPO="/restore-repo"
fi

# The password file lives inside DATA_ROOT (already shared with Docker as /data) and is removed on exit.
PASSFILE="$(mktemp "$DATA_ROOT/data/.restore-password.XXXXXX")"
trap 'rm -f "$PASSFILE"' EXIT
chmod 600 "$PASSFILE"
printf '%s' "$RESTIC_PASSWORD" > "$PASSFILE"

# Same defaults as compose.yml, so a minimal .env works too.
POSTGRES_USER="$(env_value POSTGRES_USER)"; export POSTGRES_USER="${POSTGRES_USER:-family_memories}"
POSTGRES_DB="$(env_value POSTGRES_DB)"; export POSTGRES_DB="${POSTGRES_DB:-family_memories}"

echo "==> Starting the database only"
"${COMPOSE[@]}" up -d --wait db

echo "==> Restoring snapshot '$SNAPSHOT' from $REPO"
"${COMPOSE[@]}" run --rm --no-deps \
  "${MOUNT_ARGS[@]}" \
  -e RESTIC_REPOSITORY="$CONTAINER_REPO" \
  -e RESTIC_PASSWORD_FILE="/data/$(basename "$PASSFILE")" \
  -e SNAPSHOT="$SNAPSHOT" \
  -e POSTGRES_USER -e POSTGRES_DB \
  --entrypoint /bin/bash app -euo pipefail -c '
    # --no-lock: the repository may be mounted read-only (a USB disk, the old backup disk).
    restic snapshots --no-lock "$SNAPSHOT" >/dev/null
    stage=/data/restore-staging
    rm -rf "$stage"; mkdir -p "$stage"
    restic restore --no-lock "$SNAPSHOT" --target "$stage" \
      --include /data/media --include /data/keys --include /data/config \
      --include /data/backup-staging/database.dump
    for d in media config; do
      rm -rf "/data/$d"
      if [ -d "$stage/data/$d" ]; then mv "$stage/data/$d" "/data/$d"; else mkdir -p "/data/$d"; fi
    done
    mkdir -p /data/keys
    cp -an "$stage/data/keys/." /data/keys/ 2>/dev/null || true
    dump="$stage/data/backup-staging/database.dump"
    [ -f "$dump" ] || { echo "The snapshot has no database dump" >&2; exit 1; }
    export PGPASSWORD="$POSTGRES_PASSWORD"
    psql -v ON_ERROR_STOP=1 -h db -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;"
    pg_restore --no-owner --no-privileges --exit-on-error -h db -U "$POSTGRES_USER" -d "$POSTGRES_DB" "$dump"
    rm -rf "$stage"
    echo "Files and database restored."
  '

echo "==> Starting the whole stack"
"${COMPOSE[@]}" up -d
# Wait for the app itself; optional extras (like speech-to-text downloading its model) may take longer.
"${COMPOSE[@]}" up -d --wait app

echo
echo "Done. Open the site and sign in. Then, under Family admin > Backups, press \"Back up now\"."
