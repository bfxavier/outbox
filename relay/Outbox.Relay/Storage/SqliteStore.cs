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

    private static string GenerateToken(string handle)
    {
        Span<byte> buf = stackalloc byte[24];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        return $"ob_{handle}_{Convert.ToHexString(buf).ToLowerInvariant()}";
    }
}
