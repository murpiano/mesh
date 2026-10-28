using System.Buffers.Binary;

namespace Mesh.Core;

/// <summary>What one media-plane frame carries.</summary>
public enum CastFrameType : byte
{
    /// <summary>The video codec's configuration (H.264 SPS/PPS). Sent before the first picture
    /// and again whenever the encoder restarts — a resolution change or a rotation.</summary>
    VideoConfig = 1,
    /// <summary>A keyframe: decodable on its own, which is what a receiver that just connected
    /// waits for.</summary>
    VideoKey = 2,
    VideoDelta = 3,
    /// <summary>The audio codec's configuration (AAC). Sent when audio is switched on.</summary>
    AudioConfig = 4,
    AudioData = 5
}

/// <summary>
/// One frame on the media plane: the second connection of a cast, which carries nothing but
/// these. The control plane (<see cref="MeshMessage"/>) never carries a frame, and this never
/// carries a control message — that separation is what keeps a heartbeat from waiting behind a
/// picture.
///
/// Mirrors the Android app's CastFrame.kt byte for byte: 1 byte type, 8 bytes big-endian
/// microsecond timestamp, 4 bytes big-endian payload length, payload.
/// </summary>
public sealed record CastFrame(CastFrameType Type, long TimestampUs, ReadOnlyMemory<byte> Payload)
{
    /// <summary>Far above any single encoded frame at the bitrates this uses, and small enough
    /// that a hostile length prefix cannot make the receiver allocate its way out of memory.</summary>
    public const int MaxPayloadBytes = 8 * 1_048_576;

    public static async Task WriteAsync(Stream stream, CastFrame frame)
    {
        if (frame.Payload.Length > MaxPayloadBytes)
            throw new ArgumentException($"Cast frame payload is {frame.Payload.Length} bytes (max {MaxPayloadBytes}).");

        var header = new byte[13];
        header[0] = (byte)frame.Type;
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(1, 8), frame.TimestampUs);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(9, 4), frame.Payload.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(frame.Payload);
        await stream.FlushAsync();
    }

    public static async Task<CastFrame> ReadAsync(Stream stream)
    {
        var header = await PairingProtocol.ReadBytesAsync(stream, 13);
        var type = (CastFrameType)header[0];
        if (!Enum.IsDefined(type)) throw new InvalidDataException($"Peer sent an unknown cast frame type: {header[0]}.");
        var timestampUs = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(1, 8));
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(9, 4));
        // Validate before allocating so a hostile length prefix cannot force a huge buffer.
        if (length is < 0 or > MaxPayloadBytes)
            throw new InvalidDataException($"Peer sent a cast frame of {length} bytes (max {MaxPayloadBytes}).");
        var payload = await PairingProtocol.ReadBytesAsync(stream, length);
        return new CastFrame(type, timestampUs, payload);
    }
}
