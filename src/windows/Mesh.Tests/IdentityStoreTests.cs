using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class IdentityStoreTests
{
    [Fact]
    public void LoadOrCreate_CreatesCertWithPrivateKey_WhenFileMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}.pfx");
        try
        {
            var cert = IdentityStore.LoadOrCreate(path);
            Assert.True(cert.HasPrivateKey);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrCreate_ReturnsSameIdentity_OnSecondCall()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}.pfx");
        try
        {
            var first = IdentityStore.LoadOrCreate(path);
            var second = IdentityStore.LoadOrCreate(path);
            Assert.Equal(IdentityStore.Fingerprint(first), IdentityStore.Fingerprint(second));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Fingerprint_Is64LowercaseHexChars()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}.pfx");
        try
        {
            var cert = IdentityStore.LoadOrCreate(path);
            var fp = IdentityStore.Fingerprint(cert);
            Assert.Equal(64, fp.Length);
            Assert.Equal(fp, fp.ToLowerInvariant());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
