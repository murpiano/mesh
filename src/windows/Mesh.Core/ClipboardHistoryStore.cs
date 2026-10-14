// src/windows/Mesh.Core/ClipboardHistoryStore.cs
using Microsoft.Data.Sqlite;

namespace Mesh.Core;

public sealed record ClipboardEntry(long Id, string Text, string Source, DateTimeOffset Timestamp)
{
    /// <summary>Longest <see cref="Text"/> prefix the history list UI will render directly.</summary>
    public const int DisplayTextMaxLength = 500;

    /// <summary>
    /// <see cref="Text"/> truncated to <see cref="DisplayTextMaxLength"/> characters (plus an
    /// ellipsis) for list-view rendering. Clips can be up to 4 MiB of text; rendering that much
    /// through a wrapping <c>TextBlock</c> with no cap can freeze the WPF layout thread, so the
    /// view binds to this instead of <see cref="Text"/>. The "Copy" action still uses the full,
    /// untruncated <see cref="Text"/> (it binds to the whole entry via the button's Tag).
    /// </summary>
    public string DisplayText => Text.Length > DisplayTextMaxLength
        ? Text[..DisplayTextMaxLength] + "…"
        : Text;
}

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
