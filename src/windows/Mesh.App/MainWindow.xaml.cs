using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Mesh.Core;

namespace Mesh.App;

/// <summary>
/// The desktop app, laid out 1:1 like the Android app: a Devices page (status dot, name, state;
/// click a paired device to open it, right-click for Connect/Disconnect/Unpair) and one device
/// page at a time (header with back arrow and connection status, chat and explorer sections).
/// Only one connection is ever live; it is chosen on the Devices page.
/// </summary>
public partial class MainWindow : Window, IPairingPrompt
{
    private static readonly string MeshDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mesh");
    private readonly string _pairedDevicesPath = Path.Combine(MeshDir, "paired_devices.json");
    private readonly string _chatDbPath = Path.Combine(MeshDir, "chat_history.db");

    /// <summary>Chat message texts longer than this (1 MiB of chars) are neither stored nor sent.</summary>
    private const int MaxChatTextLength = 1_048_576;

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

    /// <summary>
    /// A session that dies before living this long is "the same flapping episode" as whatever
    /// came before it, not a fresh problem: the reconnect loop must keep escalating backoff
    /// instead of resetting to the 1 s rung every time, or a peer that connects and immediately
    /// drops (bad Wi-Fi, a NAT/Doze hiccup) causes an endless storm of 1 s reconnect attempts
    /// that never slows down. Below the heartbeat interval, so a session that received even one
    /// heartbeat counts as having actually worked for a while.
    /// </summary>
    private static readonly TimeSpan FlapResetThreshold = TimeSpan.FromSeconds(15);

    private enum ReconnectAttemptResult { Reconnected, Failed, TrustLost }

    /// <summary>The connection status shown on a device page: stripe color plus its label.
    /// Same four states and colors as the Android app.</summary>
    private enum ConnectionStatus { Connected, Disconnected, Unpaired, Offline }

    private enum Section { Chat, Explorer }

    private readonly MeshDiscovery _discovery = new();
    // Live mDNS presence, keyed by uuid. Source of truth for "is this device online right now";
    // merged with PairedDeviceStore (on disk) into _deviceRows, the unified list the UI binds to.
    private readonly Dictionary<string, DiscoveredDevice> _discovered = new();
    private readonly ObservableCollection<DeviceRow> _deviceRows = new();
    private readonly X509Certificate2 _identity;
    private readonly string _selfUuid;
    private readonly string _selfName = Environment.MachineName;
    private readonly PairingListener _listener;
    private readonly CancellationTokenSource _shutdown = new();

    private readonly DarkPopup _popup;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    // The device page currently shown, if any — independent of which single peer is actually
    // connected right now (_meshPeerUuid/_meshSession). The page shows that peer's persisted chat
    // regardless of connection state; Send is only enabled while that peer is the connected one.
    private string? _openPeerUuid;
    private readonly ObservableCollection<ChatRowVm> _chatRows = new();
    // The device row / chat message whose popup is open, highlighted until it closes.
    private string? _highlightedDeviceUuid;
    private string? _highlightedMsgId;

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
    // Set by Disconnect; blocks the reconnect loop from restarting until the user connects again
    // (manually, or by accepting an incoming pairing). Cleared whenever a session actually
    // attaches, so a later real drop still reconnects normally.
    private bool _userDisconnected;
    // When the current/most recent session was attached, so the reconnect loop can tell a quick
    // flap (reset nothing — keep escalating) from a session that actually ran for a while (reset
    // backoff — this is a fresh problem). See FlapResetThreshold.
    private DateTimeOffset _meshSessionAttachedAt = DateTimeOffset.MinValue;
    // uuid of the row currently mid-Pair/Connect (manual, UI-driven only), so its State column
    // shows "Connecting". Automatic background reconnects don't touch this.
    private string? _connectingUuid;
    // Dialer side, first-time pairing only: the "Connecting..." dialog for the attempt in flight.
    // EnterPinAsync (called deep inside RunAsync) closes it and asks for the code instead.
    private DarkDialog? _pendingConnectingDialog;
    // Listener side only: the PIN confirm dialog for the pairing attempt currently in flight,
    // kept open (showing "Connecting") after Allow is clicked instead of closing immediately, so
    // the user sees it is still waiting on the dialer. Closed once HandleIncomingAsync's RunAsync
    // call actually finishes.
    private DarkDialog? _pendingConfirmDialog;
    // Listener side only: the incoming PairingSession currently in flight, so a Cancel click on
    // the "Connecting" confirm dialog (after the user already clicked Allow) can abort it by
    // closing the socket — RunAsync's own exception handling turns that into a PairingFailed
    // outcome instead of crashing.
    private PairingSession? _pendingIncomingSession;
    // Dialer side only: the outgoing PairingSession currently in flight, so a Cancel click on
    // the "Connecting..." dialog can abort it by closing the socket, same mechanism as
    // _pendingIncomingSession on the listener side.
    private PairingSession? _pendingDialerSession;

    /// <summary>One row bound by <c>ChatBubbleTemplate</c>: the stored message, whether we sent
    /// it (right-aligned) or the peer did (left-aligned), and whether its popup is open.</summary>
    private sealed class ChatRowVm : INotifyPropertyChanged
    {
        private bool _isHighlighted;

        public ChatRowVm(ChatMessage message, bool isOwn, string peerUuid, bool isHighlighted)
        {
            Message = message;
            IsOwn = isOwn;
            PeerUuid = peerUuid;
            _isHighlighted = isHighlighted;
        }

        public ChatMessage Message { get; }
        public bool IsOwn { get; }
        public string PeerUuid { get; }

        public bool IsHighlighted
        {
            get => _isHighlighted;
            set
            {
                if (_isHighlighted == value) return;
                _isHighlighted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public MainWindow()
    {
        InitializeComponent();
        DeviceListBox.ItemsSource = _deviceRows;
        ChatListBox.ItemsSource = _chatRows;
        _popup = new DarkPopup(PopupLayer);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };

        // Loaded once and cached for the window's lifetime.
        _identity = IdentityStore.LoadOrCreate(Path.Combine(MeshDir, "identity.pfx"));
        _selfUuid = DeviceIdentity.GetOrCreateUuid(Path.Combine(MeshDir, "device_id.txt"));

        // Port 0: the OS picks a free ephemeral port; that actual port is what we advertise.
        _listener = new PairingListener(0);
        _ = ListenLoopAsync();
        StartDiscovery();

        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        RebuildDeviceRows(); // show already-paired devices immediately, even before mDNS finds any
    }

    /// <summary>Not part of the app: diagnostics only. The Android app dropped its status line
    /// too — the device table already shows the connection state — so failures go to the log.</summary>
    private static void SetStatus(string text) => DebugLog.Write($"status: {text}");

    // Navigation ------------------------------------------------------------------------

    /// <summary>Shows a paired device's page — plain navigation, replacing the Devices page.
    /// Only one device page is ever open; see <see cref="OpenDevicesPage"/> to go back.</summary>
    private void OpenDevicePage(string uuid, string name)
    {
        _popup.Dismiss();
        _openPeerUuid = uuid;
        DeviceNameText.Text = name;
        DevicesPage.Visibility = Visibility.Collapsed;
        DevicePage.Visibility = Visibility.Visible;
        ChatInputBox.Clear();
        SelectSection(Section.Chat);
        ReloadChat(uuid);
        RefreshDevicePageState();
    }

    private void OpenDevicesPage()
    {
        // A popup belongs to the page it was opened on; leaving the page closes it.
        _popup.Dismiss();
        _openPeerUuid = null;
        _chatRows.Clear();
        DevicePage.Visibility = Visibility.Collapsed;
        DevicesPage.Visibility = Visibility.Visible;
    }

    private void MeshTitle_Click(object sender, RoutedEventArgs e) => OpenDevicesPage();

    private void BackButton_Click(object sender, RoutedEventArgs e) => OpenDevicesPage();

    private void ChatSectionTab_Click(object sender, MouseButtonEventArgs e) => SelectSection(Section.Chat);

    private void ExplorerSectionTab_Click(object sender, MouseButtonEventArgs e) => SelectSection(Section.Explorer);

    /// <summary>The active section tab reads as a darker patch with brighter text — no underline.</summary>
    private void SelectSection(Section section)
    {
        _popup.Dismiss();
        ChatSection.Visibility = section == Section.Chat ? Visibility.Visible : Visibility.Collapsed;
        ExplorerSection.Visibility = section == Section.Explorer ? Visibility.Visible : Visibility.Collapsed;
        ChatSectionTab.Background = Brush(section == Section.Chat ? "BgScreen" : "BgHeader");
        ExplorerSectionTab.Background = Brush(section == Section.Explorer ? "BgScreen" : "BgHeader");
        ChatSectionLabel.Foreground = Brush(section == Section.Chat ? "TextPrimary" : "TextSecondary");
        ExplorerSectionLabel.Foreground = Brush(section == Section.Explorer ? "TextPrimary" : "TextSecondary");
        if (section == Section.Chat) ScrollChatToEnd();
    }

    /// <summary>Esc closes an open popup, otherwise goes back to the Devices page — the desktop
    /// counterpart of the Android Back button.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DialogLayer.Children.Count > 0) return;
        if (_popup.IsOpen) _popup.Dismiss();
        else if (_openPeerUuid != null) OpenDevicesPage();
        else return;
        e.Handled = true;
    }

    /// <summary>A click anywhere outside the open popup closes it and still goes through, so
    /// clicking another message or device opens that one's popup in the same click. The mouse
    /// "back" side button goes back to the Devices page.</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && _openPeerUuid != null && DialogLayer.Children.Count == 0)
        {
            OpenDevicesPage();
            e.Handled = true;
            return;
        }
        if (_popup.IsOpen && !_popup.IsInside(e.OriginalSource as DependencyObject)) _popup.Dismiss();
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // Devices page ------------------------------------------------------------------------

    /// <summary>Opens a paired device's page. An unpaired device has no chat to show yet — its
    /// popup (right-click) is how it gets paired.</summary>
    private void DeviceRow_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeviceRow row } || !row.IsPaired) return;
        OpenDevicePage(row.Uuid, row.Name);
    }

    /// <summary>Right-click (the Android long-press): this device's actions, with its row
    /// highlighted until the popup closes.</summary>
    private void DeviceRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeviceRow row }) return;
        e.Handled = true;
        _popup.Show(ActionsFor(row), action => HandleDeviceAction(action, row), onDismiss: () =>
        {
            _highlightedDeviceUuid = null;
            RebuildDeviceRows();
        });
        _highlightedDeviceUuid = row.Uuid;
        RebuildDeviceRows();
    }

    /// <summary>Not paired: Connect (which pairs). Paired but not connected: Connect, Unpair.
    /// Connected: Disconnect, Unpair.</summary>
    private static IReadOnlyList<string> ActionsFor(DeviceRow row) =>
        !row.IsPaired ? new[] { "Connect" }
        : row.IsConnectedNow ? new[] { "Disconnect", "Unpair" }
        : new[] { "Connect", "Unpair" };

    private async void HandleDeviceAction(string action, DeviceRow row)
    {
        switch (action)
        {
            case "Connect" when !row.IsPaired:
                await ConnectOrPairAsync(row, isFirstTimePair: true);
                break;
            case "Connect":
                // Only one active connection at a time: drop a different connected device first.
                if (_meshSession != null && _meshPeerUuid != row.Uuid) await DisconnectCurrentSessionAsync();
                await ConnectOrPairAsync(row, isFirstTimePair: false);
                break;
            case "Disconnect":
                await DisconnectCurrentSessionAsync();
                break;
            case "Unpair":
                await UnpairDeviceAsync(row);
                break;
        }
    }

    // Device page --------------------------------------------------------------------------

    private ConnectionStatus ConnectionStatusFor(string uuid)
    {
        var row = _deviceRows.FirstOrDefault(r => r.Uuid == uuid);
        if (row == null || !row.IsPaired) return ConnectionStatus.Unpaired;
        if (row.IsConnectedNow) return ConnectionStatus.Connected;
        return row.IsOnline ? ConnectionStatus.Disconnected : ConnectionStatus.Offline;
    }

    /// <summary>Updates the status stripe and word, and enables Send only while the open device
    /// is the one single peer currently connected. Called from <see cref="RebuildDeviceRows"/>,
    /// which already runs after every event that could change this.</summary>
    private void RefreshDevicePageState()
    {
        if (_openPeerUuid == null) return;
        var (brushKey, label) = ConnectionStatusFor(_openPeerUuid) switch
        {
            ConnectionStatus.Connected => ("ConnConnected", "connected"),
            ConnectionStatus.Disconnected => ("ConnDisconnected", "disconnect"),
            ConnectionStatus.Unpaired => ("ConnUnpaired", "unpaired"),
            _ => ("ConnOffline", "offline")
        };
        ConnectionStatusStripe.Background = Brush(brushKey);
        ConnectionStatusLabel.Foreground = Brush(brushKey);
        ConnectionStatusLabel.Text = label;

        var isActive = _meshPeerUuid == _openPeerUuid && _meshSession != null;
        ChatInputBox.IsEnabled = isActive;
        SendButton.IsEnabled = isActive;
        NotConnectedHint.Visibility = isActive ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Reloads the open device page's messages from <see cref="ChatHistoryStore"/>. A
    /// no-op unless <paramref name="uuid"/> is the device whose page is open.</summary>
    private void ReloadChat(string uuid)
    {
        if (_openPeerUuid != uuid) return;
        _chatRows.Clear();
        foreach (var message in ChatHistoryStore.GetAllForPeer(_chatDbPath, uuid))
        {
            _chatRows.Add(new ChatRowVm(message, message.SenderUuid == _selfUuid, uuid,
                message.MsgId == _highlightedMsgId));
        }
        ScrollChatToEnd();
    }

    private void ScrollChatToEnd()
    {
        if (_chatRows.Count > 0) ChatListBox.ScrollIntoView(_chatRows[^1]);
    }

    /// <summary>A left click on a message's row — on the bubble or beside it — opens its popup.
    /// A click that ended a drag-selection inside the bubble does not: that was text selection.</summary>
    private void ChatRow_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid { DataContext: ChatRowVm row } grid) return;
        if (grid.Children[0] is Border { Child: TextBox { SelectionLength: > 0 } }) return;
        ShowBubbleMenu(row);
    }

    private void ChatRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid { DataContext: ChatRowVm row }) return;
        e.Handled = true;
        ShowBubbleMenu(row);
    }

    private void ShowBubbleMenu(ChatRowVm row)
    {
        var items = row.IsOwn ? new[] { "Copy", "Edit", "Delete" } : new[] { "Copy" };
        _popup.Show(items, action =>
        {
            switch (action)
            {
                case "Copy":
                    try { Clipboard.SetText(row.Message.Text); }
                    catch (COMException ex) { SetStatus($"Clipboard is busy, try again: {ex.Message}"); }
                    break;
                case "Edit":
                    ShowEditDialog(row);
                    break;
                case "Delete":
                    _ = ConfirmAndDeleteAsync(row);
                    break;
            }
        }, onDismiss: () =>
        {
            row.IsHighlighted = false;
            _highlightedMsgId = null;
        });
        row.IsHighlighted = true;
        _highlightedMsgId = row.Message.MsgId;
    }

    private void ShowEditDialog(ChatRowVm row)
    {
        var dialog = new DarkDialog(DialogLayer, "Edit message", "", "Save", "Cancel", inputText: row.Message.Text);
        dialog.PositiveClicked += () =>
        {
            var newText = dialog.Input!.Text;
            dialog.Close();
            if (!string.IsNullOrEmpty(newText) && newText != row.Message.Text)
                EditChatMessage(row.PeerUuid, row.Message.MsgId, newText);
        };
        dialog.NegativeClicked += dialog.Close;
        dialog.Show();
    }

    private async Task ConfirmAndDeleteAsync(ChatRowVm row)
    {
        var confirmed = await ShowConfirmAsync("Delete message", "Delete this message?", "Delete", "Cancel",
            CancellationToken.None);
        if (confirmed) DeleteChatMessage(row.PeerUuid, row.Message.MsgId);
    }

    /// <summary>Attachments aren't implemented yet (no wire protocol for binary transfer) — a
    /// visual stand-in for the "+" menu, same as on Android.</summary>
    private void AttachButton_Click(object sender, RoutedEventArgs e) =>
        _popup.Show(new[] { "Image", "File" }, _ => ShowToast("Not supported yet"));

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendFromInput();

    /// <summary>Enter sends; Shift+Enter starts a new line.</summary>
    private void ChatInputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        SendFromInput();
    }

    private void ChatInputBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ChatInputHint.Visibility = ChatInputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SendFromInput()
    {
        var text = ChatInputBox.Text;
        if (_openPeerUuid == null || string.IsNullOrEmpty(text) || !SendButton.IsEnabled) return;
        ChatInputBox.Clear();
        SendChatMessage(_openPeerUuid, text);
    }

    // Discovery -------------------------------------------------------------------------------

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
        _discovered[device.Uuid] = device; // same peer, possibly new IP/port after a restart
        RebuildDeviceRows();
    }

    private void OnDeviceLost(string uuid)
    {
        if (_discovered.Remove(uuid)) RebuildDeviceRows();
    }

    /// <summary>
    /// Recomputes the unified device list — every paired device (online or not) plus every
    /// currently-discovered-but-unpaired device — from <see cref="PairedDeviceStore"/> and the
    /// live <see cref="_discovered"/> map. Call after anything that could change a row: mDNS
    /// found/lost, pairing/unpairing, the mesh session's connected peer/state, or a row's popup
    /// opening or closing.
    /// </summary>
    private void RebuildDeviceRows()
    {
        var paired = PairedDeviceStore.Load(_pairedDevicesPath).ToDictionary(d => d.Uuid);
        var uuids = new HashSet<string>(paired.Keys);
        uuids.UnionWith(_discovered.Keys);
        uuids.Remove(_selfUuid);

        var rows = uuids.Select(uuid =>
        {
            var isPaired = paired.TryGetValue(uuid, out var pairedDevice);
            var isOnline = _discovered.TryGetValue(uuid, out var discovered);
            var name = isOnline ? discovered!.Name : pairedDevice?.Name ?? uuid;
            var isConnectedNow = _meshPeerUuid == uuid && _meshSession != null &&
                _meshSession.State == MeshSessionState.Connected;
            var isConnecting = _connectingUuid == uuid;
            var ip = isOnline ? discovered!.IpAddress : pairedDevice?.LastIp ?? "";
            var port = isOnline ? discovered!.Port : 0;
            return new DeviceRow(uuid, name, isOnline, isPaired, isConnectedNow, isConnecting, ip, port,
                IsHighlighted: _highlightedDeviceUuid == uuid);
        })
        .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

        _deviceRows.Clear();
        foreach (var row in rows) _deviceRows.Add(row);
        RefreshDevicePageState();
    }

    // Pairing -------------------------------------------------------------------------------

    private async Task ListenLoopAsync()
    {
        // Runs for the window's lifetime: one incoming connection at a time, then back to
        // listening. Continuations resume on the UI thread (captured dispatcher context).
        await _listener.RunAcceptLoopAsync(_identity, HandleIncomingAsync,
            ex =>
            {
                DebugLog.Write($"ListenLoopAsync: accept/handler error {ex.GetType().Name}: {ex.Message}");
                Dispatcher.InvokeAsync(() => SetStatus($"Incoming connection failed: {ex.Message}"));
            },
            _shutdown.Token);
    }

    private async Task HandleIncomingAsync(PairingSession session)
    {
        DebugLog.Write($"HandleIncomingAsync: enter from {session.PeerIp}, pairingInFlight={_pairingInFlight}");
        if (_pairingInFlight)
        {
            DebugLog.Write("HandleIncomingAsync: busy, dropping incoming session");
            return; // busy: the loop closes this session; peer sees a failure
        }
        _pairingInFlight = true;
        _pendingIncomingSession = session;
        try
        {
            var outcome = await session.RunAsync(_selfUuid, _selfName, _pairedDevicesPath, this);
            DebugLog.Write($"HandleIncomingAsync: RunAsync result={outcome.Result} detail={outcome.Detail}");
            var peerName = outcome.PeerName ?? session.PeerIp;
            SetStatus(OutcomeText(outcome, peerName));
            if (outcome.Result is PairingResult.Paired or PairingResult.Reconnected)
            {
                // The socket we accepted came from the peer's ephemeral port, so its listening
                // endpoint (needed to reconnect later) comes from mDNS when we have it.
                var endpoint = ResolvePeerEndpoint(outcome.PeerUuid!, (session.PeerIp, 0));
                await AttachMeshSessionAsync(session, outcome.PeerUuid!, peerName, endpoint);
                // Only a first-time pairing opens the device page — a silent background/manual
                // reconnect to an already-paired device must not change the screen on its own.
                if (outcome.Result == PairingResult.Paired) OpenDevicePage(outcome.PeerUuid!, peerName);
            }
            RebuildDeviceRows();
        }
        finally
        {
            _pairingInFlight = false;
            _pendingIncomingSession = null;
            _pendingConfirmDialog?.Close();
            _pendingConfirmDialog = null;
        }
    }

    private async Task ConnectOrPairAsync(DeviceRow target, bool isFirstTimePair)
    {
        if (_pairingInFlight) return;
        _pairingInFlight = true;
        _connectingUuid = target.Uuid;
        RebuildDeviceRows();
        // First-time pairing only, as on Android: nothing to show the dialer until the listener
        // approves and EnterPinAsync swaps this for the code prompt. A manual reconnect to an
        // already-trusted device shows nothing here — its row's State reads "Connecting".
        if (isFirstTimePair)
        {
            _pendingConnectingDialog = new DarkDialog(DialogLayer, "Connecting", $"Connecting to {target.Name}...",
                positive: null, negative: "Cancel");
            _pendingConnectingDialog.NegativeClicked += () =>
            {
                _pendingDialerSession?.Dispose();
                _pendingConnectingDialog?.Close();
            };
            _pendingConnectingDialog.Show();
        }
        try
        {
            PairingSession session;
            try
            {
                session = await PairingSession.ConnectAsync(_identity, target.IpAddress, target.Port);
            }
            catch (Exception ex)
            {
                SetStatus($"Connection failed: {ex.Message}");
                return;
            }
            _pendingDialerSession = session;

            // On success the transport is detached into a MeshSession, which makes this
            // using's Dispose a no-op; on every other outcome it closes the socket as before.
            using (session)
            {
                var outcome = await session.RunAsync(_selfUuid, _selfName, _pairedDevicesPath, this,
                    isManualConnect: true);
                var peerName = outcome.PeerName ?? target.Name;
                SetStatus(OutcomeText(outcome, peerName));
                if (outcome.Result is PairingResult.Paired or PairingResult.Reconnected)
                {
                    await AttachMeshSessionAsync(session, outcome.PeerUuid!, peerName,
                        (target.IpAddress, target.Port));
                    if (outcome.Result == PairingResult.Paired) OpenDevicePage(outcome.PeerUuid!, peerName);
                }
            }
        }
        finally
        {
            // Reset the flag before anything that could throw (e.g. closing a dialog), so a
            // problem here can never leave _pairingInFlight stuck true and silently dropping
            // every later incoming/outgoing attempt.
            _pairingInFlight = false;
            _connectingUuid = null;
            _pendingDialerSession = null;
            _pendingConnectingDialog?.Close();
            _pendingConnectingDialog = null;
            RebuildDeviceRows();
        }
    }

    /// <summary>
    /// Closes the current mesh session on purpose and stops the reconnect loop. No further
    /// background reconnect happens for this peer until the user reconnects (Connect, or an
    /// incoming trusted-reconnect from the peer).
    /// </summary>
    private async Task DisconnectCurrentSessionAsync()
    {
        var session = _meshSession;
        if (session == null) return;
        _userDisconnected = true;
        _meshSession = null;
        _meshReconnectTarget = null;
        // Best-effort: tells the peer this is intentional, so its own reconnect loop stops too
        // instead of dialing back in later and silently undoing the disconnect. If this never
        // arrives (peer already gone), the peer's own idle timeout notices the drop regardless.
        try { await session.SendAsync(new GoodbyeMessage()).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* best effort */ }
        session.Dispose(); // cancels its loops without firing StateChanged (no event to react to)
        RebuildDeviceRows();
    }

    /// <summary>The peer disconnected on purpose (sent <see cref="GoodbyeMessage"/>): stop
    /// trying to reconnect to them until the user reconnects, same as our own Disconnect.</summary>
    private void OnPeerGoodbye(MeshSession session)
    {
        if (!ReferenceEquals(session, _meshSession)) return; // a replaced/disposed session
        _userDisconnected = true;
        _meshSession = null;
        _meshReconnectTarget = null;
        SetStatus($"Disconnected by {_meshPeerName}");
        session.Dispose();
        RebuildDeviceRows();
    }

    /// <summary>
    /// "Connection failed" = TCP/TLS/transport problem (incl. the 10 s timeout);
    /// "Pairing failed" = connected, but the PIN was rejected/declined/timed out.
    /// </summary>
    private static string OutcomeText(PairingOutcome outcome, string peerName) => outcome.Result switch
    {
        PairingResult.Paired => $"Paired with {peerName}.",
        PairingResult.Reconnected => $"Connected to {peerName} (trusted).",
        PairingResult.ConnectionFailed => $"Connection failed: {outcome.Detail}",
        _ => $"Pairing failed: {outcome.Detail}"
    };

    /// <summary>Removes the local trust record for the device, after confirmation. Local-only
    /// (see <see cref="PairedDeviceStore.Remove"/>): the peer's own trust of us is untouched.
    /// Also drops the live session if this happened to be the currently-connected device.</summary>
    private async Task UnpairDeviceAsync(DeviceRow target)
    {
        var confirmed = await ShowConfirmAsync("Unpair", $"Remove pairing with {target.Name}?", "Unpair", "Cancel",
            CancellationToken.None);
        if (!confirmed) return;

        if (_meshPeerUuid == target.Uuid) await DisconnectCurrentSessionAsync();
        PairedDeviceStore.Remove(_pairedDevicesPath, target.Uuid);
        // Unpairing loses the chat too, same as the trust record — the peer's own copy is
        // untouched (unpair is local-only) and restores ours if we re-pair with them later (see
        // AttachMeshSessionAsync's history sync).
        ChatHistoryStore.DeleteForPeer(_chatDbPath, target.Uuid);
        if (_openPeerUuid == target.Uuid) OpenDevicesPage();
        RebuildDeviceRows();
    }

    // Mesh session lifecycle -------------------------------------------------------------

    /// <summary>
    /// Takes over <paramref name="session"/>'s authenticated transport as the new
    /// <see cref="MeshSession"/>, replacing (and disposing) any previous one. Messages the old
    /// session queued while it was disconnected are re-sent over the new one when it is the
    /// same peer, so nothing sent during an outage is lost.
    /// </summary>
    private async Task AttachMeshSessionAsync(PairingSession session, string peerUuid, string peerName,
        (string Ip, int Port) peerEndpoint)
    {
        var (transportClient, transportStream) = session.DetachTransport();
        var previous = _meshSession;
        var previousPeerUuid = _meshPeerUuid;

        // Handlers go into FromTransport itself so they are attached before its read loop starts:
        // a message the peer sends right after reconnecting can never hit an unsubscribed event.
        // The state handler only reads `current` once marshalled onto the UI thread, which is after
        // this (UI-thread) method has assigned it below.
        MeshSession? current = null;
        current = MeshSession.FromTransport(transportClient, transportStream, peerUuid,
            peerEndpoint.Ip, peerEndpoint.Port,
            onMessageReceived: message => OnMeshMessageReceived(message, peerUuid, peerName, current!),
            onStateChanged: state => Dispatcher.InvokeAsync(() => OnMeshSessionStateChanged(current!, state)));
        _meshSession = current;
        _meshPeerUuid = peerUuid;
        _meshPeerName = peerName;
        _meshPeerEndpoint = peerEndpoint;
        _meshTrustLostSession = null; // only ever refers to a session this one has just replaced
        _userDisconnected = false; // a live session now exists; a later drop should reconnect again
        _meshSessionAttachedAt = DateTimeOffset.UtcNow;
        DebugLog.Write($"AttachMeshSessionAsync: attached session for {peerName}");
        RebuildDeviceRows();

        // Each side blasts its own local history for this conversation, independently; the
        // other merges in anything it's missing (see OnMeshMessageReceived's ChatHistorySyncMessage
        // case). This is what restores a chat after one side unpaired and re-paired, losing its
        // own copy — the side that kept it replays everything back.
        var syncEntries = ChatHistoryStore.GetAllForPeer(_chatDbPath, peerUuid)
            .Select(m => new ChatSyncEntry(m.MsgId, m.SenderUuid, m.Timestamp, m.Text))
            .ToList();
        _ = current.SendAsync(new ChatHistorySyncMessage(syncEntries));

        if (previous == null) return;
        // Dispose first so the old session cannot accept anything more, then drain (which waits
        // for any in-flight send on it to settle). Only carry queued messages over to the same
        // peer: a message written to one device must not leak to a different one.
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
        DebugLog.Write($"OnMeshSessionStateChanged: state={state} isCurrent={ReferenceEquals(session, _meshSession)}");
        if (!ReferenceEquals(session, _meshSession)) return; // a replaced/disposed session
        if (ReferenceEquals(session, _meshTrustLostSession)) return; // keep "Re-pairing required"
        RebuildDeviceRows();
        if (state == MeshSessionState.Disconnected) EnsureMeshReconnect(session);
    }

    /// <summary>
    /// Makes sure a reconnect loop is working on <paramref name="session"/>. At most one loop runs;
    /// if one is already running (possibly still asleep in backoff for an older, since-replaced
    /// session) it is retargeted to this session and picks it up when it wakes.
    /// </summary>
    private void EnsureMeshReconnect(MeshSession session)
    {
        if (_userDisconnected) return; // the user closed this on purpose; wait for them to reconnect
        var loopRunning = _meshReconnectTarget != null;
        _meshReconnectTarget = session;
        if (!loopRunning) _ = RunMeshReconnectLoopAsync();
    }

    /// <summary>A current session that is down and still worth reconnecting.</summary>
    private bool NeedsMeshReconnect(MeshSession? session) =>
        session != null && session.State != MeshSessionState.Connected &&
        !ReferenceEquals(session, _meshTrustLostSession) && !_userDisconnected;

    /// <summary>
    /// UI-level reconnect orchestration. <see cref="MeshSession"/> only detects that the transport
    /// died; re-establishing a usable channel needs a fresh TLS connection plus the trusted-reconnect
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
                    // Only treat this as a fresh problem (reset backoff) if the session we're
                    // now chasing had actually been up for a while. A session that flapped —
                    // connected and died again within FlapResetThreshold — is the same ongoing
                    // episode; keep escalating, or a flaky link causes a 1 s-interval reconnect
                    // storm that never slows down.
                    if (DateTimeOffset.UtcNow - _meshSessionAttachedAt > FlapResetThreshold) attempt = 0;
                }
                if (!ReferenceEquals(_meshSession, target))
                {
                    // Replaced while we were asleep or attempting. Adopt the replacement if it is
                    // down too; otherwise there is nothing left to do.
                    if (!NeedsMeshReconnect(_meshSession)) return;
                    target = _meshReconnectTarget = _meshSession;
                    if (DateTimeOffset.UtcNow - _meshSessionAttachedAt > FlapResetThreshold) attempt = 0;
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
                try
                {
                    result = await TryTrustedReconnectAsync(peerUuid, peerName, ip, port);
                }
                finally
                {
                    _pairingInFlight = false;
                }

                if (result == ReconnectAttemptResult.TrustLost && ReferenceEquals(_meshSession, target))
                {
                    // The peer answered as itself but the trust check failed (certificate changed,
                    // or either side removed the pairing). No amount of retrying fixes that.
                    _meshTrustLostSession = target;
                    SetStatus($"{peerName} is no longer trusted (certificate changed or pairing removed).");
                    RebuildDeviceRows();
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
    /// a peer that is no longer trusted needs an explicit re-pair via Connect.
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
                // Automatic background reconnect: never manual, so the listener never gets the
                // "allow this reconnect?" prompt added for Connect. Must still be sent — RunAsync
                // on the other side expects this exact step, in this exact order.
                await session.ExchangeManualIntentAsync(isManualConnect: false);
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
            try
            {
                var record = PairedDeviceStore.Find(_pairedDevicesPath, peerUuid);
                if (record != null)
                {
                    record.LastIp = session.PeerIp;
                    record.Name = session.PeerName ?? record.Name;
                    PairedDeviceStore.Upsert(_pairedDevicesPath, record);
                }
            }
            catch (Exception ex)
            {
                // Bookkeeping only; the channel is authenticated and usable regardless. An IO
                // failure here must not strand the reconnect loop for this session.
                SetStatus($"Could not update paired-device record: {ex.Message}");
            }

            var name = session.PeerName ?? peerName;
            await AttachMeshSessionAsync(session, peerUuid, name, (ip, port));
            SetStatus($"Reconnected to {name} (trusted).");
            return ReconnectAttemptResult.Reconnected;
        }
    }

    /// <summary>The peer's listening endpoint as currently advertised over mDNS, if seen.</summary>
    private (string Ip, int Port) ResolvePeerEndpoint(string peerUuid, (string Ip, int Port) fallback) =>
        _discovered.TryGetValue(peerUuid, out var discovered) ? (discovered.IpAddress, discovered.Port) : fallback;

    // Chat ---------------------------------------------------------------------------------

    private void SendChatMessage(string peerUuid, string text)
    {
        if (text.Length > MaxChatTextLength)
        {
            ShowToast("Message too long");
            return;
        }
        var id = Guid.NewGuid().ToString();
        var timestamp = DateTimeOffset.UtcNow;
        ChatMessage? entry;
        try
        {
            entry = ChatHistoryStore.Insert(_chatDbPath, id, peerUuid, _selfUuid, _selfName, text, timestamp);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"SendChatMessage: Insert failed {ex.GetType().Name}: {ex.Message}");
            SetStatus($"Could not save chat message: {ex.Message}");
            return;
        }
        // Reload rather than append, so the list reflects the DB's own per-peer cap eviction
        // exactly (the 50th-oldest message disappearing from the DB also disappears here).
        if (entry != null) ReloadChat(peerUuid);
        if (_meshPeerUuid != peerUuid) return; // Send is disabled for this case already
        var sendTask = _meshSession?.SendAsync(new ChatSendMessage(id, timestamp, text));
        if (sendTask != null)
        {
            _ = sendTask.ContinueWith(t =>
                DebugLog.Write(t.IsFaulted
                    ? $"SendChatMessage: SendAsync faulted: {t.Exception?.InnerException?.Message}"
                    : "SendChatMessage: SendAsync completed"));
        }
    }

    /// <summary>Edits one of our own already-sent messages. Always applied locally; also sent
    /// live to the peer if they're the one currently connected — an edit made while that peer is
    /// offline is never retried/queued.</summary>
    private void EditChatMessage(string peerUuid, string msgId, string newText)
    {
        if (string.IsNullOrEmpty(newText) || newText.Length > MaxChatTextLength) return;
        try
        {
            ChatHistoryStore.UpdateText(_chatDbPath, peerUuid, msgId, newText);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not edit chat message: {ex.Message}");
            return;
        }
        ReloadChat(peerUuid);
        if (_meshPeerUuid == peerUuid) _ = _meshSession?.SendAsync(new ChatEditMessage(msgId, newText));
    }

    /// <summary>Deletes one of our own already-sent messages. Same best-effort delivery caveat as
    /// <see cref="EditChatMessage"/>.</summary>
    private void DeleteChatMessage(string peerUuid, string msgId)
    {
        try
        {
            ChatHistoryStore.DeleteMessage(_chatDbPath, peerUuid, msgId);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not delete chat message: {ex.Message}");
            return;
        }
        ReloadChat(peerUuid);
        if (_meshPeerUuid == peerUuid) _ = _meshSession?.SendAsync(new ChatDeleteMessage(msgId));
    }

    private void OnMeshMessageReceived(MeshMessage message, string peerUuid, string peerName, MeshSession session)
    {
        // Raised on MeshSession's read loop (background thread).
        DebugLog.Write($"OnMeshMessageReceived: {message.GetType().Name} sessionNull={session == null}");
        switch (message)
        {
            case GoodbyeMessage:
                Dispatcher.InvokeAsync(() => OnPeerGoodbye(session!));
                return;
            case ChatHistorySyncMessage sync:
                OnChatHistorySyncReceived(sync, peerUuid, peerName);
                return;
            case ChatEditMessage edit:
                try { ChatHistoryStore.UpdateText(_chatDbPath, peerUuid, edit.Id, edit.Text); }
                catch (Exception ex)
                {
                    Dispatcher.InvokeAsync(() => SetStatus($"Could not save chat edit: {ex.Message}"));
                    return;
                }
                Dispatcher.InvokeAsync(() => ReloadChat(peerUuid));
                return;
            case ChatDeleteMessage delete:
                try { ChatHistoryStore.DeleteMessage(_chatDbPath, peerUuid, delete.Id); }
                catch (Exception ex)
                {
                    Dispatcher.InvokeAsync(() => SetStatus($"Could not save chat delete: {ex.Message}"));
                    return;
                }
                Dispatcher.InvokeAsync(() => ReloadChat(peerUuid));
                return;
        }
        if (message is not ChatSendMessage send) return;
        if (send.Text.Length > MaxChatTextLength) return;
        ChatMessage? entry;
        try
        {
            // The sender is always "peer" from here — a ChatSendMessage only ever arrives from
            // the session's peer, never replayed from ourselves.
            entry = ChatHistoryStore.Insert(_chatDbPath, send.Id, peerUuid, peerUuid, peerName, send.Text,
                send.Timestamp);
        }
        catch (Exception ex)
        {
            Dispatcher.InvokeAsync(() => SetStatus($"Could not save chat message: {ex.Message}"));
            return;
        }
        if (entry == null) return; // already had this id (shouldn't happen for a live send, but harmless)
        Dispatcher.InvokeAsync(() => ReloadChat(peerUuid));
    }

    /// <summary>Merges the peer's own history for this conversation into ours, deduping by
    /// <see cref="ChatMessage.MsgId"/>. Restores a chat after we unpaired and re-paired with the
    /// same device (see UnpairDeviceAsync).</summary>
    private void OnChatHistorySyncReceived(ChatHistorySyncMessage sync, string peerUuid, string peerName)
    {
        var anyNew = false;
        foreach (var remoteEntry in sync.Entries)
        {
            var label = remoteEntry.SenderUuid == _selfUuid ? _selfName : peerName;
            try
            {
                if (ChatHistoryStore.Insert(_chatDbPath, remoteEntry.Id, peerUuid, remoteEntry.SenderUuid, label,
                        remoteEntry.Text, remoteEntry.Timestamp) != null)
                {
                    anyNew = true;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write($"OnChatHistorySyncReceived: Insert failed {ex.GetType().Name}: {ex.Message}");
            }
        }
        if (!anyNew) return;
        Dispatcher.InvokeAsync(() => ReloadChat(peerUuid));
    }

    // IPairingPrompt -------------------------------------------------------------------

    public Task<bool> ConfirmCertificateChangedAsync(string peerName, CancellationToken ct) =>
        ShowConfirmAsync("Certificate changed",
            $"Certificate changed for {peerName} — re-pair?\n\nThis device no longer presents the " +
            "certificate you paired with. Only continue if you know why (e.g. the app was reinstalled).",
            "Re-pair", "Cancel", ct);

    public Task<bool> ConfirmPinAsync(string peerName, string pin, CancellationToken ct) =>
        ShowConfirmAsync("Pairing request", $"{peerName} wants to connect. Allow?\n\n{pin}", "Allow", "Deny", ct,
            keepOpenOnYes: true, onCancelWhileConnecting: () => _pendingIncomingSession?.Dispose());

    /// <summary>Dialer side: the listener has just approved, so the "Connecting..." placeholder
    /// gives way to the code prompt. Resolves null on Cancel or when <paramref name="ct"/> fires.</summary>
    public Task<string?> EnterPinAsync(string peerName, CancellationToken ct)
    {
        _pendingConnectingDialog?.Close();
        _pendingConnectingDialog = null;
        var answer = new TaskCompletionSource<string?>();
        var dialog = new DarkDialog(DialogLayer, "Enter code", $"Enter the code shown on {peerName}:",
            "Submit", "Cancel", inputText: "");
        dialog.PositiveClicked += () =>
        {
            answer.TrySetResult(dialog.Input!.Text.Trim());
            dialog.Close();
        };
        dialog.NegativeClicked += dialog.Close;
        dialog.Closed += () => answer.TrySetResult(null); // no-op if Submit already answered
        var registration = ct.Register(() => Dispatcher.InvokeAsync(dialog.Close));
        _ = answer.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        dialog.Show();
        return answer.Task;
    }

    /// <summary>Listener side, already-trusted peer, manual Connect only: lightweight "X wants to
    /// connect, allow?" with no PIN — trust is already established.</summary>
    public Task<bool> ConfirmReconnectAsync(string peerName, CancellationToken ct) =>
        ShowConfirmAsync("Connect?", $"{peerName} wants to connect. Allow?", "Allow", "Deny", ct);

    /// <summary>
    /// Shows a yes/no dialog and resolves once an answer is known (closing it any other way, or
    /// <paramref name="ct"/> firing, counts as no).
    /// <paramref name="keepOpenOnYes"/> (PIN confirm only): "yes" does not close the dialog — its
    /// positive button turns into a disabled "Connecting" and the negative one into "Cancel",
    /// and the dialog is tracked in <see cref="_pendingConfirmDialog"/> for HandleIncomingAsync's
    /// finally to close once the pairing attempt actually finishes.
    /// <paramref name="onCancelWhileConnecting"/> fires if the user clicks that Cancel during the
    /// wait — the answer stays "yes", this just lets the caller abort the attempt (close the socket).
    /// </summary>
    private Task<bool> ShowConfirmAsync(string title, string message, string yes, string no, CancellationToken ct,
        bool keepOpenOnYes = false, Action? onCancelWhileConnecting = null)
    {
        var answer = new TaskCompletionSource<bool>();
        var dialog = new DarkDialog(DialogLayer, title, message, yes, no);
        var connecting = false;
        dialog.PositiveClicked += () =>
        {
            if (keepOpenOnYes)
            {
                connecting = true;
                dialog.SetPositive("Connecting", enabled: false);
                dialog.SetNegative("Cancel");
                _pendingConfirmDialog = dialog;
            }
            else
            {
                dialog.Close();
            }
            answer.TrySetResult(true);
        };
        dialog.NegativeClicked += () =>
        {
            if (connecting) onCancelWhileConnecting?.Invoke();
            answer.TrySetResult(false); // no-op once "yes" was already given
            dialog.Close();
        };
        dialog.Closed += () => answer.TrySetResult(false);
        var registration = ct.Register(() => Dispatcher.InvokeAsync(() =>
        {
            if (!connecting) dialog.Close();
        }));
        _ = answer.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        dialog.Show();
        return answer.Task;
    }

    // Window -------------------------------------------------------------------------------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark title bar in the header color, so the window frame matches the app.
    /// Windows 10 honors only the dark-mode flag, Windows 11 also the caption color; failures are
    /// ignored — the frame just stays light.</summary>
    private void ApplyDarkTitleBar()
    {
        const int useImmersiveDarkMode = 20;
        const int captionColor = 35;
        var hwnd = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        DwmSetWindowAttribute(hwnd, useImmersiveDarkMode, ref enabled, sizeof(int));
        var header = ((SolidColorBrush)Brush("BgHeader")).Color;
        var colorRef = header.R | (header.G << 8) | (header.B << 16);
        DwmSetWindowAttribute(hwnd, captionColor, ref colorRef, sizeof(int));
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _shutdown.Cancel();
        _meshSession?.Dispose();
        _listener.Dispose();
        _discovery.Dispose();
    }
}
