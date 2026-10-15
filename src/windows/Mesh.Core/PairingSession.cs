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

    /// <summary>
    /// Listener side only: show the PIN and ask "Does &lt;peerName&gt; want to connect?".
    /// Return true for "Yes", false for "No".
    /// </summary>
    Task<bool> ConfirmPinAsync(string peerName, string pin, CancellationToken ct);

    /// <summary>
    /// Dialer side only, and only after the listener has already approved: ask the user to type
    /// the code shown on the listener's screen. Returns the typed digits, or null if the user
    /// cancelled/declined. <see cref="PairingSession.RunAsync"/> does the comparison against the
    /// locally-derived expected PIN — this method never sees that value, so there is nothing to
    /// leak by returning the wrong thing.
    /// </summary>
    Task<string?> EnterPinAsync(string peerName, CancellationToken ct);

    /// <summary>
    /// Listener side only, and only for an already-trusted peer that flagged its connection
    /// attempt as a manual (human-initiated) Connect: "&lt;peerName&gt; wants to connect. Allow?"
    /// No PIN involved — trust is already established; this just lets a human decline a surprise
    /// reconnect. Automatic background reconnects never reach this prompt.
    /// </summary>
    Task<bool> ConfirmReconnectAsync(string peerName, CancellationToken ct);
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

    /// <summary>True if this side called <see cref="ConnectAsync"/> (dialed out); false if it
    /// came from <see cref="AcceptAsync"/> (listening side). The two roles see different prompts
    /// during pairing: the listener sees/shows the PIN, the dialer types it.</summary>
    public bool IsDialer { get; }

    private PairingSession(TcpClient client, SslStream stream, X509Certificate2 localIdentity, bool isDialer)
    {
        _client = client;
        _stream = stream;
        _localIdentity = localIdentity;
        IsDialer = isDialer;
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

            return new PairingSession(client, sslStream, identity, isDialer: true);
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

            return new PairingSession(client, sslStream, identity, isDialer: false);
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

    /// <summary>
    /// Protocol step (new): each side announces whether this attempt is a human-initiated manual
    /// Connect, as opposed to an automatic background reconnect. Only the dialer's flag is ever
    /// meaningful — the listener always sends false (it never "manually Connects" within a single
    /// exchange) — so the return value is "did the peer request manual confirmation", read by
    /// whichever side needs it: the listener, when deciding whether to prompt before a trusted
    /// reconnect.
    /// </summary>
    public async Task<bool> ExchangeManualIntentAsync(bool isManualConnect)
    {
        byte peer = 0;
        await WithNetworkTimeout(async () =>
        {
            await PairingProtocol.WriteByteAsync(_stream, isManualConnect ? PairingProtocol.Yes : PairingProtocol.No);
            peer = await PairingProtocol.ReadByteAsync(_stream);
        });
        return peer == PairingProtocol.Yes;
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
    /// success. Never throws for network or peer errors: they are reported through the returned
    /// outcome. Does not dispose the session; callers own it (<c>using</c>).
    ///
    /// The dialer and listener play different roles throughout (see <see cref="IsDialer"/>):
    /// the listener always answers first (it is the one who sees the PIN, or who is asked
    /// "allow this reconnect?"); the dialer waits for that answer before showing anything of its
    /// own. <paramref name="isManualConnect"/> is the dialer's only input to this asymmetry: pass
    /// true for a human-initiated Connect-button press, false (the default) for an automatic
    /// background reconnect, which must stay silent on both sides.
    /// </summary>
    public async Task<PairingOutcome> RunAsync(string selfUuid, string selfName, string pairedDevicesPath,
        IPairingPrompt prompt, bool isManualConnect = false)
    {
        PeerTrust trust;
        bool peerRequestsConfirmation;
        string pin;
        try
        {
            await ExchangeIdentityAsync(selfUuid, selfName);
            peerRequestsConfirmation = await ExchangeManualIntentAsync(isManualConnect);
            trust = CheckTrust(pairedDevicesPath, PeerUuid!, PeerFingerprint);
            if (await ExchangeTrustAsync(trust == PeerTrust.Trusted))
            {
                // Both sides already trust each other: silent, UNLESS this is a manual Connect,
                // in which case the listener gets one lightweight yes/no (no PIN — trust is
                // already established) before finalizing. Automatic reconnects never reach this.
                var needsConfirmation = IsDialer ? isManualConnect : peerRequestsConfirmation;
                if (needsConfirmation)
                {
                    if (!IsDialer)
                    {
                        bool approved;
                        try { approved = await prompt.ConfirmReconnectAsync(PeerName!, default).WaitAsync(UserTimeout); }
                        catch (Exception) { approved = false; }
                        await TrySendAsync(approved ? PairingProtocol.Yes : PairingProtocol.No);
                        if (!approved) return Outcome(PairingResult.PairingFailed, "Declined on the other device.");
                    }
                    else
                    {
                        byte verdict;
                        try { verdict = await PairingProtocol.ReadByteAsync(_stream).WaitAsync(UserTimeout); }
                        catch (Exception ex) { return Outcome(PairingResult.PairingFailed, ex.Message); }
                        if (verdict != PairingProtocol.Yes)
                            return Outcome(PairingResult.PairingFailed, "The other device declined.");
                    }
                }

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

        // Step 6: user confirmation. Sequential by role, not raced: the listener (who sees the
        // PIN) answers first; only if they approve does the dialer get prompted at all — to type
        // the code, never to compare it visually. Same cryptographic guarantee as before (both
        // sides must land on the same `pin`, derived from the committed nonce exchange earlier);
        // only which side sees vs. types it changed.
        try
        {
            using var promptCts = new CancellationTokenSource(UserTimeout);

            if (!IsDialer)
            {
                // Race our own prompt against a read of the dialer's eventual answer. In the
                // happy path the dialer never sends anything until it has read ours, so this read
                // only resolves early if the dialer vanished/misbehaved — same early-cancel trick
                // as before, just scoped to this side: a dead peer closes our stale dialog instead
                // of making us sit out the full UserTimeout.
                var dialerAnswerTask = PairingProtocol.ReadByteAsync(_stream);
                _ = dialerAnswerTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                var localAnswerTask = AskUserAsync(prompt, trust, pin, promptCts.Token);

                if (await Task.WhenAny(localAnswerTask, dialerAnswerTask) == dialerAnswerTask)
                {
                    promptCts.Cancel();
                    await localAnswerTask;
                    return Outcome(PairingResult.PairingFailed, "The other device disconnected.");
                }

                var localMatch = await localAnswerTask;
                await TrySendAsync(localMatch ? PairingProtocol.Yes : PairingProtocol.No);
                if (!localMatch)
                    return Outcome(PairingResult.PairingFailed, "PIN not confirmed on this device.");

                var dialerAnswer = await dialerAnswerTask.WaitAsync(UserTimeout);
                if (dialerAnswer != PairingProtocol.Yes)
                    return Outcome(PairingResult.PairingFailed, "The other device entered the wrong code.");
            }
            else
            {
                if (trust == PeerTrust.CertificateChanged)
                {
                    bool certOk;
                    try { certOk = await prompt.ConfirmCertificateChangedAsync(PeerName!, promptCts.Token).WaitAsync(promptCts.Token); }
                    catch (Exception) { certOk = false; }
                    if (!certOk)
                    {
                        await TrySendAsync(PairingProtocol.No); // nothing sent yet; tell the listener we're bailing
                        return Outcome(PairingResult.PairingFailed, "Certificate change not confirmed.");
                    }
                }

                byte peerAnswer;
                try { peerAnswer = await PairingProtocol.ReadByteAsync(_stream).WaitAsync(UserTimeout); }
                catch (Exception ex) { return Outcome(PairingResult.PairingFailed, ex.Message); }
                if (peerAnswer != PairingProtocol.Yes)
                    return Outcome(PairingResult.PairingFailed, "The other device did not confirm the PIN.");

                string? typed;
                try { typed = await prompt.EnterPinAsync(PeerName!, promptCts.Token).WaitAsync(UserTimeout); }
                catch (Exception) { typed = null; }
                var matches = typed == pin;
                await TrySendAsync(matches ? PairingProtocol.Yes : PairingProtocol.No);
                if (!matches)
                    return Outcome(PairingResult.PairingFailed, "Entered code did not match.");
            }
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

    /// <summary>Listener side only: cert-changed check (if needed) then show-PIN-and-ask.</summary>
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
