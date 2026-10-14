using System.Net.Security;
using System.Net.Sockets;

namespace Mesh.Core;

public enum MeshSessionState { Connected, Disconnected, Reconnecting }

/// <summary>
/// A persistent, reconnecting channel to a paired peer, carrying framed <see cref="MeshMessage"/>
/// traffic after the pairing handshake completes. Owns the transport from the point it is
/// constructed (via <see cref="FromTransport"/>) until <see cref="Dispose"/>.
///
/// Reconnect scope note: this class only probes whether the peer's last-known IP:port is
/// reachable again at the TCP level; it deliberately does NOT re-run the trusted-pairing
/// handshake (that needs the local identity certificate and paired-device store path, which
/// this class does not hold). Task 5 wires the UI layer to listen for
/// <see cref="StateChanged"/> == Disconnected, re-run <c>PairingSession.ConnectAsync</c> +
/// <c>IsPeerTrusted</c> + <c>DetachTransport</c> on success, and construct a fresh
/// <see cref="MeshSession"/> via <see cref="FromTransport"/> to replace this one.
/// </summary>
public sealed class MeshSession : IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan[] BackoffSteps =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)
    };
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    public event Action<MeshMessage>? MessageReceived;
    public event Action<MeshSessionState>? StateChanged;

    private readonly string _peerUuid;
    private readonly int _peerPort;
    private string _peerIp;
    private TcpClient? _client;
    private SslStream? _stream;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Queue<MeshMessage> _pendingSend = new();
    private DateTimeOffset _lastReceivedAt = DateTimeOffset.UtcNow;
    private MeshSessionState _state = MeshSessionState.Connected;
    private int _reconnecting; // 0/1 guard so we don't start multiple reconnect loops concurrently

    private MeshSession(TcpClient client, SslStream stream, string peerUuid, string peerIp, int peerPort)
    {
        _client = client;
        _stream = stream;
        _peerUuid = peerUuid;
        _peerIp = peerIp;
        _peerPort = peerPort;
    }

    public static MeshSession FromTransport(TcpClient client, SslStream stream, string peerUuid, string peerIp, int peerPort)
    {
        var session = new MeshSession(client, stream, peerUuid, peerIp, peerPort);
        _ = session.RunReadLoopAsync(session._lifetimeCts.Token);
        _ = session.RunHeartbeatLoopAsync(session._lifetimeCts.Token);
        return session;
    }

    public async Task SendAsync(MeshMessage message)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (_state != MeshSessionState.Connected || _stream == null)
            {
                _pendingSend.Enqueue(message);
                return;
            }
            try
            {
                await MeshMessage.WriteAsync(_stream, message);
            }
            catch
            {
                _pendingSend.Enqueue(message);
                TransitionTo(MeshSessionState.Disconnected);
                StartReconnectLoop();
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task RunReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var stream = _stream;
                if (stream == null || _state != MeshSessionState.Connected)
                {
                    await Task.Delay(200, ct);
                    continue;
                }
                var message = await MeshMessage.ReadAsync(stream);
                _lastReceivedAt = DateTimeOffset.UtcNow;
                if (message is not HeartbeatMessage)
                    MessageReceived?.Invoke(message);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                if (ct.IsCancellationRequested) return;
                TransitionTo(MeshSessionState.Disconnected);
                StartReconnectLoop();
                // Avoid a tight failure spin while a reconnect attempt is (or isn't) running.
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(HeartbeatInterval, ct); }
            catch (OperationCanceledException) { return; }

            if (_state == MeshSessionState.Connected)
            {
                if (DateTimeOffset.UtcNow - _lastReceivedAt > IdleTimeout)
                {
                    TransitionTo(MeshSessionState.Disconnected);
                    StartReconnectLoop();
                    continue;
                }
                try { await SendAsync(new HeartbeatMessage()); }
                catch { /* SendAsync already handles failure by reconnecting */ }
            }
        }
    }

    private void StartReconnectLoop()
    {
        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) == 0)
        {
            _ = ReconnectLoopAsync(_lifetimeCts.Token);
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        try
        {
            TransitionTo(MeshSessionState.Reconnecting);
            var attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                attempt++;
                var ip = _peerIp;
                if (attempt == 5)
                {
                    // After 4 direct-IP attempts, try one mDNS re-discovery pass.
                    var rediscovered = await TryRediscoverIpAsync(ct);
                    if (rediscovered != null) ip = _peerIp = rediscovered;
                }

                try
                {
                    // Reconnect here only probes that the already-trusted peer's transport is
                    // reachable again; it intentionally does not repeat the PIN/trust protocol.
                    using var probe = new TcpClient();
                    await probe.ConnectAsync(ip, _peerPort, ct);
                    // NOTE: full reconnect (TLS re-handshake against the pinned fingerprint) is
                    // wired through PairingSession.ConnectAsync + trusted-check by the caller that
                    // owns identity/paired-store paths; this class only establishes that the
                    // transport is reachable again. See Task 5 for how the UI layer re-runs the
                    // trusted-reconnect handshake and calls a new FromTransport on success.
                    return; // Signal: caller (Task 5 UI wiring) must re-run trusted handshake and replace this session.
                }
                catch
                {
                    if (ct.IsCancellationRequested) return;
                    var delay = attempt <= BackoffSteps.Length ? BackoffSteps[attempt - 1] : MaxBackoff;
                    try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    private Task<string?> TryRediscoverIpAsync(CancellationToken ct)
    {
        // Left as a hook: a full mDNS re-discovery pass needs a running MeshDiscovery instance,
        // which this class does not own. Task 5's UI-layer reconnect orchestration performs
        // the actual re-discovery and passes a fresh IP back in; this returns null here so the
        // backoff loop simply keeps retrying the last-known IP if nothing calls in with a new one.
        return Task.FromResult<string?>(null);
    }

    private void TransitionTo(MeshSessionState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        _lifetimeCts.Cancel();
        _stream?.Dispose();
        _client?.Dispose();
    }
}
