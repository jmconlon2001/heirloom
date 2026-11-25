# Family Memories

A self-hosted web app where family members answer guided prompts about their lives, writing or recording answers and attaching photos. The collection becomes a family website, a PDF, and a print-ready book.

Built for older storytellers on iPads: large text, big labelled buttons, no passwords (they sign in with an email link), and a "Need help?" box on every page.

> **Status:** Phases 0 and 1 are done. Storytellers can sign in with an email link or a printed QR card, answer guided questions by typing or recording their voice, and add photos. Admins manage people and questions. See [PROGRESS.md](PROGRESS.md) and [the build plan](docs/BUILD_PROMPT.MD).

## Quick start (Docker Compose)

```sh
cp .env.example .env        # then edit: PUBLIC_BASE_URL, ADMIN_EMAIL, POSTGRES_PASSWORD, SMTP_*, DATA_ROOT, BACKUP_ROOT
docker compose up -d
```

Open `PUBLIC_BASE_URL`, type the `ADMIN_EMAIL` address, and tap the link in the email.

- The app listens on `APP_PORT` (default 8095) over plain HTTP. Put it behind your reverse proxy: see [docs/CADDY.md](docs/CADDY.md).
- Data lives under `DATA_ROOT` (`data/` for media, keys and logs; `postgres/` for the database). Keep `BACKUP_ROOT` on a **different disk**.
- Health: `/healthz` (process alive) and `/readyz` (database and Gotenberg reachable).

## Try it locally (with fake email and a TLS proxy)

```sh
cp .env.example .env
# Set at least:
#   PUBLIC_BASE_URL=https://memories.localhost:8443   DATA_ROOT=./.data/stack   BACKUP_ROOT=./.data/backups
#   ADMIN_EMAIL=admin@example.test   POSTGRES_PASSWORD=anything   TRUSTED_PROXIES=172.30.95.0/24
#   SMTP_HOST=mailpit   SMTP_PORT=1025   SMTP_SECURITY=None   PUID/PGID=1000
docker compose --profile dev --profile proxy up -d --build
```

- App, via the proxy: <https://memories.localhost:8443> (self-signed certificate)
- Every email lands in Mailpit: <http://localhost:8025>
- `scripts/smoke-proxy.sh` runs the whole sign-in through the proxy and Mailpit, and checks the cookies.

## Development

Requires the .NET 10 SDK, Docker (tests start PostgreSQL with Testcontainers) and `ffmpeg` on your PATH (voice and video tests).

```sh
dotnet build
dotnet test --project src/App.Tests     # unit + integration (real PostgreSQL 17)
dotnet test --project src/App.E2E       # Playwright: iPad WebKit + Chromium, axe checks
dotnet tool restore && dotnet ef migrations add <Name> -p src/App -o Data/Migrations
```

The first e2e run downloads WebKit and Chromium.

| Folder | What |
|---|---|
| `src/App` | ASP.NET Core Razor Pages app (`FamilyMemories.dll`) |
| `src/App.Tests` | xUnit v3 unit and integration tests |
| `src/App.E2E` | Playwright end-to-end and accessibility tests |
| `docker/`, `Dockerfile`, `compose.yml` | Container image and stack |
| `deploy/dev/` | Local Caddy that stands in for the OPNsense reverse proxy |
| `seed/prompts.json` | The 52 built-in questions with hints (★ core) |
| `docs/` | Build plan, decisions, deployment guides |

Configuration is all environment variables; [.env.example](.env.example) documents each one. Design decisions are in [docs/DECISIONS.md](docs/DECISIONS.md).

Pushing a `v*.*.*` tag publishes `ghcr.io/<owner>/family-memories` via GitHub Actions.
