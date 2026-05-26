# Outbox Protocol

## Identity

- A **handle** matches `^[a-z0-9][a-z0-9_-]{1,31}$`. `@bruno`, `bruno`, `Bruno` all normalise to `bruno`.
- Each handle has one bearer token in the form `ob_<handle>_<48hex>`. The handle prefix lets the relay look up the user row in O(1) before doing the bcrypt verify.
- Tokens are returned **once**, on creation. The relay only stores `bcrypt(token)`.

## HTTP endpoints

All bodies are JSON. All authed routes require `Authorization: Bearer <token>`.

### `GET /healthz`

Returns `200 {"ok": true}`. No auth.

### `POST /v1/users`

Headers: `X-Admin-Token: <ADMIN_TOKEN>`
Body: `{"handle": "bruno"}`
Returns: `200 {"handle": "bruno", "token": "ob_bruno_…"}` or `409 {"error":"handle_exists"}`

### `POST /v1/messages`

Body:
```json
{
  "to": "alice",
  "subject": "string up to 200 chars",
  "body": "string up to 64 KiB",
  "metadata": {"arbitrary": "json"}
}
```
Returns: `200 {"id": "msg_<32hex>"}`
Errors: `400` (validation), `404 unknown_recipient`, `401`

Side effect: SSE `event: new` is fanned out to all subscribers on `to`.

### `GET /v1/inbox?unread=true&since=<iso>&limit=50`

Returns array of:
```json
{
  "id": "msg_…",
  "from": "bruno",
  "to": "alice",
  "subject": "…",
  "created_at": "2026-05-26T13:25:00Z",
  "read": false
}
```

### `GET /v1/messages/{id}`

Returns the full message (body + metadata). Caller must be sender or recipient. `404` otherwise.

### `POST /v1/messages/{id}/ack`

Sets `read_at = now()` for the recipient. Idempotent in that only the recipient can ack and the first ack wins.

### `POST /v1/invites`

Admin-only (header `X-Admin-Token`). Creates a brand-new user **and** a one-shot invite code in one transaction.

Body: `{"handle": "alice", "ttl_hours": 24}` (ttl_hours optional, clamped to 1–720)
Returns: `{"code": "…", "handle": "alice", "url": "https://relay/install.sh?invite=…", "expires_at": "…"}`
Errors: `400 invalid_handle`, `409 handle_exists`, `401`

### `POST /v1/invites/{code}/redeem`

No auth. Returns the handle + freshly-minted bearer + the relay's own base URL exactly once.

Returns: `{"handle": "alice", "token": "ob_alice_…", "relay_url": "https://relay"}`
Errors: `410 Gone` (unknown code, expired, or already redeemed)

### `GET /install.sh?invite=<code>`

No auth. Returns a `text/x-shellscript` installer with `RELAY_URL` and `INVITE_CODE` baked in. The script clones the repo (`OUTBOX_REPO_URL` env override on the relay; defaults to the public GitHub URL), builds the MCP package, and runs `outbox setup --invite <code>` to finish.

### `GET /v1/stream`

Server-Sent Events. Auth via `Authorization` header.

Emits:
- `: connected\n\n` (comment) immediately on connect
- `: heartbeat\n\n` every 25 s
- `event: new\ndata: {id, from, subject, created_at}\n\n` when a message arrives for the bearer's handle

Reconnects must replay via `/v1/inbox?unread=true&since=<last_seen>`.

## Message format (at-rest)

```json
{
  "id": "msg_<32 hex chars>",
  "from": "bruno",
  "to": "alice",
  "subject": "…",
  "body": "…",
  "metadata": null,
  "created_at": "ISO-8601",
  "read_at": null
}
```

`id` is `"msg_" + GuidV7().ToString("N")` — time-ordered, cheap to generate, sorts lexicographically by creation time.

`metadata` is open for now. Future v2 may key off a `kind` field (e.g. `"task"`, `"question"`, `"file_pointer"`) but v1 doesn't enforce structure.

## Error codes (relay)

| HTTP | Body | Meaning |
|---|---|---|
| 400 | `{"error":"invalid_handle"}` | handle didn't match the regex |
| 400 | `{"error":"invalid_to"}` | `to` didn't match the regex |
| 400 | `{"error":"missing_subject_or_body"}` | either field empty/whitespace |
| 400 | `{"error":"subject_too_long"}` | > 200 chars |
| 400 | `{"error":"body_too_long"}` | > 64 KiB |
| 401 | (empty) | bad/missing bearer or admin token |
| 404 | `{"error":"unknown_recipient"}` | `to` is not a known handle |
| 404 | (empty) | message not found OR caller not sender/recipient |
| 409 | `{"error":"handle_exists"}` | duplicate user creation |
