using System.Buffers.Binary;
using System.Text;

namespace Mesh.Core;

public abstract class MeshMessage
{
    private const byte ChatSendTag = 0x01;
    private const byte HeartbeatTag = 0x02;
    private const byte GoodbyeTag = 0x03;
    private const byte ChatHistorySyncTag = 0x04;
    private const byte ChatEditTag = 0x05;
    private const byte ChatDeleteTag = 0x06;

    /// <summary>
    /// Upper bound on a chat message's UTF-8 byte length. 1 MiB of UTF-16 code units expands to
    /// at most 3 bytes per code unit in UTF-8 (a surrogate pair contributes 4 bytes across 2 code
    /// units, not 4 bytes per single code unit); 4x gives comfortable headroom over that worst
    /// case. Deliberately independent of <see cref="PairingProtocol.MaxStringBytes"/>, which is
    /// sized for short handshake strings.
    /// </summary>
    public const int MaxTextBytes = 4 * 1_048_576;

    /// <summary>
    /// Upper bound on a chat message's length in UTF-16 code units — what both apps check before
    /// storing or sending, so neither offers to send what the other would drop. Derived from
    /// <see cref="MaxTextBytes"/> rather than written out, because a text this long always fits
    /// the wire limit (see the 3-bytes-per-code-unit worst case above).
    /// </summary>
    public const int MaxTextChars = MaxTextBytes / 4;

    /// <summary>Upper bound on how many entries a single history sync carries. Generous relative
    /// to the 50-per-peer cap each side actually stores, so a sync can never be truncated.</summary>
    public const int MaxHistorySyncEntries = 1000;

    public static async Task WriteAsync(Stream stream, MeshMessage message)
    {
        switch (message)
        {
            case ChatSendMessage send:
                await PairingProtocol.WriteByteAsync(stream, ChatSendTag);
                await PairingProtocol.WriteStringAsync(stream, send.Id);
                await WriteTimestampAsync(stream, send.Timestamp);
                await WriteTextAsync(stream, send.Text);
                break;
            case HeartbeatMessage:
                await PairingProtocol.WriteByteAsync(stream, HeartbeatTag);
                break;
            case GoodbyeMessage:
                await PairingProtocol.WriteByteAsync(stream, GoodbyeTag);
                break;
            case ChatHistorySyncMessage sync:
                await PairingProtocol.WriteByteAsync(stream, ChatHistorySyncTag);
                if (sync.Entries.Count > MaxHistorySyncEntries)
                    throw new ArgumentException($"History sync has {sync.Entries.Count} entries (max {MaxHistorySyncEntries}).");
                var countBytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(countBytes, (uint)sync.Entries.Count);
                await stream.WriteAsync(countBytes);
                foreach (var entry in sync.Entries)
                {
                    await PairingProtocol.WriteStringAsync(stream, entry.Id);
                    await PairingProtocol.WriteStringAsync(stream, entry.SenderUuid);
                    await WriteTimestampAsync(stream, entry.Timestamp);
                    await WriteTextAsync(stream, entry.Text);
                }
                break;
            case ChatEditMessage edit:
                await PairingProtocol.WriteByteAsync(stream, ChatEditTag);
                await PairingProtocol.WriteStringAsync(stream, edit.Id);
                await WriteTextAsync(stream, edit.Text);
                break;
            case ChatDeleteMessage delete:
                await PairingProtocol.WriteByteAsync(stream, ChatDeleteTag);
                await PairingProtocol.WriteStringAsync(stream, delete.Id);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(message), "Unknown message type.");
        }
        await stream.FlushAsync();
    }

    public static async Task<MeshMessage> ReadAsync(Stream stream)
    {
        var tag = await PairingProtocol.ReadByteAsync(stream);
        return tag switch
        {
            ChatSendTag => await ReadChatSendAsync(stream),
            HeartbeatTag => new HeartbeatMessage(),
            GoodbyeTag => new GoodbyeMessage(),
            ChatHistorySyncTag => await ReadChatHistorySyncAsync(stream),
            ChatEditTag => new ChatEditMessage(await PairingProtocol.ReadStringAsync(stream), await ReadTextAsync(stream)),
            ChatDeleteTag => new ChatDeleteMessage(await PairingProtocol.ReadStringAsync(stream)),
            _ => throw new InvalidDataException($"Unknown message tag: {tag}")
        };
    }

    private static async Task<ChatSendMessage> ReadChatSendAsync(Stream stream)
    {
        var id = await PairingProtocol.ReadStringAsync(stream);
        var timestamp = await ReadTimestampAsync(stream);
        var text = await ReadTextAsync(stream);
        return new ChatSendMessage(id, timestamp, text);
    }

    private static async Task<ChatHistorySyncMessage> ReadChatHistorySyncAsync(Stream stream)
    {
        var countBuf = await PairingProtocol.ReadBytesAsync(stream, 4);
        var count = BinaryPrimitives.ReadUInt32BigEndian(countBuf);
        if (count > MaxHistorySyncEntries)
            throw new InvalidDataException($"Peer sent a history sync of {count} entries (max {MaxHistorySyncEntries}).");
        var entries = new List<ChatSyncEntry>((int)count);
        for (var i = 0; i < count; i++)
        {
            var id = await PairingProtocol.ReadStringAsync(stream);
            var senderUuid = await PairingProtocol.ReadStringAsync(stream);
            var timestamp = await ReadTimestampAsync(stream);
            var text = await ReadTextAsync(stream);
            entries.Add(new ChatSyncEntry(id, senderUuid, timestamp, text));
        }
        return new ChatHistorySyncMessage(entries);
    }

    private static async Task WriteTimestampAsync(Stream stream, DateTimeOffset timestamp)
    {
        var bytes = BitConverter.GetBytes(timestamp.ToUnixTimeMilliseconds());
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        await PairingProtocol.WriteBytesAsync(stream, bytes);
    }

    private static async Task<DateTimeOffset> ReadTimestampAsync(Stream stream)
    {
        var buf = await PairingProtocol.ReadBytesAsync(stream, 8);
        if (BitConverter.IsLittleEndian) Array.Reverse(buf);
        return DateTimeOffset.FromUnixTimeMilliseconds(BitConverter.ToInt64(buf));
    }

    // Same framing concept as PairingProtocol's strings (u32 big-endian byte length + UTF-8
    // bytes) but self-contained so the 1024-byte handshake cap does not apply to chat text.
    private static async Task WriteTextAsync(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxTextBytes)
            throw new ArgumentException($"Chat message text is {bytes.Length} bytes (max {MaxTextBytes}).");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)bytes.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(bytes);
    }

    private static async Task<string> ReadTextAsync(Stream stream)
    {
        var header = await PairingProtocol.ReadBytesAsync(stream, 4);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        // Validate before allocating so a hostile length prefix cannot force a huge buffer.
        if (length > MaxTextBytes)
            throw new InvalidDataException($"Peer sent chat message text of {length} bytes (max {MaxTextBytes}).");
        var bytes = await PairingProtocol.ReadBytesAsync(stream, (int)length);
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>A single new chat message, sent live while both sides are connected. The sender's
/// identity is always the session's peer from the receiver's point of view, so it isn't carried
/// on the wire here (unlike <see cref="ChatSyncEntry"/>, which can describe either side).</summary>
public sealed class ChatSendMessage(string id, DateTimeOffset timestamp, string text) : MeshMessage
{
    public string Id { get; } = id;
    public DateTimeOffset Timestamp { get; } = timestamp;
    public string Text { get; } = text;
}

/// <summary>Sent once, right after a mesh session attaches (both directions, independently): the
/// sender's own locally-stored history for this conversation, so the other side can merge in
/// anything it's missing — e.g. after it unpaired and re-paired, losing its local copy.</summary>
public sealed class ChatHistorySyncMessage(IReadOnlyList<ChatSyncEntry> entries) : MeshMessage
{
    public IReadOnlyList<ChatSyncEntry> Entries { get; } = entries;
}

/// <summary><see cref="SenderUuid"/> is a device uuid (either side's), not perspective-relative,
/// so the receiver can tell whether an entry replayed back to it is one it originally sent.</summary>
public sealed record ChatSyncEntry(string Id, string SenderUuid, DateTimeOffset Timestamp, string Text);

/// <summary>Edits an own message already seen by the peer. Sent live only while connected to that
/// peer; if disconnected, the local store still changes but the peer is not retried later (see
/// MainWindow.EditChatMessage).</summary>
public sealed class ChatEditMessage(string id, string text) : MeshMessage
{
    public string Id { get; } = id;
    public string Text { get; } = text;
}

/// <summary>Deletes an own message already seen by the peer. Same best-effort delivery caveat as
/// <see cref="ChatEditMessage"/>.</summary>
public sealed class ChatDeleteMessage(string id) : MeshMessage
{
    public string Id { get; } = id;
}

public sealed class HeartbeatMessage : MeshMessage;

/// <summary>Sent once, right before an intentional (user-initiated) disconnect, so the peer
/// stops its own reconnect loop too instead of dialing back in a few seconds/minutes later.</summary>
public sealed class GoodbyeMessage : MeshMessage;
