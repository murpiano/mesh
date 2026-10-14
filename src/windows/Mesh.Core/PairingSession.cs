using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Mesh.Core;

public enum PeerTrust
{
    /// <summary>No stored record for this uuid: first-time pairing.</summary>
    Unknown,
    /// <summary>Stored record exists and its fingerprint matches the live peer certificate.</summary>
    Trusted,
    /// <summary>Stored record exists but the live fingerprint differs: warn, then force re-pair.</summary>
    CertificateChanged
}

public enum PairingResult
{
    /// <summary>First-time (or re-) pairing completed on both sides; record written.</summary>
    Paired,
    /// <summary>Both sides already trusted each other; no PIN needed.</summary>
    Reconnected,
    /// <summary>TCP/TLS/protocol-transport failure, including timeouts. UI: "Connection failed".</summary>
    ConnectionFailed,
    /// <summary>Connected, but PIN rejected/declined/timed out or nonce check failed. UI: "Pairing failed".</summary>
    PairingFailed
}

public sealed record PairingOutcome(PairingResult Result, string? PeerUuid, string? PeerName, string Detail);

/// <summary>UI callbacks used by <see cref="PairingSession.RunAsync"/>.</summary>
public interface IPairingPrompt
{
    /// <summary>
    /// "Certificate changed for &lt;name&gt; — re-pair?". Return true to continue into the PIN flow.
    /// <paramref name="ct"/> is cancelled when the user-interaction budget expires or the peer
    /// aborts; implementations should close their dialog then.
    /// </summary>
    Task<bool> ConfirmCertificateChangedAsync(string peerName, CancellationToken ct);

    /// <summary>Show the PIN; return true for "Match", false for "Not matching".</summary>
    Task<bool> ConfirmPinAsync(string peerName, string pin, CancellationToken ct);
}

/// <summary>Thrown when the peer's revealed nonce does not match its earlier commitment.</summary>
public sealed class PairingFailedException(string message) : Exception(message);

/// <summary>
/// One open TLS connection to a peer, plus the post-handshake pairing protocol
/// (<see cref="PairingProtocol"/>). The TLS stream stays open after the handshake so the
/// protocol can run over it; dispose the session to close the socket (every exit path).
/// </summary>
public sealed class PairingSession : IDisposable
{
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultUserTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Budget for the local user prompt, and separately for awaiting the peer's
    /// confirm byte. 60 s by default; overridable for tests.</summary>
    public TimeSpan UserTimeout { get; set; } = DefaultUserTimeout;

    private readonly TcpClient _client;
    private readonly SslStream _stream;
    private readonly X509Certificate2 _localIdentity;
    private byte[]? _localNonce;
    private byte[]? _peerNonce;
    private bool _detached;

    public string PeerFingerprint { get; }
    public string PeerIp { get; }
    public string? PeerUuid { get; private set; }
    public string? PeerName { get; private set; }

    private PairingSession(TcpClient client, SslStream stream, X509Certificate2 localIdentity)
    {
        _client = client;
        _stream = stream;
        _localIdentity = localIdentity;
        var peerCert = stream.RemoteCertificate as X509Certificate2
            ?? new X509Certificate2(stream.RemoteCertificate!);
        PeerFingerprint = IdentityStore.Fingerprint(peerCert);
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        PeerIp = (remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address).ToString();
    }

    /// <summary>Initiator side: TCP connect + TLS handshake, each bounded by 10 s.</summary>
    public static async Task<PairingSession> ConnectAsync(X509Certificate2 identity, string ip, int port,
        CancellationToken ct = default)
    {
        var client = new TcpClient();
        SslStream? sslStream = null;
        try
        {
            await client.ConnectAsync(ip, port, ct).AsTask().WaitAsync(NetworkTimeout, ct);

            sslStream = new SslStream(client.GetStream(), false,
                (sender, cert, chain, errors) => true); // untrusted at handshake stage, per spec

            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = ip,
                ClientCertificates = new X509Certificate2Collection(identity)
                // NOTE: do not also set RemoteCertificateValidationCallback here -- the
                // SslStream constructor above already supplies one, and SslStream throws
                // InvalidOperationException if the options try to set it again.
            }, ct).WaitAsync(NetworkTimeout, ct);

            return new PairingSession(client, sslStream, identity);
        }
        catch
        {
            sslStream?.Dispose();
            client.Dispose();
            throw;
        }
    }

    /// <summary>Listener side: TLS handshake on an accepted client, bounded by 10 s.
    /// Takes ownership of <paramref name="client"/> (disposed on failure).</summary>
    internal static async Task<PairingSession> AcceptAsync(TcpClient client, X509Certificate2 identity)
    {
        SslStream? sslStream = null;
        try
        {
            sslStream = new SslStream(client.GetStream(), false,
                (sender, cert, chain, errors) => true); // untrusted at handshake stage, per spec

            await sslStream.AuthenticateAsServerAsync(identity, clientCertificateRequired: true,
                checkCertificateRevocation: false).WaitAsync(NetworkTimeout);

            return new PairingSession(client, sslStream, identity);
        }
        catch
        {
            sslStream?.Dispose();
            client.Dispose();
            throw;
        }
    }

    /// <summary>Protocol steps 1-2: send own uuid/name, receive the peer's.</summary>
    public async Task ExchangeIdentityAsync(string selfUuid, string selfName)
    {
        await WithNetworkTimeout(async () =>
        {
            await PairingProtocol.WriteStringAsync(_stream, selfUuid);
            await PairingProtocol.WriteStringAsync(_stream, selfName);
            PeerUuid = await PairingProtocol.ReadStringAsync(_stream);
            PeerName = await PairingProtocol.ReadStringAsync(_stream);
        });
    }

    /// <summary>Protocol step 3. Returns true only if BOTH sides trust each other.</summary>
    public async Task<bool> ExchangeTrustAsync(bool localTrustsPeer)
    {
        byte peer = 0;
        await WithNetworkTimeout(async () =>
        {
            await PairingProtocol.WriteByteAsync(_stream, localTrustsPeer ? PairingProtocol.Yes : PairingProtocol.No);
            peer = await PairingProtocol.ReadByteAsync(_stream);
        });
        return localTrustsPeer && peer == PairingProtocol.Yes;
    }

    /// <summary>Protocol steps 4-5: commit-then-reveal nonce exchange.</summary>
    /// <exception cref="PairingFailedException">peer's nonce does not match its commitment.</exception>
    public async Task ExchangeNoncesAsync()
    {
        var nonce = PairingProtocol.CreateNonce();
        byte[] peerCommitment = [];
        byte[] peerNonce = [];
        await WithNetworkTimeout(async () =>
        {
            await PairingProtocol.WriteBytesAsync(_stream, PairingProtocol.Commit(nonce));
            peerCommitment = await PairingProtocol.ReadBytesAsync(_stream, PairingProtocol.CommitmentLength);
            // Reveal only after the peer has committed.
            await PairingProtocol.WriteBytesAsync(_stream, nonce);
            peerNonce = await PairingProtocol.ReadBytesAsync(_stream, PinDerivation.NonceLength);
        });
        if (!PairingProtocol.VerifyCommitment(peerCommitment, peerNonce))
            throw new PairingFailedException("Peer's nonce does not match its commitment.");
        _localNonce = nonce;
        _peerNonce = peerNonce;
    }

    public string ComputePin()
    {
        if (_localNonce == null || _peerNonce == null)
            throw new InvalidOperationException("Nonce exchange has not completed yet.");
        return PinDerivation.Derive(IdentityStore.Fingerprint(_localIdentity), PeerFingerprint,
            _localNonce, _peerNonce);
    }

    public static PeerTrust CheckTrust(string pairedDevicesPath, string uuid, string fingerprint)
    {
        var record = PairedDeviceStore.Find(pairedDevicesPath, uuid);
        if (record == null) return PeerTrust.Unknown;
        return record.Fingerprint == fingerprint ? PeerTrust.Trusted : PeerTrust.CertificateChanged;
    }

    public bool IsPeerTrusted(string pairedDevicesPath, string uuid) =>
        CheckTrust(pairedDevicesPath, uuid, PeerFingerprint) == PeerTrust.Trusted;

    /// <summary>
    /// Runs the whole post-handshake protocol and writes the trust record only on mutual
    /// success. Identical on the initiator and listener side. Never throws for network or
    /// peer errors: they are reported through the returned outcome. Does not dispose the
    /// session; callers own it (<c>using</c>).
    /// </summary>
    public async Task<PairingOutcome> RunAsync(string selfUuid, string selfName, string pairedDevicesPath,
        IPairingPrompt prompt)
    {
        PeerTrust trust;
        string pin;
        try
        {
            await ExchangeIdentityAsync(selfUuid, selfName);
            trust = CheckTrust(pairedDevicesPath, PeerUuid!, PeerFingerprint);
            if (await ExchangeTrustAsync(trust == PeerTrust.Trusted))
            {
                var existing = PairedDeviceStore.Find(pairedDevicesPath, PeerUuid!)!;
                existing.LastIp = PeerIp;
                existing.Name = PeerName!;
                PairedDeviceStore.Upsert(pairedDevicesPath, existing);
                return Outcome(PairingResult.Reconnected, "Trusted reconnect.");
            }
            await ExchangeNoncesAsync();
            pin = ComputePin();
        }
        catch (PairingFailedException ex)
        {
            return Outcome(PairingResult.PairingFailed, ex.Message);
        }
        catch (Exception ex)
        {
            return Outcome(PairingResult.ConnectionFailed, ex.Message);
        }

        // Step 6: user confirmation, raced against the peer's answer so an early
        // "Not matching" on the other device closes our dialog immediately.
        try
        {
            using var promptCts = new CancellationTokenSource(UserTimeout);
            var peerAnswer = PairingProtocol.ReadByteAsync(_stream);
            _ = peerAnswer.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var localAnswer = AskUserAsync(prompt, trust, pin, promptCts.Token);

            if (await Task.WhenAny(localAnswer, peerAnswer) == peerAnswer &&
                (!peerAnswer.IsCompletedSuccessfully || peerAnswer.Result != PairingProtocol.Yes))
            {
                promptCts.Cancel();
                await localAnswer;
                await TrySendAsync(PairingProtocol.No);
                return Outcome(PairingResult.PairingFailed, "The other device did not confirm the PIN.");
            }

            var localMatch = await localAnswer;
            await PairingProtocol.WriteByteAsync(_stream, localMatch ? PairingProtocol.Yes : PairingProtocol.No)
                .WaitAsync(NetworkTimeout);
            if (!localMatch)
                return Outcome(PairingResult.PairingFailed, "PIN not confirmed on this device.");

            var peer = await peerAnswer.WaitAsync(UserTimeout);
            if (peer != PairingProtocol.Yes)
                return Outcome(PairingResult.PairingFailed, "The other device did not confirm the PIN.");
        }
        catch (Exception ex)
        {
            return Outcome(PairingResult.PairingFailed, ex.Message);
        }

        PairedDeviceStore.Upsert(pairedDevicesPath, new PairedDevice
        {
            Uuid = PeerUuid!,
            Name = PeerName!,
            Fingerprint = PeerFingerprint,
            LastIp = PeerIp,
            PairedAt = DateTimeOffset.UtcNow
        });
        return Outcome(PairingResult.Paired, "Paired.");
    }

    private async Task<bool> AskUserAsync(IPairingPrompt prompt, PeerTrust trust, string pin, CancellationToken ct)
    {
        try
        {
            if (trust == PeerTrust.CertificateChanged &&
                !await prompt.ConfirmCertificateChangedAsync(PeerName!, ct).WaitAsync(ct))
                return false;
            return await prompt.ConfirmPinAsync(PeerName!, pin, ct).WaitAsync(ct);
        }
        catch (Exception)
        {
            // Cancelled (timeout / peer aborted) or UI error: treat as "Not matching".
            return false;
        }
    }

    private async Task TrySendAsync(byte value)
    {
        try { await PairingProtocol.WriteByteAsync(_stream, value).WaitAsync(NetworkTimeout); }
        catch (Exception) { /* peer already gone */ }
    }

    private PairingOutcome Outcome(PairingResult result, string detail) =>
        new(result, PeerUuid, PeerName, detail);

    private static Task WithNetworkTimeout(Func<Task> step) => step().WaitAsync(NetworkTimeout);

    /// <summary>
    /// Hands ownership of the open, authenticated transport to the caller (e.g. to build a
    /// <see cref="MeshSession"/> on a Paired/Reconnected outcome). After this call, this
    /// session's own <see cref="Dispose"/> becomes a no-op — the caller now owns closing
    /// the transport.
    /// </summary>
    public (TcpClient Client, SslStream Stream) DetachTransport()
    {
        _detached = true;
        return (_client, _stream);
    }

    public void Dispose()
    {
        if (_detached) return;
        _stream.Dispose();
        _client.Dispose();
    }
}
