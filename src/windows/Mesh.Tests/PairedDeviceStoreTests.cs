using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class PairedDeviceStoreTests
{
    [Fact]
    public void Load_ReturnsEmptyList_WhenFileMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}.json");
        var result = PairedDeviceStore.Load(path);
        Assert.Empty(result);
    }

    [Fact]
    public void Load_ReturnsEmptyList_WhenFileCorrupt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}.json");
        File.WriteAllText(path, "{not valid json");
        try
        {
            var result = PairedDeviceStore.Load(path);
            Assert.Empty(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Upsert_ThenLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}.json");
        try
        {
            var device = new PairedDevice
            {
                Uuid = "device-1",
                Name = "Pixel",
                Fingerprint = "abc123",
                LastIp = "192.168.1.5",
                PairedAt = DateTimeOffset.Parse("2026-10-13T00:00:00Z")
            };
            PairedDeviceStore.Upsert(path, device);
            var loaded = PairedDeviceStore.Load(path);
            Assert.Single(loaded);
            Assert.Equal("device-1", loaded[0].Uuid);
            Assert.Equal("abc123", loaded[0].Fingerprint);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Upsert_ReplacesExistingUuid_RatherThanDuplicating()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}.json");
        try
        {
            var first = new PairedDevice { Uuid = "d1", Name = "Old", Fingerprint = "fp1", LastIp = "1.1.1.1", PairedAt = DateTimeOffset.UtcNow };
            var second = new PairedDevice { Uuid = "d1", Name = "New", Fingerprint = "fp2", LastIp = "2.2.2.2", PairedAt = DateTimeOffset.UtcNow };
            PairedDeviceStore.Upsert(path, first);
            PairedDeviceStore.Upsert(path, second);
            var loaded = PairedDeviceStore.Load(path);
            Assert.Single(loaded);
            Assert.Equal("fp2", loaded[0].Fingerprint);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
