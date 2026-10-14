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
    private bool _meshReconnectInFlight;

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

        var current = MeshSession.FromTransport(transportClient, transportStream, peerUuid,
            peerEndpoint.Ip, peerEndpoint.Port);
        _meshSession = current;
        _meshPeerUuid = peerUuid;
        _meshPeerName = peerName;
        _meshPeerEndpoint = peerEndpoint;
        current.MessageReceived += message => OnMeshMessageReceived(message, peerName);
        current.StateChanged += state => OnMeshSessionStateChanged(current, state);
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
        // Raised on MeshSession's background loops.
        Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(session, _meshSession)) return; // a replaced/disposed session
            SessionStatusText.Text = state switch
            {
                MeshSessionState.Connected => $"Connected to {_meshPeerName}",
                MeshSessionState.Reconnecting => $"Reconnecting to {_meshPeerName}...",
                MeshSessionState.Disconnected => $"Disconnected from {_meshPeerName}",
                _ => SessionStatusText.Text
            };
            if (state == MeshSessionState.Disconnected) _ = ReconnectMeshSessionAsync(session);
        });
    }

    /// <summary>
    /// UI-level reconnect orchestration. <see cref="MeshSession"/> only probes TCP reachability;
    /// re-establishing a usable channel needs a fresh TLS connection plus the trusted-reconnect
    /// handshake (identity + mutual trust, no PIN), which needs our identity and paired-device
    /// store. Retries with backoff until a new session replaces <paramref name="stale"/> — either
    /// from here, or because the peer reconnected to us through the accept loop.
    /// </summary>
    private async Task ReconnectMeshSessionAsync(MeshSession stale)
    {
        if (_meshReconnectInFlight) return;
        _meshReconnectInFlight = true;
        var peerUuid = _meshPeerUuid!;
        var peerName = _meshPeerName!;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var delay = MeshReconnectBackoff[Math.Min(attempt, MeshReconnectBackoff.Length - 1)];
                try { await Task.Delay(delay, _shutdown.Token); }
                catch (OperationCanceledException) { return; }

                if (!ReferenceEquals(_meshSession, stale)) return; // already replaced
                if (_pairingInFlight) continue; // a user/incoming pairing owns the floor; retry later

                // Re-resolve every attempt: mDNS updates the peer's IP/port if it restarted or
                // moved networks; otherwise fall back to the last endpoint we connected through.
                var (ip, port) = ResolvePeerEndpoint(peerUuid, _meshPeerEndpoint);
                if (port <= 0) continue; // listening port not known yet (incoming-only peer)

                _pairingInFlight = true;
                ConnectButton.IsEnabled = false;
                try
                {
                    if (await TryTrustedReconnectAsync(peerUuid, peerName, ip, port)) return;
                }
                finally
                {
                    _pairingInFlight = false;
                    ConnectButton.IsEnabled = true;
                }
            }
        }
        finally
        {
            _meshReconnectInFlight = false;
        }
    }

    /// <summary>
    /// One silent trusted-reconnect attempt: connect, exchange identities, and proceed only if the
    /// peer is the expected one and both sides still trust each other. Never prompts the user —
    /// a peer that is no longer trusted needs an explicit re-pair via the Connect button.
    /// </summary>
    private async Task<bool> TryTrustedReconnectAsync(string peerUuid, string peerName, string ip, int port)
    {
        PairingSession session;
        try
        {
            session = await PairingSession.ConnectAsync(_identity, ip, port, _shutdown.Token);
        }
        catch (Exception)
        {
            return false;
        }

        using (session)
        {
            try
            {
                await session.ExchangeIdentityAsync(_selfUuid, _selfName);
                if (session.PeerUuid != peerUuid || !session.IsPeerTrusted(_pairedDevicesPath, peerUuid))
                    return false;
                if (!await session.ExchangeTrustAsync(localTrustsPeer: true))
                    return false;
            }
            catch (Exception)
            {
                return false;
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
            return true;
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
