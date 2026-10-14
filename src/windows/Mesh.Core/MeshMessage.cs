namespace Mesh.Core;

public abstract class MeshMessage
{
    private const byte ClipboardPushTag = 0x01;
    private const byte HeartbeatTag = 0x02;

    public static async Task WriteAsync(Stream stream, MeshMessage message)
    {
        switch (message)
        {
            case ClipboardPushMessage push:
                await PairingProtocol.WriteByteAsync(stream, ClipboardPushTag);
                var tsBytes = BitConverter.GetBytes(push.Timestamp.ToUnixTimeMilliseconds());
                if (BitConverter.IsLittleEndian) Array.Reverse(tsBytes);
                await PairingProtocol.WriteBytesAsync(stream, tsBytes);
                await PairingProtocol.WriteStringAsync(stream, push.Text);
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
        var text = await PairingProtocol.ReadStringAsync(stream);
        return new ClipboardPushMessage(timestamp, text);
    }
}

public sealed class ClipboardPushMessage(DateTimeOffset timestamp, string text) : MeshMessage
{
    public DateTimeOffset Timestamp { get; } = timestamp;
    public string Text { get; } = text;
}

public sealed class HeartbeatMessage : MeshMessage;
