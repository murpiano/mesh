using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class DeviceIdentityTests
{
    [Fact]
    public void GetOrCreateUuid_ReturnsSameUuid_OnSecondCall()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}", "device_id.txt");
        try
        {
            var first = DeviceIdentity.GetOrCreateUuid(path);
            var second = DeviceIdentity.GetOrCreateUuid(path);
            Assert.True(Guid.TryParse(first, out _));
            Assert.Equal(first, second);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void GetOrCreateUuid_ReadsExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}.txt");
        const string existing = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
        File.WriteAllText(path, existing + "\n");
        try
        {
            Assert.Equal(existing, DeviceIdentity.GetOrCreateUuid(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetOrCreateUuid_RegeneratesWhenFileCorrupt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}.txt");
        File.WriteAllText(path, "not-a-uuid");
        try
        {
            var uuid = DeviceIdentity.GetOrCreateUuid(path);
            Assert.True(Guid.TryParse(uuid, out _));
            Assert.Equal(uuid, DeviceIdentity.GetOrCreateUuid(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
