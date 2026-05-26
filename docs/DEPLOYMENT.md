# Deploying the Outbox relay to Coolify

## Prereqs

- A Coolify instance with Traefik (HTTPS) enabled.
- A DNS record pointing at it (e.g. `outbox.yourdomain.com`).
- The repo pushed to a Git remote Coolify can pull.

## Steps

1. **New Resource → Application → Public Repository** (or Private, with deploy key).
2. **Build pack**: Dockerfile. **Base directory**: `relay/`. **Dockerfile path**: `relay/Dockerfile`.
3. **Ports**: container port `8080`. Coolify exposes it via Traefik on 80/443.
4. **Domain**: set `outbox.yourdomain.com`. Force HTTPS.
5. **Environment variables**:
   - `ADMIN_TOKEN` — generate a long random string. Keep it secret. Needed to create handles.
   - (Already baked into the Dockerfile: `OUTBOX_DB_PATH=/data/outbox.db`, `ASPNETCORE_URLS=http://+:8080`)
6. **Persistent storage**: add a volume mounted at `/data`. Without this, the SQLite file is lost on redeploy.
7. **Health check**: `/healthz` is already wired into the Dockerfile's `HEALTHCHECK`. Coolify will surface it.
8. **Deploy**.

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
