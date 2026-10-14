using System.Buffers.Binary;
using System.Text;

namespace Mesh.Core;

public abstract class MeshMessage
{
    private const byte ClipboardPushTag = 0x01;
    private const byte HeartbeatTag = 0x02;

    /// <summary>
    /// Upper bound on a ClipboardPush text field's UTF-8 byte length. 1 MiB of UTF-16 code units
    /// expands to at most 3 bytes per code unit in UTF-8 (a surrogate pair contributes 4 bytes
    /// across 2 code units, not 4 bytes per single code unit); 4x gives comfortable headroom
    /// over that worst case. Deliberately independent of
    /// <see cref="PairingProtocol.MaxStringBytes"/>, which is sized for short handshake strings.
    /// </summary>
    public const int MaxTextBytes = 4 * 1_048_576;

    public static async Task WriteAsync(Stream stream, MeshMessage message)
    {
        switch (message)
        {
            case ClipboardPushMessage push:
                await PairingProtocol.WriteByteAsync(stream, ClipboardPushTag);
                var tsBytes = BitConverter.GetBytes(push.Timestamp.ToUnixTimeMilliseconds());
                if (BitConverter.IsLittleEndian) Array.Reverse(tsBytes);
                await PairingProtocol.WriteBytesAsync(stream, tsBytes);
                await WriteTextAsync(stream, push.Text);
                break;
            case HeartbeatMessage:
                await PairingProtocol.WriteByteAsync(stream, HeartbeatTag);
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
            ClipboardPushTag => await ReadClipboardPushAsync(stream),
            HeartbeatTag => new HeartbeatMessage(),
            _ => throw new InvalidDataException($"Unknown message tag: {tag}")
        };
    }

    private static async Task<ClipboardPushMessage> ReadClipboardPushAsync(Stream stream)
    {
        var tsBuf = await PairingProtocol.ReadBytesAsync(stream, 8);
        if (BitConverter.IsLittleEndian) Array.Reverse(tsBuf);
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(BitConverter.ToInt64(tsBuf));
        var text = await ReadTextAsync(stream);
        return new ClipboardPushMessage(timestamp, text);
    }

    // Same framing concept as PairingProtocol's strings (u32 big-endian byte length + UTF-8
    // bytes) but self-contained so the 1024-byte handshake cap does not apply to clipboard text.
    private static async Task WriteTextAsync(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxTextBytes)
            throw new ArgumentException($"ClipboardPush text is {bytes.Length} bytes (max {MaxTextBytes}).");
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
            throw new InvalidDataException($"Peer sent ClipboardPush text of {length} bytes (max {MaxTextBytes}).");
        var bytes = await PairingProtocol.ReadBytesAsync(stream, (int)length);
        return Encoding.UTF8.GetString(bytes);
    }
}

public sealed class ClipboardPushMessage(DateTimeOffset timestamp, string text) : MeshMessage
{
    public DateTimeOffset Timestamp { get; } = timestamp;
    public string Text { get; } = text;
}

public sealed class HeartbeatMessage : MeshMessage;
