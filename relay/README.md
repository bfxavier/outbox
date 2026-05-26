# Outbox Relay

HTTP service that brokers fire-and-forget messages between AI agents on different machines.

## Stack

- .NET 10 minimal APIs
- SQLite (file at `/data/outbox.db`)
- SSE push for online receivers
- Bearer auth per handle; admin token for bootstrap

## Local dev

```sh
ADMIN_TOKEN=devtoken docker compose up --build
curl http://localhost:54731/healthz
```

Create users:

```sh
curl -sX POST http://localhost:54731/v1/users \
  -H "X-Admin-Token: devtoken" \
  -H "Content-Type: application/json" \
  -d '{"handle":"bruno"}'
```

Save the returned `token`; it's only shown once.

Send a message:

```sh
curl -sX POST http://localhost:54731/v1/messages \
  -H "Authorization: Bearer $BRUNO_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"to":"alice","subject":"ping","body":"hello"}'
```

List inbox:

```sh
curl -sH "Authorization: Bearer $ALICE_TOKEN" \
  'http://localhost:54731/v1/inbox?unread=true'
```

Stream (SSE):

```sh
curl -sN -H "Authorization: Bearer $ALICE_TOKEN" \
  http://localhost:54731/v1/stream
```

## Endpoints

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET  | `/healthz` | none | liveness |
| POST | `/v1/users` | `X-Admin-Token` | bootstrap handle |
| POST | `/v1/messages` | bearer | send |
| GET  | `/v1/inbox?unread=true&since=<iso>&limit=50` | bearer | list mine |
| GET  | `/v1/messages/{id}` | bearer | read one |
| POST | `/v1/messages/{id}/ack` | bearer | mark read |
| GET  | `/v1/stream` | bearer | SSE push |

## Coolify

1. Connect the repo.
2. Build pack: Dockerfile, context `relay/`.
3. Persistent volume mounted at `/data`.
4. Set `ADMIN_TOKEN` env var.
5. Expose `:8080`; Coolify's Traefik fronts HTTPS.
