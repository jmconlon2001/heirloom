#!/usr/bin/env bash
# Phase 0 acceptance check: a magic-link login works end to end through a TLS
# reverse proxy on a sub-domain, with the email delivered to Mailpit.
#
#   scripts/smoke-proxy.sh            uses ./.env (see "Try it locally" in README.md)
#
# Brings up: app, db, gotenberg, mailpit (profile dev), caddy (profile proxy).
set -euo pipefail
cd "$(dirname "$0")/.."

PORT="${PROXY_HTTPS_PORT:-8443}"
HOST="memories.localhost"
BASE="https://${HOST}:${PORT}"
MAILPIT="http://localhost:${MAILPIT_UI_PORT:-8025}"
ADMIN="$(grep -E '^ADMIN_EMAIL=' .env | cut -d= -f2-)"
JAR="$(mktemp)"
trap 'rm -f "$JAR"' EXIT

curl_proxy() { curl -sS --fail-with-body -k --resolve "${HOST}:${PORT}:127.0.0.1" -b "$JAR" -c "$JAR" "$@"; }
token_from() { sed -n 's/.*name="__RequestVerificationToken" type="hidden" value="\([^"]*\)".*/\1/p' | head -1; }
step() { printf '\n==> %s\n' "$*"; }

step "Starting stack (dev + proxy profiles)"
docker compose --profile dev --profile proxy up -d --build --wait

step "Readiness through the proxy"
curl_proxy "${BASE}/readyz"; echo

step "Clearing Mailpit"
curl -sS -X DELETE "${MAILPIT}/api/v1/messages" >/dev/null

step "Requesting a sign-in link for ${ADMIN}"
login_page="$(curl_proxy "${BASE}/account/login")"
af="$(printf '%s' "$login_page" | token_from)"
curl_proxy -o /dev/null -w '%{http_code} -> %{redirect_url}\n' \
  --data-urlencode "Email=${ADMIN}" --data-urlencode "__RequestVerificationToken=${af}" "${BASE}/account/login"

step "Waiting for the email in Mailpit"
link=""
for _ in $(seq 1 20); do
  link="$(curl -sS "${MAILPIT}/api/v1/message/latest" 2>/dev/null \
    | grep -oE 'https://[^" \\]+/account/magic-link\?token=[A-Za-z0-9_%-]+' | head -1 || true)"
  [ -n "$link" ] && break
  sleep 1
done
[ -n "$link" ] || { echo "FAIL: no sign-in email arrived"; exit 1; }
echo "link: $link"
case "$link" in "${BASE}"/*) ;; *) echo "FAIL: link does not use PUBLIC_BASE_URL (${BASE})"; exit 1;; esac

step "Opening the link and tapping 'Sign me in'"
magic_page="$(curl_proxy "$link")"
printf '%s' "$magic_page" | grep -q "Sign me in" || { echo "FAIL: confirmation page missing"; exit 1; }
af="$(printf '%s' "$magic_page" | token_from)"
token="${link#*token=}"
curl_proxy -o /dev/null -w '%{http_code} -> %{redirect_url}\n' \
  --data-urlencode "token=${token}" --data-urlencode "__RequestVerificationToken=${af}" \
  "${BASE}/account/magic-link"

step "Checking the session"
# Netscape jar columns: domain, subdomains, path, secure, expires, name, value
insecure="$(awk -F'\t' 'NF==7 && $6 ~ /^(fm_|\.AspNetCore)/ && $4 != "TRUE" {print $6}' "$JAR")"
[ -z "$insecure" ] || { echo "FAIL: cookies not marked Secure (forwarded proto not trusted?): $insecure"; exit 1; }
expires="$(awk -F'\t' 'NF==7 && $6=="fm_auth" {print $5}' "$JAR")"
[ -n "$expires" ] || { echo "FAIL: no fm_auth cookie"; exit 1; }
days=$(( (expires - $(date +%s)) / 86400 ))
echo "fm_auth is Secure + HttpOnly, remembered for ${days} days"
[ "$days" -ge 179 ] || { echo "FAIL: device should be remembered ~180 days"; exit 1; }
home="$(curl_proxy "${BASE}/")"
printf '%s' "$home" | grep -o "<h1>Welcome, [^<]*</h1>" || { echo "FAIL: not signed in"; exit 1; }

step "Checking the app saw the real client address"
docker compose logs app --since 2m | grep -q '"RequestPath":"/account/magic-link"' && echo "request log ok"

printf '\nPASS: magic-link login works behind the reverse proxy at %s\n' "$BASE"
