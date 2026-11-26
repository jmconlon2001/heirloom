# syntax=docker/dockerfile:1.7
# Family Memories app image. Production target is linux/amd64 (Intel N100).
# The app is published framework-dependent and architecture-neutral (no RID), so the same
# Dockerfile builds on amd64 or arm64 with or without BuildKit.

FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/App/App.csproj src/App/
RUN dotnet restore src/App/App.csproj
COPY src/App/ src/App/
COPY seed/ seed/
RUN dotnet publish src/App/App.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble AS runtime

# postgresql-client-17 comes from the PostgreSQL project's apt repo (Ubuntu 24.04 ships 16,
# which cannot dump a PostgreSQL 17 server). ffmpeg normalises voice recordings and makes video posters;
# libgomp1 is the OpenMP runtime Magick.NET's native library needs on Linux; pandoc makes the EPUB/DOCX.
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl tzdata; \
    install -d /usr/share/postgresql-common/pgdg; \
    curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc; \
    echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt noble-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list; \
    apt-get update; \
    apt-get install -y --no-install-recommends postgresql-client-17 ffmpeg libgomp1 bzip2 pandoc; \
    apt-get clean; \
    rm -rf /var/lib/apt/lists/*

# restic: a pinned upstream release (Ubuntu's package is years old), checked against its published SHA-256.
ARG RESTIC_VERSION=0.20.0
RUN set -eux; \
    arch="$(dpkg --print-architecture)"; \
    case "$arch" in \
      amd64) sum=0ccd9fa7885074a24603b051e01bf9bd8b5e45d6a1791e9d9887e9e9372107f7 ;; \
      arm64) sum=1188daded239bccb5952a01d2ad33e77a0d3dc34f9d9fc1e7fdda618d44938ef ;; \
      *) echo "unsupported architecture $arch"; exit 1 ;; \
    esac; \
    curl -fsSL -o /tmp/restic.bz2 "https://github.com/restic/restic/releases/download/v${RESTIC_VERSION}/restic_${RESTIC_VERSION}_linux_${arch}.bz2"; \
    echo "$sum  /tmp/restic.bz2" | sha256sum -c -; \
    bunzip2 -c /tmp/restic.bz2 > /usr/local/bin/restic; \
    chmod 0755 /usr/local/bin/restic; \
    rm /tmp/restic.bz2; \
    restic version

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DATA_DIR=/data \
    DOTNET_NOLOGO=1 \
    TZ=America/New_York

WORKDIR /app
COPY --from=build /app ./
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod 0755 /usr/local/bin/entrypoint.sh && mkdir -p /data /backups

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl -fsS http://localhost:8080/healthz || exit 1

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["dotnet", "FamilyMemories.dll"]
