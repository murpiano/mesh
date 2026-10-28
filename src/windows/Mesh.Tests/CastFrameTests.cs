using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class CastFrameTests
{
    [Theory]
    [InlineData(CastFrameType.VideoConfig)]
    [InlineData(CastFrameType.VideoKey)]
    [InlineData(CastFrameType.VideoDelta)]
    [InlineData(CastFrameType.AudioConfig)]
    [InlineData(CastFrameType.AudioData)]
    public async Task EveryFrameType_RoundTrips(CastFrameType type)
    {
        using var stream = new MemoryStream();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await CastFrame.WriteAsync(stream, new CastFrame(type, 1_234_567_890L, payload));
        stream.Position = 0;

        var received = await CastFrame.ReadAsync(stream);

        Assert.Equal(type, received.Type);
        Assert.Equal(1_234_567_890L, received.TimestampUs);
        Assert.Equal(payload, received.Payload.ToArray());
    }

    [Fact]
    public async Task PayloadAtTheLimit_RoundTrips()
    {
        using var stream = new MemoryStream();
        var payload = new byte[CastFrame.MaxPayloadBytes];
        payload[^1] = 42;
        await CastFrame.WriteAsync(stream, new CastFrame(CastFrameType.VideoKey, 0, payload));
        stream.Position = 0;

        var received = await CastFrame.ReadAsync(stream);

        Assert.Equal(CastFrame.MaxPayloadBytes, received.Payload.Length);
        Assert.Equal(42, received.Payload.Span[^1]);
    }

    [Fact]
    public async Task PayloadOverTheLimit_IsRejectedWithoutAllocating()
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)CastFrameType.VideoDelta);
        await stream.WriteAsync(new byte[8]);
        await stream.WriteAsync(new byte[] { 0x00, 0x80, 0x00, 0x01 }); // MaxPayloadBytes + 1
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => CastFrame.ReadAsync(stream));
    }

    [Fact]
    public async Task UnknownFrameType_IsRejected()
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0x7F);
        await stream.WriteAsync(new byte[8]);
        await stream.WriteAsync(new byte[4]);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => CastFrame.ReadAsync(stream));
    }
}
