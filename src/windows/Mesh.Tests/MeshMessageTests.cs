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
}
