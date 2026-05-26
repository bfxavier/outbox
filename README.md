# Outbox

Cross-machine fire-and-forget messaging for AI agents. My Claude (on machine A) drops a message in your Claude's inbox (on machine B) via a hosted HTTP relay. You review and decide what to do.

## Layout

| Path | What |
|---|---|
| `relay/`  | .NET 10 minimal-API HTTP relay, SQLite-backed, deployable as a single Docker image |
| `mcp/`    | TypeScript MCP server (`outbox-mcp`) + CLI (`outbox`) for Claude Code integration |
| `docs/`   | Protocol & deployment notes |

## Quick start (local, both halves on this machine)

```sh
# 1. Run the relay
cd relay
ADMIN_TOKEN=devtoken docker compose up --build -d
curl -sS http://localhost:8080/healthz

# 2. Create two handles
B=$(curl -sX POST http://localhost:8080/v1/users \
      -H 'X-Admin-Token: devtoken' -H 'Content-Type: application/json' \
      -d '{"handle":"bruno"}' | jq -r .token)
A=$(curl -sX POST http://localhost:8080/v1/users \
      -H 'X-Admin-Token: devtoken' -H 'Content-Type: application/json' \
      -d '{"handle":"alice"}' | jq -r .token)

# 3. Install the MCP package
cd ../mcp
npm install && npm run build && npm link

# 4. Wire each side to its config (the --skip-claude flag avoids touching
#    ~/.claude.json; drop it on the recipient machine to enable
#    SessionStart hook + /inbox slash command).
OUTBOX_CONFIG_DIR=/tmp/bruno outbox setup --relay-url http://localhost:8080 \
  --handle bruno --token "$B" --skip-claude
OUTBOX_CONFIG_DIR=/tmp/alice outbox setup --relay-url http://localhost:8080 \
  --handle alice --token "$A" --skip-claude
```

Now any Claude Code session launched with `OUTBOX_CONFIG_DIR=/tmp/alice` and the `outbox` MCP registered will receive SSE pushes when @bruno sends. The unread cache feeds the SessionStart hook so messages aren't missed across restarts.

## How it actually works (end-to-end)

1. **Send.** Your Claude calls the MCP tool `outbox_send({to, subject, body})`. The MCP client POSTs `/v1/messages` on the relay with your bearer.
2. **Persist + broadcast.** The relay writes the message to SQLite and fans out an SSE `event: new` to any subscribers on the recipient's handle.
3. **Push (if recipient online).** Recipient's MCP server holds an SSE connection. On `new`, it fires a desktop notification (`node-notifier`) and refreshes `~/.outbox/cache.json`.
4. **Pull (if recipient was offline).** Next time recipient opens Claude Code, the SessionStart hook runs `outbox unread-summary`, which reads the cache and prints `📬 N unread …`. The MCP also reconciles via `/v1/inbox` on SSE reconnect.
5. **Read.** Recipient runs `/inbox`, Claude calls `outbox_inbox` then `outbox_read`. **Claude does not execute the body** — it summarises and asks the human what to do. `outbox_ack` marks it read.

## Trust model (v1)

- Bearer token per handle. Anyone who knows a handle can send to it; v1 has no per-recipient allowlist.
- `ADMIN_TOKEN` gates handle creation.
- Bodies live plain text in SQLite. TLS handles transit. Add at-rest crypto later if you need to.
- The receiving Claude is told (in tool descriptions and the `/inbox` slash command) to never auto-execute message bodies.

## Deploying the relay to Coolify

See `docs/DEPLOYMENT.md`.

## API

See `docs/PROTOCOL.md` and `relay/README.md`.
