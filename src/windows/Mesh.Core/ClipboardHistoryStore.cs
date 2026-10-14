// src/windows/Mesh.Core/ClipboardHistoryStore.cs
using Microsoft.Data.Sqlite;

namespace Mesh.Core;

public sealed record ClipboardEntry(long Id, string Text, string Source, DateTimeOffset Timestamp);

public static class ClipboardHistoryStore
{
    private const int Cap = 50;

    public static ClipboardEntry Insert(string dbPath, string text, string source, DateTimeOffset timestamp)
    {
        using var connection = Open(dbPath);
        using var transaction = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO clipboard_history (text, source, timestamp) VALUES ($text, $source, $timestamp)";
            insert.Parameters.AddWithValue("$text", text);
            insert.Parameters.AddWithValue("$source", source);
            insert.Parameters.AddWithValue("$timestamp", timestamp.ToUnixTimeMilliseconds());
            insert.ExecuteNonQuery();
        }

        long newId;
        using (var idCmd = connection.CreateCommand())
        {
            idCmd.Transaction = transaction;
            idCmd.CommandText = "SELECT last_insert_rowid()";
            newId = (long)idCmd.ExecuteScalar()!;
        }

        using (var evict = connection.CreateCommand())
        {
            evict.Transaction = transaction;
            evict.CommandText = """
                DELETE FROM clipboard_history
                WHERE id NOT IN (
                    SELECT id FROM clipboard_history ORDER BY timestamp DESC, id DESC LIMIT $cap
                )
                """;
            evict.Parameters.AddWithValue("$cap", Cap);
            evict.ExecuteNonQuery();
        }

        transaction.Commit();
        return new ClipboardEntry(newId, text, source, timestamp);
    }

    public static List<ClipboardEntry> GetAll(string dbPath)
    {
        if (!File.Exists(dbPath)) return new List<ClipboardEntry>();

        using var connection = Open(dbPath);
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id, text, source, timestamp FROM clipboard_history ORDER BY timestamp DESC, id DESC";
        using var reader = select.ExecuteReader();

        var results = new List<ClipboardEntry>();
        while (reader.Read())
        {
            results.Add(new ClipboardEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
        }
        return results;
    }

    private static SqliteConnection Open(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS clipboard_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                text TEXT NOT NULL,
                source TEXT NOT NULL,
                timestamp INTEGER NOT NULL
            )
            """;
        create.ExecuteNonQuery();
        return connection;
    }
}
