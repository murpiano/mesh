using System.Net.Security;
using System.Net.Sockets;

namespace Mesh.Core;

public enum MeshSessionState { Connected, Disconnected, Reconnecting }

/// <summary>
/// A channel to a paired peer, carrying framed <see cref="MeshMessage"/> traffic after the
/// pairing handshake completes. Owns the transport from the point it is constructed (via
/// <see cref="FromTransport"/>) until <see cref="Dispose"/>.
///
/// This class only detects that the transport died (read/write failure, or no traffic within
/// the idle timeout) and reports it via <see cref="StateChanged"/> == Disconnected; it does not
/// reconnect itself. The owner (the UI layer) listens for that event and re-runs
/// <c>PairingSession.ConnectAsync</c> + <c>IsPeerTrusted</c> + <c>DetachTransport</c> on success,
/// constructing a fresh <see cref="MeshSession"/> via <see cref="FromTransport"/> to replace this
/// one — the single reconnect loop and policy (dialer/passive tie-break, backoff) live there.
/// Messages queued here while disconnected are carried over by calling
/// <see cref="DrainPendingSendsAsync"/> on this (disposed) session and re-sending them through
/// the replacement.
/// </summary>
public sealed class MeshSession : IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);

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
    // Test-only seam: production call sites always take the static defaults above. Letting tests
    // pass short durations lets them exercise the real heartbeat/idle-timeout loop without
    // waiting out the real 20s/45s production intervals.
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _idleTimeout;
    // Backing store for the session state, accessed only via Volatile.Read / Interlocked.Exchange
    // (see State / TransitionTo) so concurrent transitions from the read/heartbeat/reconnect loops
    // and SendAsync never race on the field itself and never double-fire StateChanged.
    private int _stateValue = (int)MeshSessionState.Connected;

    /// <summary>Current connection state. Anything other than Connected means this session is dead
    /// for sending purposes until the owner replaces it (see the class remarks on reconnect).</summary>
    public MeshSessionState State => (MeshSessionState)Volatile.Read(ref _stateValue);

    private MeshSession(TcpClient client, SslStream stream, string peerUuid, string peerIp, int peerPort,
        TimeSpan heartbeatInterval, TimeSpan idleTimeout)
    {
        _client = client;
        _stream = stream;
        _peerUuid = peerUuid;
        _peerIp = peerIp;
        _peerPort = peerPort;
        _heartbeatInterval = heartbeatInterval;
        _idleTimeout = idleTimeout;
    }

    /// <summary>
    /// Takes ownership of an authenticated transport and starts the read/heartbeat loops.
    /// Pass <paramref name="onMessageReceived"/>/<paramref name="onStateChanged"/> here rather than
    /// subscribing to the events afterwards: they are attached before the loops start, so a message
    /// the peer sent immediately (already buffered on the stream, whose first read can complete
    /// synchronously inside this call) can never be raised with no subscriber and silently dropped.
    /// <paramref name="heartbeatInterval"/>/<paramref name="idleTimeout"/> are a test-only seam
    /// (production callers always take the defaults) so tests can exercise the real loop without
    /// waiting out the real production intervals.
    /// </summary>
    public static MeshSession FromTransport(TcpClient client, SslStream stream, string peerUuid, string peerIp,
        int peerPort, Action<MeshMessage>? onMessageReceived = null, Action<MeshSessionState>? onStateChanged = null,
        TimeSpan? heartbeatInterval = null, TimeSpan? idleTimeout = null)
    {
        var session = new MeshSession(client, stream, peerUuid, peerIp, peerPort,
            heartbeatInterval ?? HeartbeatInterval, idleTimeout ?? IdleTimeout);
        if (onMessageReceived != null) session.MessageReceived += onMessageReceived;
        if (onStateChanged != null) session.StateChanged += onStateChanged;
        DebugLog.Write($"FromTransport: new session for peer {peerUuid} at {peerIp}:{peerPort}");
        _ = session.RunReadLoopAsync(session._lifetimeCts.Token);
        _ = session.RunHeartbeatLoopAsync(session._lifetimeCts.Token);
        return session;
    }

    public async Task SendAsync(MeshMessage message)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (State != MeshSessionState.Connected || _stream == null)
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
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Empties the queue of messages that <see cref="SendAsync"/> accepted while this session was
    /// not connected (or whose write failed) and returns them in their original order, so a
    /// caller replacing this session with a fresh one (see the class remarks on reconnect) can
    /// re-send them over the new transport instead of losing them. Heartbeats are dropped: they
    /// are only meaningful on the transport they were generated for. Waits for any in-flight
    /// <see cref="SendAsync"/> to finish first, so call it after <see cref="Dispose"/> to make
    /// sure nothing else is enqueued afterwards. Asynchronous on purpose: a <see cref="SendAsync"/>
    /// started on a UI thread resumes on that thread while holding the send lock, so a blocking
    /// wait here from the same thread could deadlock.
    /// </summary>
    public async Task<IReadOnlyList<MeshMessage>> DrainPendingSendsAsync()
    {
        await _sendLock.WaitAsync();
        try
        {
            var drained = new List<MeshMessage>(_pendingSend.Count);
            while (_pendingSend.Count > 0)
            {
                var message = _pendingSend.Dequeue();
                if (message is not HeartbeatMessage) drained.Add(message);
            }
            return drained;
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
                if (stream == null || State != MeshSessionState.Connected)
                {
                    await Task.Delay(200, ct);
                    continue;
                }
                var message = await MeshMessage.ReadAsync(stream);
                _lastReceivedAt = DateTimeOffset.UtcNow;
                DebugLog.Write($"RunReadLoopAsync: received {message.GetType().Name}");
                if (message is not HeartbeatMessage)
                    MessageReceived?.Invoke(message);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"RunReadLoopAsync: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                if (ct.IsCancellationRequested) return;
                TransitionTo(MeshSessionState.Disconnected);
                // Avoid a tight failure spin while the owner's reconnect orchestration catches up.
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_heartbeatInterval, ct); }
            catch (OperationCanceledException) { return; }

            if (State != MeshSessionState.Connected) continue;

            // The idle-timeout check must run regardless of whether the send lock below can be
            // acquired: a heartbeat send stuck behind an in-flight large-clip write (peer asleep,
            // TCP send buffer full) must never prevent us from noticing a dead connection. Do NOT
            // gate this on a successful lock acquisition.
            if (DateTimeOffset.UtcNow - _lastReceivedAt > _idleTimeout)
            {
                DebugLog.Write($"RunHeartbeatLoopAsync: idle timeout, lastReceivedAt={_lastReceivedAt:HH:mm:ss.fff}");
                TransitionTo(MeshSessionState.Disconnected);
                continue;
            }

            await TrySendHeartbeatAsync();
        }
    }

    /// <summary>
    /// Non-blocking heartbeat send: if <see cref="_sendLock"/> is already held by an in-flight
    /// <see cref="SendAsync"/> (e.g. a large clip currently being written), skip sending the
    /// heartbeat this cycle rather than block waiting for the lock. The idle-timeout check in
    /// <see cref="RunHeartbeatLoopAsync"/> runs independently of this and is what actually
    /// detects a dead connection.
    /// </summary>
    private async Task TrySendHeartbeatAsync()
    {
        if (!_sendLock.Wait(0)) return; // a send is already in progress; skip this cycle
        try
        {
            if (State != MeshSessionState.Connected || _stream == null) return;
            try
            {
                await MeshMessage.WriteAsync(_stream, new HeartbeatMessage());
            }
            catch (Exception ex)
            {
                DebugLog.Write($"TrySendHeartbeatAsync: write failed {ex.GetType().Name}: {ex.Message}");
                TransitionTo(MeshSessionState.Disconnected);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void TransitionTo(MeshSessionState state)
    {
        // Atomically swap in the new state and capture whatever was there before. Exchange is a
        // single indivisible read-modify-write, so when two loops race to transition at the same
        // time, exactly one of them observes previous != state and fires StateChanged; the other
        // sees the state already matches what it was about to set and does nothing. This closes
        // the duplicate-event race without a lock around the whole class.
        var previous = (MeshSessionState)Interlocked.Exchange(ref _stateValue, (int)state);
        if (previous != state)
        {
            if (state == MeshSessionState.Disconnected)
            {
                // Close the transport so a write currently stuck trying to flush into a now-dead
                // connection (e.g. peer asleep, TCP send buffer full) unblocks immediately with an
                // IOException/ObjectDisposedException instead of hanging until the OS's own TCP
                // retransmission timeout (which can be ~15 minutes). The unblocked SendAsync then
                // correctly routes the message into the pending-send queue via its existing catch.
                try { _stream?.Dispose(); } catch { /* best effort */ }
                try { _client?.Dispose(); } catch { /* best effort */ }
            }
            StateChanged?.Invoke(state);
        }
    }

    public void Dispose()
    {
        _lifetimeCts.Cancel();
        _stream?.Dispose();
        _client?.Dispose();
    }
}
