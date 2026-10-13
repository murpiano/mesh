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
    public async Task PeerAdvertisedBeforeBrowsingStarted_IsFoundByActiveQuery()
    {
        using var advertiser = new MeshDiscovery();
        advertiser.Advertise("test-uuid-early", "EarlyPC", "windows", 5557);
        // Let the unsolicited announcements go by before anyone is listening.
        await Task.Delay(TimeSpan.FromSeconds(3));

        var found = new TaskCompletionSource<DiscoveredDevice>();
        using var browser = new MeshDiscovery();
        browser.StartBrowsing(device =>
        {
            if (device.Uuid == "test-uuid-early") found.TrySetResult(device);
        });

        var device = await found.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("EarlyPC", device.Name);
        Assert.Equal(5557, device.Port);
    }

    [Fact]
    public async Task PeerShutdown_IsReportedAsLost()
    {
        var found = new TaskCompletionSource<bool>();
        var lost = new TaskCompletionSource<string>();
        using var browser = new MeshDiscovery();
        browser.StartBrowsing(
            device => { if (device.Uuid == "test-uuid-bye") found.TrySetResult(true); },
            uuid => { if (uuid == "test-uuid-bye") lost.TrySetResult(uuid); });

        var advertiser = new MeshDiscovery();
        advertiser.Advertise("test-uuid-bye", "ByePC", "windows", 5558);
        await found.Task.WaitAsync(TimeSpan.FromSeconds(10));

        advertiser.Dispose(); // sends goodbye
        Assert.Equal("test-uuid-bye", await lost.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void StartBrowsing_CanBeCalledAgainAsRefresh()
    {
        using var browser = new MeshDiscovery();
        var errors = new List<Exception>();
        browser.StartBrowsing(_ => { }, onError: errors.Add);
        browser.StartBrowsing(_ => { }, onError: errors.Add);
        Assert.Empty(errors);
    }

    [Fact]
    public void Advertise_DoesNotThrow_WhenCalledTwiceWithSamePort()
    {
        using var advertiser = new MeshDiscovery();
        advertiser.Advertise("test-uuid-2", "TestPC2", "windows", 5556);
        advertiser.Advertise("test-uuid-2", "TestPC2", "windows", 5556);
    }
}
