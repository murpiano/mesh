using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class CastSettingsTests
{
    private static CastSettings Sample() => new(
        Quality: 73,
        Audio: true,
        Placement: new CastPlacement(CastPlacementMode.Window, "monitor-2", 10, 20, 1280, 720),
        Monitors: new[]
        {
            new CastMonitor("monitor-1", "Dell U2720Q", 3840, 2160, true),
            new CastMonitor("monitor-2", "Laptop", 1920, 1080, false)
        });

    [Fact]
    public async Task EveryField_RoundTrips()
    {
        using var stream = new MemoryStream();
        var sent = Sample();
        await MeshMessage.WriteAsync(stream, new CastSettingsMessage("cast-1", sent));
        stream.Position = 0;

        var received = Assert.IsType<CastSettingsMessage>(await MeshMessage.ReadAsync(stream));

        Assert.Equal("cast-1", received.CastId);
        Assert.Equal(sent.Quality, received.Settings.Quality);
        Assert.True(received.Settings.Audio);
        Assert.Equal(sent.Placement, received.Settings.Placement);
        Assert.Equal(sent.Monitors, received.Settings.Monitors);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(250, 100)]
    [InlineData(50, 50)]
    public async Task Quality_IsClampedOnRead(int written, int expected)
    {
        using var stream = new MemoryStream();
        await MeshMessage.WriteAsync(stream, new CastSettingsMessage("cast-1", Sample() with { Quality = written }));
        stream.Position = 0;

        var received = Assert.IsType<CastSettingsMessage>(await MeshMessage.ReadAsync(stream));

        Assert.Equal(expected, received.Settings.Quality);
    }

    [Fact]
    public async Task MonitorList_LongerThanTheCap_IsRejected()
    {
        var monitors = Enumerable.Range(0, CastSettings.MaxMonitors + 1)
            .Select(i => new CastMonitor($"monitor-{i}", $"Screen {i}", 1920, 1080, i == 0))
            .ToArray();
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            MeshMessage.WriteAsync(stream, new CastSettingsMessage("cast-1", Sample() with { Monitors = monitors })));
    }
}
