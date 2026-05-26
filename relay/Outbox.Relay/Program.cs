using System.Text.Json;
using System.Text.RegularExpressions;
using Outbox.Relay.Endpoints;
using Outbox.Relay.Models;
using Outbox.Relay.Storage;
using Serilog;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .WriteTo.Console()
    .MinimumLevel.Information());

var dbPath = Environment.GetEnvironmentVariable("OUTBOX_DB_PATH") ?? "/data/outbox.db";
var adminToken = Environment.GetEnvironmentVariable("ADMIN_TOKEN")
    ?? throw new InvalidOperationException("ADMIN_TOKEN env var is required");
var repoUrl = Environment.GetEnvironmentVariable("OUTBOX_REPO_URL")
    ?? "https://github.com/bfxavier/outbox.git";

builder.Services.AddSingleton(new SqliteStore(dbPath));
builder.Services.AddSingleton<StreamHub>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

var app = builder.Build();
app.UseSerilogRequestLogging();

var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
var handleRegex = new Regex(@"^[a-z0-9][a-z0-9_-]{1,31}$", RegexOptions.Compiled);

string? Authenticate(HttpContext ctx, SqliteStore store)
{
    var auth = ctx.Request.Headers.Authorization.ToString();
    if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
    return store.AuthenticateBearer(auth["Bearer ".Length..].Trim());
}

static string GetBaseUrl(HttpContext ctx)
{
    var fwdProto = ctx.Request.Headers["X-Forwarded-Proto"].ToString();
    var scheme = string.IsNullOrEmpty(fwdProto) ? ctx.Request.Scheme : fwdProto.Split(',')[0].Trim();
    var fwdHost = ctx.Request.Headers["X-Forwarded-Host"].ToString();
    var host = string.IsNullOrEmpty(fwdHost) ? ctx.Request.Host.Value : fwdHost.Split(',')[0].Trim();
    return $"{scheme}://{host}";
}

app.MapGet("/", () =>
{
    const string body = """
        Outbox relay — cross-machine AI agent messaging.

        Endpoints:
          GET  /healthz                  liveness
          GET  /install.sh?invite=<code> onboarding installer (pipe to bash)
          POST /v1/invites               admin: mint invite (X-Admin-Token)
          POST /v1/invites/{code}/redeem one-shot: exchange invite for bearer
          POST /v1/messages              send (bearer)
          GET  /v1/inbox                 list mine (bearer)
          GET  /v1/messages/{id}         read one (bearer)
          POST /v1/messages/{id}/ack     mark read (bearer)
          GET  /v1/stream                SSE push (bearer)

        Repo: https://github.com/bfxavier/outbox
        """;
    return Results.Text(body, "text/plain; charset=utf-8");
});

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

app.MapPost("/v1/users", (HttpContext ctx, CreateUserRequest req, SqliteStore store) =>
{
    var admin = ctx.Request.Headers["X-Admin-Token"].ToString();
    if (admin != adminToken) return Results.StatusCode(401);
    var handle = req.Handle?.Trim().TrimStart('@').ToLowerInvariant() ?? "";
    if (!handleRegex.IsMatch(handle))
        return Results.BadRequest(new { error = "invalid_handle", detail = "must match ^[a-z0-9][a-z0-9_-]{1,31}$" });
    var (created, token) = store.CreateUser(handle);
    if (!created) return Results.Conflict(new { error = "handle_exists" });
    return Results.Ok(new CreateUserResponse(handle, token));
});

app.MapPost("/v1/messages", async (HttpContext ctx, SendMessageRequest req, SqliteStore store, StreamHub hub) =>
{
    var from = Authenticate(ctx, store);
    if (from is null) return Results.StatusCode(401);
    var to = req.To?.Trim().TrimStart('@').ToLowerInvariant() ?? "";
    if (!handleRegex.IsMatch(to)) return Results.BadRequest(new { error = "invalid_to" });
    if (!store.UserExists(to)) return Results.NotFound(new { error = "unknown_recipient" });
    if (string.IsNullOrWhiteSpace(req.Subject) || string.IsNullOrWhiteSpace(req.Body))
        return Results.BadRequest(new { error = "missing_subject_or_body" });
    if (req.Subject.Length > 200) return Results.BadRequest(new { error = "subject_too_long" });
    if (req.Body.Length > 64 * 1024) return Results.BadRequest(new { error = "body_too_long" });
    string? metaJson = req.Metadata.HasValue ? req.Metadata.Value.GetRawText() : null;
    var id = store.InsertMessage(from, to, req.Subject, req.Body, metaJson);
    var evt = JsonSerializer.Serialize(new { id, from, subject = req.Subject, created_at = DateTimeOffset.UtcNow }, jsonOpts);
    hub.Broadcast(to, "new", evt);
    return Results.Ok(new SendMessageResponse(id));
});

app.MapGet("/v1/inbox", (HttpContext ctx, SqliteStore store, bool? unread, string? since, int? limit) =>
{
    var me = Authenticate(ctx, store);
    if (me is null) return Results.StatusCode(401);
    DateTimeOffset? sinceTs = null;
    if (!string.IsNullOrWhiteSpace(since) && DateTimeOffset.TryParse(since, out var ts)) sinceTs = ts;
    var items = store.ListInbox(me, unread ?? false, sinceTs, Math.Clamp(limit ?? 50, 1, 500));
    return Results.Ok(items);
});

app.MapGet("/v1/messages/{id}", (HttpContext ctx, string id, SqliteStore store) =>
{
    var me = Authenticate(ctx, store);
    if (me is null) return Results.StatusCode(401);
    var msg = store.GetMessage(id, me);
    return msg is null ? Results.NotFound() : Results.Ok(msg);
});

app.MapPost("/v1/messages/{id}/ack", (HttpContext ctx, string id, SqliteStore store) =>
{
    var me = Authenticate(ctx, store);
    if (me is null) return Results.StatusCode(401);
    var ok = store.AckMessage(id, me);
    return ok ? Results.Ok(new AckResponse(true)) : Results.NotFound();
});

app.MapPost("/v1/invites", (HttpContext ctx, CreateInviteRequest req, SqliteStore store) =>
{
    var admin = ctx.Request.Headers["X-Admin-Token"].ToString();
    if (admin != adminToken) return Results.StatusCode(401);
    var handle = req.Handle?.Trim().TrimStart('@').ToLowerInvariant() ?? "";
    if (!handleRegex.IsMatch(handle))
        return Results.BadRequest(new { error = "invalid_handle" });
    var ttl = TimeSpan.FromHours(Math.Clamp(req.TtlHours ?? 24, 1, 24 * 30));
    try
    {
        var code = store.CreateInvite(handle, ttl);
        var baseUrl = GetBaseUrl(ctx);
        var url = $"{baseUrl}/install.sh?invite={code}";
        return Results.Ok(new CreateInviteResponse(code, handle, url, DateTimeOffset.UtcNow.Add(ttl)));
    }
    catch (InvalidOperationException e) when (e.Message == "handle_exists")
    {
        return Results.Conflict(new { error = "handle_exists" });
    }
});

app.MapPost("/v1/invites/{code}/redeem", (HttpContext ctx, string code, SqliteStore store) =>
{
    var result = store.RedeemInvite(code);
    if (result is null) return Results.StatusCode(410);
    return Results.Ok(new RedeemInviteResponse(result.Value.handle, result.Value.token, GetBaseUrl(ctx)));
});

app.MapGet("/install.sh", (HttpContext ctx, string? invite) =>
{
    var script = InstallerScript.Generate(GetBaseUrl(ctx), invite, repoUrl);
    return Results.Text(script, "text/x-shellscript", System.Text.Encoding.UTF8);
});

app.MapGet("/v1/stream", async (HttpContext ctx, SqliteStore store, StreamHub hub) =>
{
    var me = Authenticate(ctx, store);
    if (me is null) { ctx.Response.StatusCode = 401; return; }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    await ctx.Response.WriteAsync(": connected\n\n");
    await ctx.Response.Body.FlushAsync();

    var sub = hub.Add(me);
    var ct = ctx.RequestAborted;
    var heartbeat = Task.Run(async () =>
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(25));
            while (await timer.WaitForNextTickAsync(ct))
                sub.Channel.Writer.TryWrite(": heartbeat\n\n");
        }
        catch (OperationCanceledException) { }
    }, ct);

    try
    {
        await foreach (var payload in sub.Channel.Reader.ReadAllAsync(ct))
        {
            await ctx.Response.WriteAsync(payload, ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        hub.Remove(me, sub);
        try { await heartbeat; } catch { }
    }
});

app.Run();
