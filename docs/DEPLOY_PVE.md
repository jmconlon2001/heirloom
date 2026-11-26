# Deploying on Proxmox VE

Two options:
- **(a) An unprivileged Debian 13 LXC container** running Docker: light and fast.
- **(b) A small Debian VM:** simplest, and the most isolated.

Either way, the family's data lives on a ZFS dataset (or any storage), and backups go to a **different physical disk**.

## (a) Unprivileged LXC with Docker

### Create the container

```sh
# On the Proxmox host
pct create 120 local:vztmpl/debian-13-standard_13.*_amd64.tar.zst \
  --hostname heirloom --memory 3072 --cores 2 --rootfs local-zfs:16 \
  --net0 name=eth0,bridge=vmbr0,ip=dhcp --unprivileged 1 --features nesting=1,keyctl=1 --onboot 1
```

`nesting=1` lets Docker run inside the container; `keyctl=1` keeps some Docker features happy.

### Data on ZFS, bind-mounted in

```sh
zfs create rpool/heirloom            # the family's data (snapshot it too, if you like)
zfs create backup-pool/heirloom      # on a DIFFERENT disk or pool
pct set 120 -mp0 /rpool/heirloom,mp=/data/heirloom
pct set 120 -mp1 /backup-pool/heirloom,mp=/backups/heirloom
```

**UID mapping (important):** in an unprivileged container, user 1000 inside is user **101000** on the host. Give that host user the folders:

```sh
chown -R 101000:101000 /rpool/heirloom /backup-pool/heirloom
```

Inside the container, use `PUID=1000`, `PGID=1000`. PostgreSQL's own folder is fixed up by the database container on first start.

### Docker and the stack

```sh
pct enter 120
apt update && apt install -y ca-certificates curl
curl -fsSL https://get.docker.com | sh
mkdir -p /opt/heirloom && cd /opt/heirloom
# Download compose.yml and env.example from the latest GitHub release into this folder.
cp env.example .env && nano .env
#   DATA_ROOT=/data/heirloom   BACKUP_ROOT=/backups/heirloom   PUID=1000 PGID=1000 …
docker login ghcr.io          # if the image is private (token with read:packages)
docker compose up -d
curl -s localhost:8095/readyz   # Healthy
```

## (b) A small Debian VM

Create a Debian 13 VM with 2 vCPUs, 3–4 GB RAM and a 16 GB system disk. Add a second virtual disk (on storage backed by a *different* physical disk) for backups and mount it at `/backups`. Then follow the Docker steps above, with `PUID`/`PGID` set to your user's ids (`id -u`, `id -g`), and no UID shifting.

## Both: next steps

- **HTTPS:** point your reverse proxy at `http://<container-or-vm-ip>:8095`. See [CADDY.md](CADDY.md).
- **Backups:** the app backs up nightly to `BACKUP_ROOT`. For extra safety, also set an offsite repository (`OFFSITE_RESTIC_REPOSITORY`, e.g. Backblaze B2).
- **Proxmox's own backups** (vzdump or Proxmox Backup Server) of the container or VM are a nice extra, but don't replace the app's backups: they don't include a consistent database dump of a running PostgreSQL.
- **Upgrading:** `docker compose pull && docker compose up -d` (with the new release's compose file, or `APP_VERSION` set).
- **Disaster recovery** on a fresh container or VM: [RESTORE.md](RESTORE.md).
