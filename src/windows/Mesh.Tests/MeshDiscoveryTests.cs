using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class MeshDiscoveryTests
{
    [Fact]
    public async Task Advertise_IsFoundByBrowse_OnLoopback()
    {
        var found = new TaskCompletionSource<DiscoveredDevice>();
        using var browser = new MeshDiscovery();
        browser.StartBrowsing(device =>
        {
            if (device.Uuid == "test-uuid-1") found.TrySetResult(device);
        });

        using var advertiser = new MeshDiscovery();
        advertiser.Advertise("test-uuid-1", "TestPC", "windows", 5555);

        var result = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(found.Task, result);
        var device = await found.Task;
        Assert.Equal("TestPC", device.Name);
        Assert.Equal("windows", device.Platform);
        Assert.Equal(5555, device.Port);
    }

    [Fact]
    public void Advertise_DoesNotThrow_WhenCalledTwiceWithSamePort()
    {
        using var advertiser = new MeshDiscovery();
        advertiser.Advertise("test-uuid-2", "TestPC2", "windows", 5556);
        advertiser.Advertise("test-uuid-2", "TestPC2", "windows", 5556);
    }
}
