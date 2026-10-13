using Mesh.Core;
using System.Net;
using Xunit;

namespace Mesh.Tests;

public class PairingSessionTests
{
    [Fact]
    public async Task Handshake_BothSidesComputeSamePin()
    {
        var certAPath = Path.Combine(Path.GetTempPath(), $"mesh-test-a-{Guid.NewGuid()}.pfx");
        var certBPath = Path.Combine(Path.GetTempPath(), $"mesh-test-b-{Guid.NewGuid()}.pfx");
        try
        {
            var certA = IdentityStore.LoadOrCreate(certAPath);
            var certB = IdentityStore.LoadOrCreate(certBPath);

            const int port = 51234;
            var server = new PairingSession();
            var listenTask = server.StartListener(certA, port);

            await Task.Delay(200); // let the listener bind
            var client = new PairingSession();
            await client.ConnectTo(certB, IPAddress.Loopback.ToString(), port);
            await listenTask;

            var serverPin = server.ComputePin();
            var clientPin = client.ComputePin();
            Assert.Equal(serverPin, clientPin);
        }
        finally
        {
            if (File.Exists(certAPath)) File.Delete(certAPath);
            if (File.Exists(certBPath)) File.Delete(certBPath);
        }
    }

    [Fact]
    public void IsPeerTrusted_ReturnsFalse_WhenNoStoredRecord()
    {
        var storePath = Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}.json");
        var session = new PairingSession();
        Assert.False(session.IsPeerTrusted(storePath, "unknown-uuid"));
    }

    [Fact]
    public void IsPeerTrusted_ReturnsFalse_WhenFingerprintChanged()
    {
        var storePath = Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}.json");
        try
        {
            PairedDeviceStore.Upsert(storePath, new PairedDevice
            {
                Uuid = "known-uuid",
                Name = "Old Device",
                Fingerprint = "old-fingerprint",
                LastIp = "1.2.3.4",
                PairedAt = DateTimeOffset.UtcNow
            });

            var session = new PairingSession();
            // Simulate a session whose peer fingerprint differs from storage.
            typeof(PairingSession).GetProperty("PeerFingerprint")!
                .SetValue(session, "new-fingerprint");

            Assert.False(session.IsPeerTrusted(storePath, "known-uuid"));
        }
        finally
        {
            if (File.Exists(storePath)) File.Delete(storePath);
        }
    }
}
