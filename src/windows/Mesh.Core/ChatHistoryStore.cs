// src/windows/Mesh.Core/ChatHistoryStore.cs
using Microsoft.Data.Sqlite;

namespace Mesh.Core;

/// <summary>One chat message, scoped to the conversation with <see cref="PeerUuid"/>.
/// <see cref="MsgId"/> is a globally unique id assigned at creation (by whichever side wrote the
/// message), used to dedupe during a <see cref="ChatHistorySyncMessage"/> merge. <see
/// cref="SenderUuid"/> is a device uuid (either side's) rather than a perspective-relative label,
/// so attribution survives being replayed through a history sync.</summary>
public sealed record ChatMessage(
    string MsgId, string PeerUuid, string SenderUuid, string SenderLabel, string Text, DateTimeOffset Timestamp)
{
    /// <summary>Longest <see cref="Text"/> prefix the history list UI will render directly.</summary>
    public const int DisplayTextMaxLength = 500;

    /// <summary>
    /// <see cref="Text"/> truncated to <see cref="DisplayTextMaxLength"/> characters (plus an
    /// ellipsis) for list-view rendering. Messages can be up to 4 MiB of text; rendering that
    /// much through a wrapping <c>TextBlock</c> with no cap can freeze the WPF layout thread, so
    /// the view binds to this instead of <see cref="Text"/>. The "Copy" action still uses the
    /// full, untruncated <see cref="Text"/> (it binds to the whole entry via the button's Tag).
    /// </summary>
    public string DisplayText => Text.Length > DisplayTextMaxLength
        ? Text[..DisplayTextMaxLength] + "…"
        : Text;
}

public static class ChatHistoryStore
{
    /// <summary>Messages kept per conversation (per <c>peer_uuid</c>), not globally — each paired
    /// device gets its own 50-message window.</summary>
    private const int CapPerPeer = 50;

    /// <summary>Inserts a message, or does nothing if <paramref name="msgId"/> is already stored
    /// (e.g. replayed back by a history sync the receiver already has). Returns null on that
    /// duplicate case so callers know not to add a second copy to the UI.</summary>
    public static ChatMessage? Insert(string dbPath, string msgId, string peerUuid, string senderUuid,
        string senderLabel, string text, DateTimeOffset timestamp)
    {
        using var connection = Open(dbPath);
        using var transaction = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO chat_messages (msg_id, peer_uuid, sender_uuid, sender_label, text, timestamp)
                VALUES ($msgId, $peerUuid, $senderUuid, $senderLabel, $text, $timestamp)
                """;
            insert.Parameters.AddWithValue("$msgId", msgId);
            insert.Parameters.AddWithValue("$peerUuid", peerUuid);
            insert.Parameters.AddWithValue("$senderUuid", senderUuid);
            insert.Parameters.AddWithValue("$senderLabel", senderLabel);
            insert.Parameters.AddWithValue("$text", text);
            insert.Parameters.AddWithValue("$timestamp", timestamp.ToUnixTimeMilliseconds());
            var inserted = insert.ExecuteNonQuery();
            if (inserted == 0)
            {
                transaction.Commit();
                return null; // msg_id already present
            }
        }

        using (var evict = connection.CreateCommand())
        {
            evict.Transaction = transaction;
            evict.CommandText = """
                DELETE FROM chat_messages
                WHERE peer_uuid = $peerUuid AND msg_id NOT IN (
                    SELECT msg_id FROM chat_messages WHERE peer_uuid = $peerUuid
                    ORDER BY timestamp DESC, msg_id DESC LIMIT $cap
                )
                """;
            evict.Parameters.AddWithValue("$peerUuid", peerUuid);
            evict.Parameters.AddWithValue("$cap", CapPerPeer);
            evict.ExecuteNonQuery();
        }

        transaction.Commit();
        return new ChatMessage(msgId, peerUuid, senderUuid, senderLabel, text, timestamp);
    }

    /// <summary>All messages for one conversation, oldest first (chat order).</summary>
    public static List<ChatMessage> GetAllForPeer(string dbPath, string peerUuid)
    {
        if (!File.Exists(dbPath)) return new List<ChatMessage>();

        using var connection = Open(dbPath);
        using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT msg_id, peer_uuid, sender_uuid, sender_label, text, timestamp FROM chat_messages
            WHERE peer_uuid = $peerUuid ORDER BY timestamp ASC, msg_id ASC
            """;
        select.Parameters.AddWithValue("$peerUuid", peerUuid);
        using var reader = select.ExecuteReader();

        var results = new List<ChatMessage>();
        while (reader.Read())
        {
            results.Add(new ChatMessage(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
        }
        return results;
    }

    /// <summary>Removes the whole conversation with <paramref name="peerUuid"/> — called when the
    /// user unpairs that device (see spec: unpairing loses the chat too, same as the trust
    /// record; the peer's own copy is untouched and restores ours on a later re-pair).</summary>
    public static void DeleteForPeer(string dbPath, string peerUuid)
    {
        if (!File.Exists(dbPath)) return;
        using var connection = Open(dbPath);
        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM chat_messages WHERE peer_uuid = $peerUuid";
        delete.Parameters.AddWithValue("$peerUuid", peerUuid);
        delete.ExecuteNonQuery();
    }

    /// <summary>Edits one message's text in place. No-op if <paramref name="msgId"/> is not
    /// stored for <paramref name="peerUuid"/>.</summary>
    public static void UpdateText(string dbPath, string peerUuid, string msgId, string newText)
    {
        using var connection = Open(dbPath);
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE chat_messages SET text = $text WHERE peer_uuid = $peerUuid AND msg_id = $msgId";
        update.Parameters.AddWithValue("$text", newText);
        update.Parameters.AddWithValue("$peerUuid", peerUuid);
        update.Parameters.AddWithValue("$msgId", msgId);
        update.ExecuteNonQuery();
    }

    /// <summary>Removes one message. No-op if <paramref name="msgId"/> is not stored for
    /// <paramref name="peerUuid"/>.</summary>
    public static void DeleteMessage(string dbPath, string peerUuid, string msgId)
    {
        using var connection = Open(dbPath);
        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM chat_messages WHERE peer_uuid = $peerUuid AND msg_id = $msgId";
        delete.Parameters.AddWithValue("$peerUuid", peerUuid);
        delete.Parameters.AddWithValue("$msgId", msgId);
        delete.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Pooling=False: without it, Microsoft.Data.Sqlite keeps the underlying file handle
        // open in a pool after Dispose, so a test that deletes the db file right after using it
        // can race a handle the pool hasn't released yet (IOException: file in use).
        var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS chat_messages (
                msg_id TEXT PRIMARY KEY,
                peer_uuid TEXT NOT NULL,
                sender_uuid TEXT NOT NULL,
                sender_label TEXT NOT NULL,
                text TEXT NOT NULL,
                timestamp INTEGER NOT NULL
            )
            """;
        create.ExecuteNonQuery();
        using var index = connection.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS idx_chat_messages_peer ON chat_messages(peer_uuid)";
        index.ExecuteNonQuery();
        return connection;
    }
}
