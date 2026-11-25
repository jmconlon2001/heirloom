#!/bin/sh
# Starts as root only to fix ownership of the bind-mounted folders for PUID/PGID
# (the OpenMediaVault/linuxserver convention), then drops to that user for good.
set -eu

PUID="${PUID:-1000}"
PGID="${PGID:-1000}"

if [ "$(id -u)" = "0" ]; then
    for dir in /data /data/keys /data/logs /data/media /data/exports /data/config /data/backups; do
        mkdir -p "$dir"
    done
    # Only touch files not already owned correctly (cheap on large media trees after the first run).
    find /data -xdev \( ! -user "$PUID" -o ! -group "$PGID" \) -exec chown -h "$PUID:$PGID" {} +
    if [ -d /backups ]; then chown "$PUID:$PGID" /backups; fi
    chmod 0700 /data/keys

    exec setpriv --reuid="$PUID" --regid="$PGID" --clear-groups --inh-caps=-all \
        env HOME=/tmp "$@"
fi

exec "$@"
