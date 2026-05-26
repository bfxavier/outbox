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
