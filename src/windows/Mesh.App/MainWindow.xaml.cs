using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Mesh.Core;

namespace Mesh.App;

public partial class MainWindow : Window, IPairingPrompt
{
    private static readonly string MeshDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mesh");
    private readonly string _pairedDevicesPath = Path.Combine(MeshDir, "paired_devices.json");
    private readonly string _clipboardDbPath = Path.Combine(MeshDir, "clipboard_history.db");

    /// <summary>Clipboard texts longer than this (1 MiB of chars) are neither stored nor pushed.</summary>
    private const int MaxClipboardTextLength = 1_048_576;
    /// <summary>Mirrors <see cref="ClipboardHistoryStore"/>'s row cap so the list matches the DB.</summary>
    private const int ClipboardHistoryCap = 50;

    /// <summary>Delays between UI-level trusted-reconnect attempts after a session drops.</summary>
    private static readonly TimeSpan[] MeshReconnectBackoff =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Symmetry break for background reconnects: only the side whose device UUID sorts lower
    /// (ordinal) dials out; the other side waits for that incoming connection on its accept loop.
    /// The waiting side still dials as a liveness fallback, but only after this grace period and
    /// then at <see cref="PassiveReconnectInterval"/>, so it never runs in lockstep with the dialer.
    /// </summary>
    private static readonly TimeSpan PassiveReconnectGrace = TimeSpan.FromSeconds(120);
    /// <summary>Deliberately not a multiple of the dialer's 30 s cap, so the two schedules drift apart.</summary>
    private static readonly TimeSpan PassiveReconnectInterval = TimeSpan.FromSeconds(45);

    private enum ReconnectAttemptResult { Reconnected, Failed, TrustLost }

    private readonly MeshDiscovery _discovery = new();
    private readonly ObservableCollection<DiscoveredDevice> _devices = new();
    private readonly ObservableCollection<ClipboardEntry> _clipboardHistory = new();
    private readonly X509Certificate2 _identity;
    private readonly string _selfUuid;
    private readonly string _selfName = Environment.MachineName;
    private readonly PairingListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private ClipboardWatcher? _clipboardWatcher;

    // All pairing work is marshalled onto the UI thread, so a plain flag is enough to keep
    // a double-click or an incoming connection from racing an attempt already in flight.
    private bool _pairingInFlight;

    // The persistent post-pairing channel to the (single) connected peer, plus what we need to
    // re-establish it. Only touched on the UI thread.
    private MeshSession? _meshSession;
    private string? _meshPeerUuid;
    private string? _meshPeerName;
    private (string Ip, int Port) _meshPeerEndpoint = ("", 0);
    // The session the (single) running reconnect loop is currently trying to replace; null when no
    // loop is running. Tracking the instance instead of a plain "in flight" flag lets a newer
    // session's disconnect retarget a loop that was started for an older, since-replaced session.
    private MeshSession? _meshReconnectTarget;
    // A dead session whose peer no longer passes the trust check: retrying can never succeed, so
    // it is left alone until the user re-pairs (which replaces it).
    private MeshSession? _meshTrustLostSession;

    public MainWindow()
    {
        InitializeComponent();
        DeviceListBox.ItemsSource = _devices;
        ClipboardHistoryListBox.ItemsSource = _clipboardHistory;

        // Loaded once and cached for the window's lifetime.
        _identity = IdentityStore.LoadOrCreate(Path.Combine(MeshDir, "identity.pfx"));
        _selfUuid = DeviceIdentity.GetOrCreateUuid(Path.Combine(MeshDir, "device_id.txt"));

        // Port 0: the OS picks a free ephemeral port; that actual port is what we advertise.
        _listener = new PairingListener(0);
        _ = ListenLoopAsync();
        StartDiscovery();

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var entry in ClipboardHistoryStore.GetAll(_clipboardDbPath)) _clipboardHistory.Add(entry);
        // The HWND exists by now, which the clipboard listener needs.
        _clipboardWatcher = new ClipboardWatcher(this);
        _clipboardWatcher.TextCopied += OnLocalTextCopied;
    }

    private void StartDiscovery()
    {
        MdnsErrorBanner.Visibility = Visibility.Collapsed;
        try
        {
            _discovery.Advertise(_selfUuid, _selfName, "windows", _listener.Port);
        }
        catch (MeshDiscoveryException)
        {
            ShowMdnsError();
        }
        _discovery.StartBrowsing(
            device => Dispatcher.InvokeAsync(() => OnDeviceFound(device)),
            uuid => Dispatcher.InvokeAsync(() => OnDeviceLost(uuid)),
            _ => Dispatcher.InvokeAsync(ShowMdnsError));
    }

    private void ShowMdnsError() => MdnsErrorBanner.Visibility = Visibility.Visible;

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => StartDiscovery();

    private void OnDeviceFound(DiscoveredDevice device)
    {
        if (device.Uuid == _selfUuid) return; // our own announcement
        var existing = _devices.FirstOrDefault(d => d.Uuid == device.Uuid);
        if (existing != null)
        {
            // Same peer, possibly new IP/port after a restart: replace in place.
            _devices[_devices.IndexOf(existing)] = device;
            return;
        }
        _devices.Add(device);
    }

    private void OnDeviceLost(string uuid)
    {
        var existing = _devices.FirstOrDefault(d => d.Uuid == uuid);
        if (existing != null) _devices.Remove(existing);
    }

    private async Task ListenLoopAsync()
    {
        // Runs for the window's lifetime: one incoming connection at a time, then back to
        // listening. Continuations resume on the UI thread (captured dispatcher context).
        await _listener.RunAcceptLoopAsync(_identity, HandleIncomingAsync,
            ex => Dispatcher.InvokeAsync(() => StatusText.Text = $"Incoming connection failed: {ex.Message}"),
            _shutdown.Token);
    }

    private async Task HandleIncomingAsync(PairingSession session)
    {
        if (_pairingInFlight) return; // busy: the loop closes this session; peer sees a failure
        _pairingInFlight = true;
        ConnectButton.IsEnabled = false;
        try
        {
            var outcome = await session.RunAsync(_selfUuid, _selfName, _pairedDevicesPath, this);
            var peerName = outcome.PeerName ?? session.PeerIp;
            // A trusted reconnect from the peer we already have a mesh session with is the peer's
            // background reconnect replacing that session: report it quietly, the same way our own
            // background reconnect does, instead of popping a dialog on every network blip.
            var isBackgroundReconnect = outcome.Result == PairingResult.Reconnected &&
                _meshSession != null && _meshPeerUuid == outcome.PeerUuid;
            if (isBackgroundReconnect)
                StatusText.Text = $"Reconnected to {peerName} (trusted).";
            else
                ShowOutcome(outcome, peerName);
            if (outcome.Result is PairingResult.Paired or PairingResult.Reconnected)
            {
                // The socket we accepted came from the peer's ephemeral port, so its listening
                // endpoint (needed to reconnect later) comes from mDNS when we have it.
                var endpoint = ResolvePeerEndpoint(outcome.PeerUuid!, (session.PeerIp, 0));
                await AttachMeshSessionAsync(session, outcome.PeerUuid!, peerName, endpoint);
            }
        }
        finally
        {
            _pairingInFlight = false;
            ConnectButton.IsEnabled = true;
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceListBox.SelectedItem is not DiscoveredDevice target) return;
        if (_pairingInFlight) return;
        _pairingInFlight = true;
        ConnectButton.IsEnabled = false;
        try
        {
            PairingSession session;
            try
            {
                session = await PairingSession.ConnectAsync(_identity, target.IpAddress, target.Port);
            }
            catch (Exception ex)
            {
                ShowOutcome(new PairingOutcome(PairingResult.ConnectionFailed, target.Uuid, target.Name, ex.Message),
                    target.Name);
                return;
            }

            // On success the transport is detached into a MeshSession, which makes this
            // using's Dispose a no-op; on every other outcome it closes the socket as before.
            using (session)
            {
                var outcome = await session.RunAsync(_selfUuid, _selfName, _pairedDevicesPath, this);
                var peerName = outcome.PeerName ?? target.Name;
                ShowOutcome(outcome, peerName);
                if (outcome.Result is PairingResult.Paired or PairingResult.Reconnected)
                {
                    await AttachMeshSessionAsync(session, outcome.PeerUuid!, peerName,
                        (target.IpAddress, target.Port));
                }
            }
        }
        finally
        {
            _pairingInFlight = false;
            ConnectButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// "Connection failed" = TCP/TLS/transport problem (incl. the 10 s timeout);
    /// "Pairing failed" = connected, but the PIN was rejected/declined/timed out.
    /// Shown non-blocking so the accept loop is never held up by an open message box.
    /// </summary>
    private void ShowOutcome(PairingOutcome outcome, string peerName)
    {
        var text = outcome.Result switch
        {
            PairingResult.Paired => $"Paired with {peerName}.",
            PairingResult.Reconnected => $"Reconnected to {peerName} (trusted).",
            PairingResult.ConnectionFailed => $"Connection failed: {outcome.Detail}",
            _ => "Pairing failed."
        };
        StatusText.Text = text;
        Dispatcher.InvokeAsync(() => MessageBox.Show(this, text, "Mesh"));
    }

    // Mesh session lifecycle -------------------------------------------------------------

    /// <summary>
    /// Takes over <paramref name="session"/>'s authenticated transport as the new
    /// <see cref="MeshSession"/>, replacing (and disposing) any previous one. Messages the old
    /// session queued while it was disconnected are re-sent over the new one when it is the
    /// same peer, so nothing copied during an outage is lost.
    /// </summary>
    private async Task AttachMeshSessionAsync(PairingSession session, string peerUuid, string peerName,
        (string Ip, int Port) peerEndpoint)
    {
        var (transportClient, transportStream) = session.DetachTransport();
        var previous = _meshSession;
        var previousPeerUuid = _meshPeerUuid;

        // Handlers go into FromTransport itself so they are attached before its read loop starts:
        // a clip the peer sends right after reconnecting can never hit an unsubscribed event.
        // The state handler only reads `current` once marshalled onto the UI thread, which is after
        // this (UI-thread) method has assigned it below.
        MeshSession? current = null;
        current = MeshSession.FromTransport(transportClient, transportStream, peerUuid,
            peerEndpoint.Ip, peerEndpoint.Port,
            onMessageReceived: message => OnMeshMessageReceived(message, peerName),
            onStateChanged: state => Dispatcher.InvokeAsync(() => OnMeshSessionStateChanged(current!, state)));
        _meshSession = current;
        _meshPeerUuid = peerUuid;
        _meshPeerName = peerName;
        _meshPeerEndpoint = peerEndpoint;
        _meshTrustLostSession = null; // only ever refers to a session this one has just replaced
        SessionStatusText.Text = $"Connected to {peerName}";

        if (previous == null) return;
        // Dispose first so the old session cannot accept anything more, then drain (which waits
        // for any in-flight send on it to settle). Only carry clipboard data over to the same
        // peer: content copied while linked to one device should not leak to a different one.
        previous.Dispose();
        var pending = await previous.DrainPendingSendsAsync();
        if (previousPeerUuid != peerUuid) return;
        foreach (var message in pending)
        {
            await current.SendAsync(message); // queues again (in order) if this one drops too
        }
    }

    private void OnMeshSessionStateChanged(MeshSession session, MeshSessionState state)
    {
        // Runs on the UI thread (the handler passed to FromTransport marshals it here).
        if (!ReferenceEquals(session, _meshSession)) return; // a replaced/disposed session
        if (ReferenceEquals(session, _meshTrustLostSession)) return; // keep "Re-pairing required"
        SessionStatusText.Text = state switch
        {
            MeshSessionState.Connected => $"Connected to {_meshPeerName}",
            MeshSessionState.Reconnecting => $"Reconnecting to {_meshPeerName}...",
            MeshSessionState.Disconnected => $"Disconnected from {_meshPeerName}",
            _ => SessionStatusText.Text
        };
        if (state == MeshSessionState.Disconnected) EnsureMeshReconnect(session);
    }

    /// <summary>
    /// Makes sure a reconnect loop is working on <paramref name="session"/>. At most one loop runs;
    /// if one is already running (possibly still asleep in backoff for an older, since-replaced
    /// session) it is retargeted to this session and picks it up when it wakes.
    /// </summary>
    private void EnsureMeshReconnect(MeshSession session)
    {
        var loopRunning = _meshReconnectTarget != null;
        _meshReconnectTarget = session;
        if (!loopRunning) _ = RunMeshReconnectLoopAsync();
    }

    /// <summary>A current session that is down and still worth reconnecting.</summary>
    private bool NeedsMeshReconnect(MeshSession? session) =>
        session != null && session.State != MeshSessionState.Connected &&
        !ReferenceEquals(session, _meshTrustLostSession);

    /// <summary>
    /// UI-level reconnect orchestration. <see cref="MeshSession"/> only probes TCP reachability;
    /// re-establishing a usable channel needs a fresh TLS connection plus the trusted-reconnect
    /// handshake (identity + mutual trust, no PIN), which needs our identity and paired-device
    /// store. Runs on the UI thread, so every check-then-act below is atomic with respect to the
    /// state handler and the accept loop.
    ///
    /// The loop keeps going until the current <see cref="_meshSession"/> is healthy. Before every
    /// sleep and after every wake/attempt it re-decides what to work on: a newer target set by
    /// <see cref="EnsureMeshReconnect"/>, or — if its target was replaced (e.g. the peer reconnected
    /// to us) — whatever session is current now, if that one is itself down. So a session that
    /// drops while a loop for an older one is asleep is never left without a reconnect attempt.
    ///
    /// Only the side whose UUID sorts lower dials straight away (see
    /// <see cref="PassiveReconnectGrace"/>); a peer whose trust check fails stops the loop for good.
    /// </summary>
    private async Task RunMeshReconnectLoopAsync()
    {
        MeshSession? target = null;
        var attempt = 0;
        try
        {
            while (true)
            {
                if (!ReferenceEquals(target, _meshReconnectTarget))
                {
                    target = _meshReconnectTarget; // retargeted by a newer disconnect
                    attempt = 0;
                }
                if (!ReferenceEquals(_meshSession, target))
                {
                    // Replaced while we were asleep or attempting. Adopt the replacement if it is
                    // down too; otherwise there is nothing left to do.
                    if (!NeedsMeshReconnect(_meshSession)) return;
                    target = _meshReconnectTarget = _meshSession;
                    attempt = 0;
                }
                if (!NeedsMeshReconnect(target)) return;

                var peerUuid = _meshPeerUuid!;
                var peerName = _meshPeerName!;
                var isDialer = string.CompareOrdinal(_selfUuid, peerUuid) < 0;
                var delay = isDialer
                    ? MeshReconnectBackoff[Math.Min(attempt, MeshReconnectBackoff.Length - 1)]
                    : attempt == 0 ? PassiveReconnectGrace : PassiveReconnectInterval;
                attempt++;
                try { await Task.Delay(delay, _shutdown.Token); }
                catch (OperationCanceledException) { return; }

                // Anything may have changed while asleep: re-decide before dialing.
                if (!ReferenceEquals(target, _meshReconnectTarget) || !ReferenceEquals(_meshSession, target)) continue;
                if (_pairingInFlight) continue; // a user/incoming pairing owns the floor; retry later

                // Re-resolve every attempt: mDNS updates the peer's IP/port if it restarted or
                // moved networks; otherwise fall back to the last endpoint we connected through.
                var (ip, port) = ResolvePeerEndpoint(peerUuid, _meshPeerEndpoint);
                if (port <= 0) continue; // listening port not known yet (incoming-only peer)

                ReconnectAttemptResult result;
                _pairingInFlight = true;
                ConnectButton.IsEnabled = false;
                try
                {
                    result = await TryTrustedReconnectAsync(peerUuid, peerName, ip, port);
                }
                finally
                {
                    _pairingInFlight = false;
                    ConnectButton.IsEnabled = true;
                }

                if (result == ReconnectAttemptResult.TrustLost && ReferenceEquals(_meshSession, target))
                {
                    // The peer answered as itself but the trust check failed (certificate changed,
                    // or either side removed the pairing). No amount of retrying fixes that.
                    _meshTrustLostSession = target;
                    SessionStatusText.Text = $"Re-pairing required with {peerName}";
                    StatusText.Text = $"{peerName} is no longer trusted (certificate changed or pairing " +
                        "removed). Select it and press Connect to pair again.";
                }
                // Reconnected / TrustLost / Failed: the checks at the top decide whether to stop.
            }
        }
        finally
        {
            _meshReconnectTarget = null;
        }
    }

    /// <summary>
    /// One silent trusted-reconnect attempt: connect, exchange identities, and proceed only if the
    /// peer is the expected one and both sides still trust each other. Never prompts the user —
    /// a peer that is no longer trusted needs an explicit re-pair via the Connect button.
    /// Returns <see cref="ReconnectAttemptResult.TrustLost"/> only when the expected peer answered
    /// but trust failed on either side (permanent); network errors, a busy peer or a different
    /// device on that endpoint are <see cref="ReconnectAttemptResult.Failed"/> (worth retrying).
    /// </summary>
    private async Task<ReconnectAttemptResult> TryTrustedReconnectAsync(string peerUuid, string peerName,
        string ip, int port)
    {
        PairingSession session;
        try
        {
            session = await PairingSession.ConnectAsync(_identity, ip, port, _shutdown.Token);
        }
        catch (Exception)
        {
            return ReconnectAttemptResult.Failed;
        }

        using (session)
        {
            try
            {
                await session.ExchangeIdentityAsync(_selfUuid, _selfName);
                // Someone else now on that IP:port (e.g. DHCP reuse): not our peer, keep trying.
                if (session.PeerUuid != peerUuid) return ReconnectAttemptResult.Failed;
                // Our peer, but its certificate no longer matches our record (or the record is gone).
                if (!session.IsPeerTrusted(_pairedDevicesPath, peerUuid)) return ReconnectAttemptResult.TrustLost;
                // ExchangeTrustAsync returns false only if the peer explicitly answered "not
                // trusted"; a busy/closed peer throws instead and lands in the catch below.
                if (!await session.ExchangeTrustAsync(localTrustsPeer: true))
                    return ReconnectAttemptResult.TrustLost;
            }
            catch (Exception)
            {
                return ReconnectAttemptResult.Failed;
            }

            // Same bookkeeping RunAsync does on a trusted reconnect.
            var record = PairedDeviceStore.Find(_pairedDevicesPath, peerUuid);
            if (record != null)
            {
                record.LastIp = session.PeerIp;
                record.Name = session.PeerName ?? record.Name;
                PairedDeviceStore.Upsert(_pairedDevicesPath, record);
            }

            var name = session.PeerName ?? peerName;
            await AttachMeshSessionAsync(session, peerUuid, name, (ip, port));
            StatusText.Text = $"Reconnected to {name} (trusted).";
            return ReconnectAttemptResult.Reconnected;
        }
    }

    /// <summary>The peer's listening endpoint as currently advertised over mDNS, if seen.</summary>
    private (string Ip, int Port) ResolvePeerEndpoint(string peerUuid, (string Ip, int Port) fallback)
    {
        var discovered = _devices.FirstOrDefault(d => d.Uuid == peerUuid);
        return discovered != null ? (discovered.IpAddress, discovered.Port) : fallback;
    }

    // Clipboard ------------------------------------------------------------------------------

    private void OnLocalTextCopied(string text)
    {
        // Raised on the UI thread (window message hook).
        if (text.Length > MaxClipboardTextLength) return;
        ClipboardEntry entry;
        try
        {
            entry = ClipboardHistoryStore.Insert(_clipboardDbPath, text, "local", DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save clipboard history: {ex.Message}";
            return;
        }
        AddHistoryEntry(entry);
        // While disconnected this queues inside the session and is carried over on reconnect.
        _ = _meshSession?.SendAsync(new ClipboardPushMessage(entry.Timestamp, text));
    }

    private void OnMeshMessageReceived(MeshMessage message, string peerName)
    {
        // Raised on MeshSession's read loop (background thread).
        if (message is not ClipboardPushMessage push) return;
        if (push.Text.Length > MaxClipboardTextLength) return;
        ClipboardEntry entry;
        try
        {
            entry = ClipboardHistoryStore.Insert(_clipboardDbPath, push.Text, peerName, push.Timestamp);
        }
        catch (Exception ex)
        {
            Dispatcher.InvokeAsync(() => StatusText.Text = $"Could not save clipboard history: {ex.Message}");
            return;
        }
        Dispatcher.InvokeAsync(() => AddHistoryEntry(entry));
    }

    private void AddHistoryEntry(ClipboardEntry entry)
    {
        _clipboardHistory.Insert(0, entry);
        while (_clipboardHistory.Count > ClipboardHistoryCap) _clipboardHistory.RemoveAt(_clipboardHistory.Count - 1);
    }

    private void CopyHistoryEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ClipboardEntry entry }) return;
        // Our own write must not come back through the watcher and get re-pushed.
        _clipboardWatcher?.SuppressNextChange(TimeSpan.FromMilliseconds(500));
        try
        {
            Clipboard.SetText(entry.Text);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            StatusText.Text = $"Clipboard is busy, try again: {ex.Message}";
        }
    }

    // IPairingPrompt -------------------------------------------------------------------

    public Task<bool> ConfirmCertificateChangedAsync(string peerName, CancellationToken ct) =>
        ShowConfirmAsync("Certificate changed",
            $"Certificate changed for {peerName} — re-pair?\n\nThis device no longer presents the " +
            "certificate you paired with. Only continue if you know why (e.g. the app was reinstalled).",
            pin: null, "Re-pair", "Cancel", ct);

    public Task<bool> ConfirmPinAsync(string peerName, string pin, CancellationToken ct) =>
        ShowConfirmAsync("Confirm PIN", $"Does this PIN match the one shown on {peerName}?",
            pin, "Match", "Not matching", ct);

    private async Task<bool> ShowConfirmAsync(string title, string message, string? pin,
        string yes, string no, CancellationToken ct)
    {
        var window = new PinConfirmWindow(title, message, pin, yes, no) { Owner = this };
        var closed = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => closed.TrySetResult(window.Result);
        // Timeout or peer abort: close the dialog, which counts as "not matching".
        using var registration = ct.Register(() => Dispatcher.InvokeAsync(() =>
        {
            if (window.IsVisible) window.Close();
        }));
        window.Show();
        return await closed.Task;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _shutdown.Cancel();
        _clipboardWatcher?.Dispose();
        _meshSession?.Dispose();
        _listener.Dispose();
        _discovery.Dispose();
    }
}
