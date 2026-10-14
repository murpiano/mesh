using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class MeshMessageTests
{
    [Fact]
    public async Task ClipboardPush_RoundTrips()
    {
        using var stream = new MemoryStream();
        var sent = new ClipboardPushMessage(DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000), "hello world");
        await MeshMessage.WriteAsync(stream, sent);
        stream.Position = 0;

        var received = await MeshMessage.ReadAsync(stream);

        var push = Assert.IsType<ClipboardPushMessage>(received);
        Assert.Equal(sent.Timestamp, push.Timestamp);
        Assert.Equal("hello world", push.Text);
    }

    [Fact]
    public async Task Heartbeat_RoundTrips()
    {
        using var stream = new MemoryStream();
        await MeshMessage.WriteAsync(stream, new HeartbeatMessage());
        stream.Position = 0;

        var received = await MeshMessage.ReadAsync(stream);

        Assert.IsType<HeartbeatMessage>(received);
    }

    [Fact]
    public async Task MultipleMessages_RoundTripInOrder_OnSameStream()
    {
        using var stream = new MemoryStream();
        await MeshMessage.WriteAsync(stream, new ClipboardPushMessage(DateTimeOffset.UtcNow, "first"));
        await MeshMessage.WriteAsync(stream, new HeartbeatMessage());
        await MeshMessage.WriteAsync(stream, new ClipboardPushMessage(DateTimeOffset.UtcNow, "second"));
        stream.Position = 0;

        var first = Assert.IsType<ClipboardPushMessage>(await MeshMessage.ReadAsync(stream));
        Assert.Equal("first", first.Text);
        Assert.IsType<HeartbeatMessage>(await MeshMessage.ReadAsync(stream));
        var second = Assert.IsType<ClipboardPushMessage>(await MeshMessage.ReadAsync(stream));
        Assert.Equal("second", second.Text);
    }

    [Fact]
    public async Task ClipboardPush_LargerThanPairingStringCap_RoundTrips()
    {
        // Regression: the text field used to go through PairingProtocol.WriteStringAsync,
        // which caps strings at 1024 bytes, so any real-sized clip threw on write.
        var text = string.Concat(Enumerable.Repeat("clipboard-", 500)) + "\u00e9\u4e2d\U0001F600";
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) > PairingProtocol.MaxStringBytes);
        using var stream = new MemoryStream();
        var sent = new ClipboardPushMessage(DateTimeOffset.FromUnixTimeMilliseconds(42), text);
        await MeshMessage.WriteAsync(stream, sent);
        stream.Position = 0;

        var push = Assert.IsType<ClipboardPushMessage>(await MeshMessage.ReadAsync(stream));
        Assert.Equal(sent.Timestamp, push.Timestamp);
        Assert.Equal(text, push.Text);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task ClipboardPush_LengthPrefixAboveCap_IsRejected()
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0x01);
        stream.Write(new byte[8]);
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)MeshMessage.MaxTextBytes + 1);
        stream.Write(header);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => MeshMessage.ReadAsync(stream));
    }
}
