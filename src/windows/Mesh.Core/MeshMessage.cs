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
                await stream.WriteAsync(new[] { ClipboardPushTag });
                var tsBytes = BitConverter.GetBytes(push.Timestamp.ToUnixTimeMilliseconds());
                if (BitConverter.IsLittleEndian) Array.Reverse(tsBytes);
                await stream.WriteAsync(tsBytes);
                await PairingProtocol.WriteStringAsync(stream, push.Text);
                break;
            case HeartbeatMessage:
                await stream.WriteAsync(new[] { HeartbeatTag });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(message), "Unknown message type.");
        }
        await stream.FlushAsync();
    }

    public static async Task<MeshMessage> ReadAsync(Stream stream)
    {
        var tagBuf = new byte[1];
        await ReadExactAsync(stream, tagBuf);
        return tagBuf[0] switch
        {
            ClipboardPushTag => await ReadClipboardPushAsync(stream),
            HeartbeatTag => new HeartbeatMessage(),
            _ => throw new InvalidDataException($"Unknown message tag: {tagBuf[0]}")
        };
    }

    private static async Task<ClipboardPushMessage> ReadClipboardPushAsync(Stream stream)
    {
        var tsBuf = new byte[8];
        await ReadExactAsync(stream, tsBuf);
        if (BitConverter.IsLittleEndian) Array.Reverse(tsBuf);
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(BitConverter.ToInt64(tsBuf));
        var text = await PairingProtocol.ReadStringAsync(stream);
        return new ClipboardPushMessage(timestamp, text);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}

public sealed class ClipboardPushMessage(DateTimeOffset timestamp, string text) : MeshMessage
{
    public DateTimeOffset Timestamp { get; } = timestamp;
    public string Text { get; } = text;
}

public sealed class HeartbeatMessage : MeshMessage;
