# Reverse proxy: Caddy on OPNsense

The stack speaks plain HTTP on one port (`APP_PORT`, default **8095**). TLS, certificates and the public sub-domain are handled by the Caddy plugin on OPNsense (`os-caddy`).

## 1. DNS
Point `memories.example.com` at your WAN address (or at OPNsense on your LAN if it's family-only behind a VPN). Caddy gets a Let's Encrypt certificate automatically.

## 2. Caddy (OPNsense UI)
**Services → Caddy Web Server → Reverse Proxy**

- **Domains → +**: `memories.example.com`, port 443.
- **Handlers → +**: Domain `memories.example.com`, upstream `http://<docker-host-ip>:8095`.

The equivalent Caddyfile:

```caddyfile
memories.example.com {
	encode zstd gzip

	# Large uploads (video) and slow connections from older iPads.
	request_body {
		max_size 1200MB
	}

	reverse_proxy 192.168.1.20:8095 {
		# Caddy already sends X-Forwarded-For / -Proto / -Host.
		transport http {
			read_timeout 10m
			write_timeout 10m
		}
	}
}
```

No extra headers are needed: the app sets its own security headers (CSP, `X-Frame-Options`, and so on). Add HSTS in Caddy if you want it: `header Strict-Transport-Security "max-age=31536000"`.

## 3. App `.env`

```dotenv
PUBLIC_BASE_URL=https://memories.example.com
# The address the app sees requests coming FROM: OPNsense's LAN IP.
TRUSTED_PROXIES=192.168.1.1
```

The app only honours `X-Forwarded-For` and `X-Forwarded-Proto` from `TRUSTED_PROXIES`. If this is wrong, sign-in cookies won't be marked Secure and the logs will show OPNsense's IP for every visitor.

**How to find the right value:** sign in once, then on the Docker host run
`docker compose exec db psql -U family_memories -tAc 'select "ConsumedByIp" from "MagicLinkTokens" order by "CreatedAt" desc limit 1'`.
It should show your phone or PC's real IP. If it shows the OPNsense address, `TRUSTED_PROXIES` doesn't include it yet.

> With Docker's default userland proxy, some setups see the Docker bridge gateway (e.g. `172.30.95.1`) rather than the OPNsense IP. If so, add `FRONTEND_SUBNET` (default `172.30.95.0/24`) to `TRUSTED_PROXIES`, but only if nothing else on the host can reach that port directly.

## 4. Check it
```sh
curl -I https://memories.example.com/readyz   # 200, body "Healthy" (or "Degraded" if Gotenberg is down)
```
Then sign in from a phone. The sign-in email's button must start with `https://memories.example.com/`.

## Trying it locally
`docker compose --profile dev --profile proxy up -d` runs a Caddy that does the same job at `https://memories.localhost:8443` (self-signed; accept the warning). `scripts/smoke-proxy.sh` signs in through it automatically.
