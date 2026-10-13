using Mesh.Core;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Mesh.Tests;

/// <summary>Two distinct identities generated once for the whole test class.</summary>
public sealed class TwoIdentitiesFixture : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}");
    public X509Certificate2 CertA { get; }
    public X509Certificate2 CertB { get; }

    public TwoIdentitiesFixture()
    {
        CertA = IdentityStore.LoadOrCreate(Path.Combine(_dir, "a.pfx"));
        CertB = IdentityStore.LoadOrCreate(Path.Combine(_dir, "b.pfx"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

internal sealed class FakePrompt(bool pinAnswer, bool certChangedAnswer = true, bool hang = false) : IPairingPrompt
{
    public string? ShownPin { get; private set; }
    public bool CertChangedShown { get; private set; }
    public bool PinShown => ShownPin != null;

    public async Task<bool> ConfirmCertificateChangedAsync(string peerName, CancellationToken ct)
    {
        CertChangedShown = true;
        if (hang) await Task.Delay(Timeout.Infinite, ct);
        return certChangedAnswer;
    }

    public async Task<bool> ConfirmPinAsync(string peerName, string pin, CancellationToken ct)
    {
        ShownPin = pin;
        if (hang) await Task.Delay(Timeout.Infinite, ct);
        return pinAnswer;
    }
}

public class PairingSessionTests(TwoIdentitiesFixture ids) : IClassFixture<TwoIdentitiesFixture>
{
    private static async Task<(PairingSession server, PairingSession client)> ConnectPair(
        X509Certificate2 serverCert, X509Certificate2 clientCert)
    {
        using var listener = new PairingListener(); // port 0: OS-assigned
        var accept = listener.AcceptAsync(serverCert);
        var client = await PairingSession.ConnectAsync(clientCert, IPAddress.Loopback.ToString(), listener.Port);
        var server = await accept;
        return (server, client);
    }

    private static string TempStore() => Path.Combine(Path.GetTempPath(), $"mesh-test-{Guid.NewGuid()}.json");

    [Fact]
    public void Listener_BindsEphemeralPort_WhenGivenZero()
    {
        using var listener = new PairingListener(0);
        Assert.InRange(listener.Port, 1, 65535);
    }

    [Fact]
    public async Task Handshake_BothSidesComputeSamePin_WithDistinctIdentities()
    {
        var (server, client) = await ConnectPair(ids.CertA, ids.CertB);
        using (server)
        using (client)
        {
            Assert.NotEqual(server.PeerFingerprint, client.PeerFingerprint);
            await Task.WhenAll(server.ExchangeIdentityAsync("uuid-a", "Alpha"),
                               client.ExchangeIdentityAsync("uuid-b", "Bravo"));
            Assert.Equal("uuid-b", server.PeerUuid);
            Assert.Equal("Bravo", server.PeerName);
            Assert.Equal("uuid-a", client.PeerUuid);
            Assert.Equal("Alpha", client.PeerName);

            await Task.WhenAll(server.ExchangeNoncesAsync(), client.ExchangeNoncesAsync());
            Assert.Equal(server.ComputePin(), client.ComputePin());
        }
    }

    [Fact]
    public async Task Pin_DiffersBetweenSessions_BecauseOfFreshNonces()
    {
        async Task<string> OnePin()
        {
            var (server, client) = await ConnectPair(ids.CertA, ids.CertB);
            using (server)
            using (client)
            {
                await Task.WhenAll(server.ExchangeNoncesAsync(), client.ExchangeNoncesAsync());
                return server.ComputePin();
            }
        }
        // Same two certificates; with nonces the PIN is fresh each time (1e-6 collision odds
        // per pair, so require at least one difference across three sessions).
        var pins = new[] { await OnePin(), await OnePin(), await OnePin() };
        Assert.True(pins.Distinct().Count() > 1);
    }

    [Fact]
    public async Task AcceptAsync_ThrowsTimeout_WhenPeerNeverCompletesTlsHandshake()
    {
        using var listener = new PairingListener();
        var accept = listener.AcceptAsync(ids.CertA);

        // Raw TCP connect, never send a ClientHello.
        using var rawClient = new TcpClient();
        await rawClient.ConnectAsync(IPAddress.Loopback, listener.Port);

        await Assert.ThrowsAsync<TimeoutException>(() => accept);
    }

    [Fact]
    public async Task AcceptLoop_KeepsListening_AfterFailedAttempt()
    {
        using var listener = new PairingListener();
        using var cts = new CancellationTokenSource();
        var errors = new List<Exception>();
        var handled = new TaskCompletionSource<string>();
        var loop = listener.RunAcceptLoopAsync(ids.CertA, session =>
        {
            handled.TrySetResult(session.PeerFingerprint);
            return Task.CompletedTask;
        }, ex => { lock (errors) errors.Add(ex); }, cts.Token);

        // 1st attempt: connect and immediately hang up mid-handshake.
        using (var bad = new TcpClient())
        {
            await bad.ConnectAsync(IPAddress.Loopback, listener.Port);
        }

        // 2nd attempt on the same port must still be served.
        using var good = await PairingSession.ConnectAsync(ids.CertB, "127.0.0.1", listener.Port);
        var fp = await handled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(IdentityStore.Fingerprint(ids.CertB), fp);
        Assert.NotEmpty(errors);

        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_BothConfirm_BothSidesStorePeerRecord()
    {
        var storeA = TempStore();
        var storeB = TempStore();
        try
        {
            var (server, client) = await ConnectPair(ids.CertA, ids.CertB);
            var promptA = new FakePrompt(pinAnswer: true);
            var promptB = new FakePrompt(pinAnswer: true);
            using (server)
            using (client)
            {
                var results = await Task.WhenAll(
                    server.RunAsync("uuid-a", "Alpha", storeA, promptA),
                    client.RunAsync("uuid-b", "Bravo", storeB, promptB));
                Assert.All(results, r => Assert.Equal(PairingResult.Paired, r.Result));
            }

            Assert.Equal(promptA.ShownPin, promptB.ShownPin);
            Assert.False(promptA.CertChangedShown);

            var recA = Assert.Single(PairedDeviceStore.Load(storeA));
            Assert.Equal("uuid-b", recA.Uuid);
            Assert.Equal("Bravo", recA.Name);
            Assert.Equal(IdentityStore.Fingerprint(ids.CertB), recA.Fingerprint);
            Assert.Equal("127.0.0.1", recA.LastIp);

            var recB = Assert.Single(PairedDeviceStore.Load(storeB));
            Assert.Equal("uuid-a", recB.Uuid);
            Assert.Equal("Alpha", recB.Name);
            Assert.Equal(IdentityStore.Fingerprint(ids.CertA), recB.Fingerprint);
        }
        finally
        {
            File.Delete(storeA);
            File.Delete(storeB);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task RunAsync_AnyReject_BothFail_AndNeitherWritesRecord(bool answerA, bool answerB)
    {
        var storeA = TempStore();
        var storeB = TempStore();
        try
        {
            var (server, client) = await ConnectPair(ids.CertA, ids.CertB);
            using (server)
            using (client)
            {
                var results = await Task.WhenAll(
                    server.RunAsync("uuid-a", "Alpha", storeA, new FakePrompt(answerA)),
                    client.RunAsync("uuid-b", "Bravo", storeB, new FakePrompt(answerB)));
                Assert.All(results, r => Assert.Equal(PairingResult.PairingFailed, r.Result));
            }
            Assert.False(File.Exists(storeA));
            Assert.False(File.Exists(storeB));
        }
        finally
        {
            File.Delete(storeA);
            File.Delete(storeB);
        }
    }

    [Fact]
    public async Task RunAsync_UserNeverAnswers_BothFailAfterTimeout_NoRecord()
    {
        var storeA = TempStore();
        var storeB = TempStore();
        try
        {
            var (server, client) = await ConnectPair(ids.CertA, ids.CertB);
            server.UserTimeout = TimeSpan.FromSeconds(1);
            client.UserTimeout = TimeSpan.FromSeconds(1);
            using (server)
            using (client)
            {
                var results = await Task.WhenAll(
                    server.RunAsync("uuid-a", "Alpha", storeA, new FakePrompt(true, hang: true)),
                    client.RunAsync("uuid-b", "Bravo", storeB, new FakePrompt(true)))
                    .WaitAsync(TimeSpan.FromSeconds(15));
                Assert.All(results, r => Assert.Equal(PairingResult.PairingFailed, r.Result));
            }
            Assert.False(File.Exists(storeA));
            Assert.False(File.Exists(storeB));
        }
        finally
        {
            File.Delete(storeA);
            File.Delete(storeB);
        }
    }

    [Fact]
    public async Task RunAsync_PeerDisconnects_BeforeConfirming_IsPairingFailed_NoRecord()
    {
        var storeA = TempStore();
        try
        {
            var (server, client) = await ConnectPair(ids.CertA, ids.CertB);
            using (server)
            {
                var serverRun = server.RunAsync("uuid-a", "Alpha", storeA, new FakePrompt(true, hang: true));
                await client.ExchangeIdentityAsync("uuid-b", "Bravo");
                await client.ExchangeTrustAsync(false);
                await client.ExchangeNoncesAsync();
                client.Dispose(); // vanish while A's PIN dialog is up

                var result = await serverRun.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(PairingResult.PairingFailed, result.Result);
            }
            Assert.False(File.Exists(storeA));
        }
        finally
        {
            File.Delete(storeA);
        }
    }

    [Fact]
    public async Task RunAsync_SecondConnection_IsTrustedReconnect_InBothDirections()
    {
        var storeA = TempStore();
        var storeB = TempStore();
        try
        {
            // First pairing: B initiates to A.
            var (server, client) = await ConnectPair(ids.CertA, ids.CertB);
            using (server)
            using (client)
            {
                await Task.WhenAll(
                    server.RunAsync("uuid-a", "Alpha", storeA, new FakePrompt(true)),
                    client.RunAsync("uuid-b", "Bravo", storeB, new FakePrompt(true)));
            }

            // Reconnect with roles swapped: A initiates to B (B is now the listener).
            var (server2, client2) = await ConnectPair(ids.CertB, ids.CertA);
            var promptB = new FakePrompt(false);
            var promptA = new FakePrompt(false);
            using (server2)
            using (client2)
            {
                var results = await Task.WhenAll(
                    server2.RunAsync("uuid-b", "Bravo", storeB, promptB),
                    client2.RunAsync("uuid-a", "Alpha", storeA, promptA));
                Assert.All(results, r => Assert.Equal(PairingResult.Reconnected, r.Result));
            }
            Assert.False(promptA.PinShown);
            Assert.False(promptB.PinShown);
        }
        finally
        {
            File.Delete(storeA);
            File.Delete(storeB);
        }
    }

    [Fact]
    public async Task RunAsync_OnlyOneSideTrusts_BothFallBackToPinFlow()
    {
        var storeA = TempStore();
        var storeB = TempStore();
        try
        {
            // A already trusts B, but B has no record of A (e.g. B cleared its data).
            PairedDeviceStore.Upsert(storeA, new PairedDevice
            {
                Uuid = "uuid-b", Name = "Bravo", Fingerprint = IdentityStore.Fingerprint(ids.CertB),
                LastIp = "10.0.0.9", PairedAt = DateTimeOffset.UtcNow
            });
            var (server, client) = await ConnectPair(ids.CertA, ids.CertB);
            var promptA = new FakePrompt(true);
            var promptB = new FakePrompt(true);
            using (server)
            using (client)
            {
                var results = await Task.WhenAll(
                    server.RunAsync("uuid-a", "Alpha", storeA, promptA),
                    client.RunAsync("uuid-b", "Bravo", storeB, promptB));
                Assert.All(results, r => Assert.Equal(PairingResult.Paired, r.Result));
            }
            Assert.True(promptA.PinShown);
            Assert.True(promptB.PinShown);
            Assert.Single(PairedDeviceStore.Load(storeB));
        }
        finally
        {
            File.Delete(storeA);
            File.Delete(storeB);
        }
    }

    [Fact]
    public async Task RunAsync_CertificateChanged_WarnsFirst_AndDeclineFailsWithoutOverwriting()
    {
        var storeA = TempStore();
        var storeB = TempStore();
        try
        {
            PairedDeviceStore.Upsert(storeA, new PairedDevice
            {
                Uuid = "uuid-b", Name = "Bravo", Fingerprint = "old-fingerprint",
                LastIp = "10.0.0.9", PairedAt = DateTimeOffset.UtcNow
            });
            var (server, client) = await ConnectPair(ids.CertA, ids.CertB);
            var promptA = new FakePrompt(pinAnswer: true, certChangedAnswer: false);
            using (server)
            using (client)
            {
                var results = await Task.WhenAll(
                    server.RunAsync("uuid-a", "Alpha", storeA, promptA),
                    client.RunAsync("uuid-b", "Bravo", storeB, new FakePrompt(true)));
                Assert.All(results, r => Assert.Equal(PairingResult.PairingFailed, r.Result));
            }
            Assert.True(promptA.CertChangedShown);
            Assert.False(promptA.PinShown);
            Assert.Equal("old-fingerprint", Assert.Single(PairedDeviceStore.Load(storeA)).Fingerprint);
            Assert.False(File.Exists(storeB));
        }
        finally
        {
            File.Delete(storeA);
            File.Delete(storeB);
        }
    }

    [Fact]
    public async Task RunAsync_CertificateChanged_AcceptThenMatch_RePairsWithNewFingerprint()
    {
        var storeA = TempStore();
        var storeB = TempStore();
        try
        {
            PairedDeviceStore.Upsert(storeA, new PairedDevice
            {
                Uuid = "uuid-b", Name = "Bravo", Fingerprint = "old-fingerprint",
                LastIp = "10.0.0.9", PairedAt = DateTimeOffset.UtcNow
            });
            var (server, client) = await ConnectPair(ids.CertA, ids.CertB);
            var promptA = new FakePrompt(pinAnswer: true, certChangedAnswer: true);
            using (server)
            using (client)
            {
                var results = await Task.WhenAll(
                    server.RunAsync("uuid-a", "Alpha", storeA, promptA),
                    client.RunAsync("uuid-b", "Bravo", storeB, new FakePrompt(true)));
                Assert.All(results, r => Assert.Equal(PairingResult.Paired, r.Result));
            }
            Assert.True(promptA.CertChangedShown);
            Assert.True(promptA.PinShown);
            Assert.Equal(IdentityStore.Fingerprint(ids.CertB),
                Assert.Single(PairedDeviceStore.Load(storeA)).Fingerprint);
        }
        finally
        {
            File.Delete(storeA);
            File.Delete(storeB);
        }
    }

    [Fact]
    public void CheckTrust_DistinguishesUnknownTrustedAndChanged()
    {
        var store = TempStore();
        try
        {
            Assert.Equal(PeerTrust.Unknown, PairingSession.CheckTrust(store, "known-uuid", "fp"));
            PairedDeviceStore.Upsert(store, new PairedDevice
            {
                Uuid = "known-uuid", Name = "Old Device", Fingerprint = "old-fingerprint",
                LastIp = "1.2.3.4", PairedAt = DateTimeOffset.UtcNow
            });
            Assert.Equal(PeerTrust.Trusted, PairingSession.CheckTrust(store, "known-uuid", "old-fingerprint"));
            Assert.Equal(PeerTrust.CertificateChanged, PairingSession.CheckTrust(store, "known-uuid", "new-fingerprint"));
            Assert.Equal(PeerTrust.Unknown, PairingSession.CheckTrust(store, "other-uuid", "old-fingerprint"));
        }
        finally
        {
            File.Delete(store);
        }
    }

    [Fact]
    public async Task ConnectAsync_ToClosedPort_Throws()
    {
        int port;
        using (var probe = new PairingListener()) port = probe.Port; // now closed
        await Assert.ThrowsAnyAsync<Exception>(() =>
            PairingSession.ConnectAsync(ids.CertB, "127.0.0.1", port));
    }
}
