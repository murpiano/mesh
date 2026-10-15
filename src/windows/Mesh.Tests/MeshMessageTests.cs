using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class MeshMessageTests
{
    [Fact]
    public async Task ChatSend_RoundTrips()
    {
        using var stream = new MemoryStream();
        var id = Guid.NewGuid().ToString();
        var sent = new ChatSendMessage(id, DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000), "hello world");
        await MeshMessage.WriteAsync(stream, sent);
        stream.Position = 0;

        var received = await MeshMessage.ReadAsync(stream);

        var push = Assert.IsType<ChatSendMessage>(received);
        Assert.Equal(id, push.Id);
        Assert.Equal(sent.Timestamp, push.Timestamp);
        Assert.Equal("hello world", push.Text);
    }

    [Fact]
    public async Task ChatEdit_RoundTrips()
    {
        using var stream = new MemoryStream();
        var id = Guid.NewGuid().ToString();
        await MeshMessage.WriteAsync(stream, new ChatEditMessage(id, "edited text"));
        stream.Position = 0;

        var received = await MeshMessage.ReadAsync(stream);

        var edit = Assert.IsType<ChatEditMessage>(received);
        Assert.Equal(id, edit.Id);
        Assert.Equal("edited text", edit.Text);
    }

    [Fact]
    public async Task ChatDelete_RoundTrips()
    {
        using var stream = new MemoryStream();
        var id = Guid.NewGuid().ToString();
        await MeshMessage.WriteAsync(stream, new ChatDeleteMessage(id));
        stream.Position = 0;

        var received = await MeshMessage.ReadAsync(stream);

        var delete = Assert.IsType<ChatDeleteMessage>(received);
        Assert.Equal(id, delete.Id);
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
    public async Task ChatHistorySync_RoundTrips()
    {
        using var stream = new MemoryStream();
        var entries = new[]
        {
            new ChatSyncEntry(Guid.NewGuid().ToString(), "self-uuid",
                DateTimeOffset.FromUnixTimeMilliseconds(100), "first"),
            new ChatSyncEntry(Guid.NewGuid().ToString(), "peer-uuid",
                DateTimeOffset.FromUnixTimeMilliseconds(200), "second"),
        };
        await MeshMessage.WriteAsync(stream, new ChatHistorySyncMessage(entries));
        stream.Position = 0;

        var received = Assert.IsType<ChatHistorySyncMessage>(await MeshMessage.ReadAsync(stream));

        Assert.Equal(2, received.Entries.Count);
        Assert.Equal(entries[0], received.Entries[0]);
        Assert.Equal(entries[1], received.Entries[1]);
    }

    [Fact]
    public async Task ChatHistorySync_Empty_RoundTrips()
    {
        using var stream = new MemoryStream();
        await MeshMessage.WriteAsync(stream, new ChatHistorySyncMessage(Array.Empty<ChatSyncEntry>()));
        stream.Position = 0;

        var received = Assert.IsType<ChatHistorySyncMessage>(await MeshMessage.ReadAsync(stream));

        Assert.Empty(received.Entries);
    }

    [Fact]
    public async Task MultipleMessages_RoundTripInOrder_OnSameStream()
    {
        using var stream = new MemoryStream();
        await MeshMessage.WriteAsync(stream, new ChatSendMessage(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, "first"));
        await MeshMessage.WriteAsync(stream, new HeartbeatMessage());
        await MeshMessage.WriteAsync(stream, new ChatSendMessage(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, "second"));
        stream.Position = 0;

        var first = Assert.IsType<ChatSendMessage>(await MeshMessage.ReadAsync(stream));
        Assert.Equal("first", first.Text);
        Assert.IsType<HeartbeatMessage>(await MeshMessage.ReadAsync(stream));
        var second = Assert.IsType<ChatSendMessage>(await MeshMessage.ReadAsync(stream));
        Assert.Equal("second", second.Text);
    }

    [Fact]
    public async Task ChatSend_LargerThanPairingStringCap_RoundTrips()
    {
        // Regression: the text field used to go through PairingProtocol.WriteStringAsync,
        // which caps strings at 1024 bytes, so any real-sized message threw on write.
        var text = string.Concat(Enumerable.Repeat("chat-message-", 500)) + "é中\U0001F600";
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) > PairingProtocol.MaxStringBytes);
        using var stream = new MemoryStream();
        var sent = new ChatSendMessage(Guid.NewGuid().ToString(), DateTimeOffset.FromUnixTimeMilliseconds(42), text);
        await MeshMessage.WriteAsync(stream, sent);
        stream.Position = 0;

        var push = Assert.IsType<ChatSendMessage>(await MeshMessage.ReadAsync(stream));
        Assert.Equal(sent.Timestamp, push.Timestamp);
        Assert.Equal(text, push.Text);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task ChatSend_LengthPrefixAboveCap_IsRejected()
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0x01); // ChatSendTag
        var emptyIdHeader = new byte[4]; // Id: zero-length string
        stream.Write(emptyIdHeader);
        stream.Write(new byte[8]); // timestamp
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)MeshMessage.MaxTextBytes + 1);
        stream.Write(header);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => MeshMessage.ReadAsync(stream));
    }
}
