# Deploying the Outbox relay to Coolify

## Prereqs

- A Coolify instance with Traefik (HTTPS) enabled.
- A DNS record pointing at it (e.g. `relay.yourdomain.com`).
- The repo pushed to a Git remote Coolify can pull.

## Steps (Docker Compose build pack)

1. **New Resource → Application → Public Repository** (or Private, with deploy key).
2. **Build pack**: `Docker Compose`. **Base Directory**: `/relay`. **Docker Compose Location**: `/docker-compose.yml`.
3. **Domains for relay**: set `https://relay.yourdomain.com`. Coolify auto-wires Traefik to the `relay` service's exposed port (8080).
4. **Environment Variables** (UI tab):
   - `ADMIN_TOKEN` — **required**. Generate a long random string. Without it the relay throws on startup.
5. **Persistent Storage**: the compose declares a named volume `outbox-data` mounted at `/data`. Coolify creates and manages it. Snapshot it for backups.
6. **Deploy**.

The compose at `relay/docker-compose.yml` uses `expose: ["8080"]` (no host-port binding) so Coolify's Traefik picks it up cleanly. The sibling `docker-compose.override.yml` only matters for local dev (auto-applied when you run `docker compose` from your machine) and is ignored in Coolify because Coolify launches with an explicit `-f` flag.

## Troubleshooting

- **Browser says nothing on the domain**: open Coolify → Logs. If you see `ADMIN_TOKEN env var is required` and a crash loop, you forgot to set the env var in step 4. Set it, redeploy.
- **Stuck old container**: if you redeployed after editing the compose, Coolify may have a stale container named `outbox-relay` from the previous compose. Stop the application in Coolify, then redeploy.
- **DNS / cert**: confirm `dig relay.yourdomain.com` returns Coolify's host IP, and that the Let's Encrypt cert finished issuing (Coolify shows progress under the domain).

## Smoke test after deploy

```sh
RELAY=https://outbox.yourdomain.com
ADMIN=<your admin token>

curl -fsS $RELAY/healthz

curl -fsS -X POST $RELAY/v1/users \
  -H "X-Admin-Token: $ADMIN" \
  -H 'Content-Type: application/json' \
  -d '{"handle":"bruno"}'
```

Take the returned `token` and ship it to your machine securely (1Password, signal, whatever). Repeat for your coworker. Each user runs:

```sh
outbox setup
# enter RELAY, your handle, your token
```

That writes `~/.outbox/config.json`, registers `outbox-mcp` in `~/.claude.json`, adds a SessionStart hook, and drops `/inbox.md`.

## Operational notes

- **Backups**: snapshot the `/data` volume. Whole DB is one SQLite file.
- **Rotating a token**: there's no CLI for this yet. Easiest: delete the row in SQLite and re-create the user. Coordinate with the user since their `~/.outbox/config.json` will need updating.
- **Logs**: Serilog writes structured JSON to stdout. Coolify captures it.
- **Bumping the relay**: just push to the repo; Coolify rebuilds. Schema migrations are append-only and idempotent (`CREATE TABLE IF NOT EXISTS`).
