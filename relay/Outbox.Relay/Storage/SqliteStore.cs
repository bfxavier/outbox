using System.Globalization;
using BCrypt.Net;
using Microsoft.Data.Sqlite;
using Outbox.Relay.Models;

namespace Outbox.Relay.Storage;

public sealed class SqliteStore
{
    private readonly string _connectionString;

    public SqliteStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
              handle      TEXT PRIMARY KEY,
              token_hash  TEXT NOT NULL,
              created_at  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
              id            TEXT PRIMARY KEY,
              from_handle   TEXT NOT NULL REFERENCES users(handle),
              to_handle     TEXT NOT NULL REFERENCES users(handle),
              subject       TEXT NOT NULL,
              body          TEXT NOT NULL,
              metadata_json TEXT,
              created_at    TEXT NOT NULL,
              read_at       TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_msg_to_unread ON messages(to_handle, read_at);
            CREATE INDEX IF NOT EXISTS idx_msg_to_created ON messages(to_handle, created_at);
            CREATE TABLE IF NOT EXISTS invites (
              code        TEXT PRIMARY KEY,
              handle      TEXT NOT NULL,
              token       TEXT NOT NULL,
              created_at  TEXT NOT NULL,
              expires_at  TEXT NOT NULL,
              redeemed_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_invites_handle ON invites(handle);
            """;
        cmd.ExecuteNonQuery();
    }

    public (bool created, string token) CreateUser(string handle)
    {
        using var conn = Open();
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT 1 FROM users WHERE handle = $h";
        check.Parameters.AddWithValue("$h", handle);
        if (check.ExecuteScalar() is not null) return (false, "");

        var token = GenerateToken(handle);
        var hash = BCrypt.Net.BCrypt.HashPassword(token, workFactor: 11);
        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO users(handle, token_hash, created_at) VALUES ($h, $t, $c)";
        insert.Parameters.AddWithValue("$h", handle);
        insert.Parameters.AddWithValue("$t", hash);
        insert.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
        return (true, token);
    }

    public string? AuthenticateBearer(string bearer)
    {
        if (!bearer.StartsWith("ob_", StringComparison.Ordinal)) return null;
        var parts = bearer.Split('_', 3);
        if (parts.Length != 3 || parts[1].Length == 0) return null;
        var handle = parts[1];
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT token_hash FROM users WHERE handle = $h";
        cmd.Parameters.AddWithValue("$h", handle);
        if (cmd.ExecuteScalar() is not string hash) return null;
        return BCrypt.Net.BCrypt.Verify(bearer, hash) ? handle : null;
    }

    public bool UserExists(string handle)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM users WHERE handle = $h";
        cmd.Parameters.AddWithValue("$h", handle);
        return cmd.ExecuteScalar() is not null;
    }

    public string InsertMessage(string from, string to, string subject, string body, string? metadataJson)
    {
        var id = "msg_" + Guid.CreateVersion7().ToString("N");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages(id, from_handle, to_handle, subject, body, metadata_json, created_at, read_at)
            VALUES ($id, $from, $to, $subj, $body, $meta, $created, NULL)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);
        cmd.Parameters.AddWithValue("$subj", subject);
        cmd.Parameters.AddWithValue("$body", body);
        cmd.Parameters.AddWithValue("$meta", (object?)metadataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return id;
    }

    public List<InboxItem> ListInbox(string handle, bool unreadOnly, DateTimeOffset? since, int limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sql = "SELECT id, from_handle, to_handle, subject, created_at, read_at FROM messages WHERE to_handle = $h";
        if (unreadOnly) sql += " AND read_at IS NULL";
        if (since.HasValue) sql += " AND created_at > $since";
        sql += " ORDER BY created_at DESC LIMIT $lim";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$h", handle);
        if (since.HasValue) cmd.Parameters.AddWithValue("$since", since.Value.ToString("O"));
        cmd.Parameters.AddWithValue("$lim", limit);
        var items = new List<InboxItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            items.Add(new InboxItem(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture),
                !r.IsDBNull(5)));
        }
        return items;
    }

    public Message? GetMessage(string id, string requester)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, from_handle, to_handle, subject, body, metadata_json, created_at, read_at
            FROM messages
            WHERE id = $id AND (from_handle = $r OR to_handle = $r)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$r", requester);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Message(
            r.GetString(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5),
            DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture),
            r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture));
    }

    public bool AckMessage(string id, string recipient)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE messages SET read_at = $now
            WHERE id = $id AND to_handle = $r AND read_at IS NULL
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$r", recipient);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public string CreateInvite(string handle, TimeSpan ttl)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT 1 FROM users WHERE handle = $h";
            check.Parameters.AddWithValue("$h", handle);
            if (check.ExecuteScalar() is not null)
                throw new InvalidOperationException("handle_exists");
        }

        var token = GenerateToken(handle);
        var hash = BCrypt.Net.BCrypt.HashPassword(token, workFactor: 11);
        using (var insertUser = conn.CreateCommand())
        {
            insertUser.Transaction = tx;
            insertUser.CommandText = "INSERT INTO users(handle, token_hash, created_at) VALUES ($h, $t, $c)";
            insertUser.Parameters.AddWithValue("$h", handle);
            insertUser.Parameters.AddWithValue("$t", hash);
            insertUser.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
            insertUser.ExecuteNonQuery();
        }

        var code = GenerateInviteCode();
        using (var insertInvite = conn.CreateCommand())
        {
            insertInvite.Transaction = tx;
            insertInvite.CommandText = """
                INSERT INTO invites(code, handle, token, created_at, expires_at, redeemed_at)
                VALUES ($c, $h, $t, $now, $exp, NULL)
                """;
            insertInvite.Parameters.AddWithValue("$c", code);
            insertInvite.Parameters.AddWithValue("$h", handle);
            insertInvite.Parameters.AddWithValue("$t", token);
            insertInvite.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            insertInvite.Parameters.AddWithValue("$exp", DateTimeOffset.UtcNow.Add(ttl).ToString("O"));
            insertInvite.ExecuteNonQuery();
        }

        tx.Commit();
        return code;
    }

    public (string handle, string token)? RedeemInvite(string code)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        string handle, token;
        DateTimeOffset expires;
        bool alreadyRedeemed;

        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT handle, token, expires_at, redeemed_at FROM invites WHERE code = $c";
            sel.Parameters.AddWithValue("$c", code);
            using var r = sel.ExecuteReader();
            if (!r.Read()) return null;
            handle = r.GetString(0);
            token = r.GetString(1);
            expires = DateTimeOffset.Parse(r.GetString(2), System.Globalization.CultureInfo.InvariantCulture);
            alreadyRedeemed = !r.IsDBNull(3);
        }

        if (alreadyRedeemed) return null;
        if (DateTimeOffset.UtcNow > expires) return null;

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = "UPDATE invites SET redeemed_at = $now WHERE code = $c AND redeemed_at IS NULL";
            upd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            upd.Parameters.AddWithValue("$c", code);
            if (upd.ExecuteNonQuery() != 1) return null;
        }

        tx.Commit();
        return (handle, token);
    }

    private static string GenerateToken(string handle)
    {
        Span<byte> buf = stackalloc byte[24];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        return $"ob_{handle}_{Convert.ToHexString(buf).ToLowerInvariant()}";
    }

    private static string GenerateInviteCode()
    {
        Span<byte> buf = stackalloc byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var chars = new char[12];
        for (var i = 0; i < 12; i++) chars[i] = alphabet[buf[i] % alphabet.Length];
        return new string(chars);
    }
}
