using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class CastPlacementTests
{
    private static readonly CastMonitor[] Monitors =
    {
        new("\\\\.\\DISPLAY1", "Monitor 1 (primary)", 3840, 2160, true),
        new("\\\\.\\DISPLAY2", "Monitor 2", 1920, 1080, false)
    };

    [Fact]
    public void AMonitorThatIsStillThere_IsKept()
    {
        var wanted = new CastPlacement(CastPlacementMode.Fullscreen, "\\\\.\\DISPLAY2", 0, 0, 0, 0);

        Assert.Equal(wanted, CastSettings.CorrectPlacement(wanted, Monitors));
    }

    [Fact]
    public void AMonitorThatIsGone_FallsBackToThePrimaryOneAndKeepsTheMode()
    {
        var wanted = new CastPlacement(CastPlacementMode.Fullscreen, "\\\\.\\DISPLAY7", 10, 20, 800, 600);

        var corrected = CastSettings.CorrectPlacement(wanted, Monitors);

        Assert.Equal("\\\\.\\DISPLAY1", corrected.MonitorId);
        Assert.Equal(CastPlacementMode.Fullscreen, corrected.Mode);
        Assert.Equal(800, corrected.Width);
    }

    [Fact]
    public void WithoutAPrimaryMonitor_TheFirstOneIsUsed()
    {
        var monitors = new[] { new CastMonitor("a", "A", 1920, 1080, false) };
        var wanted = new CastPlacement(CastPlacementMode.Window, "b", 0, 0, 640, 480);

        Assert.Equal("a", CastSettings.CorrectPlacement(wanted, monitors).MonitorId);
    }

    [Fact]
    public void WithNoMonitorsAtAll_ThePlacementIsLeftAlone()
    {
        var wanted = new CastPlacement(CastPlacementMode.AllMonitors, "whatever", 0, 0, 0, 0);

        Assert.Equal(wanted, CastSettings.CorrectPlacement(wanted, System.Array.Empty<CastMonitor>()));
    }
}
