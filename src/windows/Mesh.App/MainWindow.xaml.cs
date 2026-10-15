using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mesh.Core;

namespace Mesh.App;

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

    private readonly MeshDiscovery _discovery = new();
    // Live mDNS presence, keyed by uuid. Source of truth for "is this device online right now";
    // merged with PairedDeviceStore (on disk) into _deviceRows, the unified list the UI binds to.
    private readonly Dictionary<string, DiscoveredDevice> _discovered = new();
    private readonly ObservableCollection<DeviceRow> _deviceRows = new();
    // One tab per device the user has opened (see OpenOrFocusDeviceTab) — independent of which
    // single peer is actually connected right now (_meshPeerUuid/_meshSession). Each tab shows
    // that peer's persisted chat regardless of connection state; its Send box is only enabled
    // while that peer is the one currently connected (see RefreshTabConnectionState).
    private readonly Dictionary<string, DeviceTabUi> _openDeviceTabs = new();
    private readonly X509Certificate2 _identity;
    private readonly string _selfUuid;
    private readonly string _selfName = Environment.MachineName;
    private readonly PairingListener _listener;
    private readonly CancellationTokenSource _shutdown = new();

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
    // Set by the Disconnect button; blocks the reconnect loop from restarting until the user
    // connects again (manually, or by accepting an incoming pairing). Cleared whenever a session
    // actually attaches, so a later real drop still reconnects normally.
    private bool _userDisconnected;
    // When the current/most recent session was attached, so the reconnect loop can tell a quick
    // flap (reset nothing — keep escalating) from a session that actually ran for a while (reset
    // backoff — this is a fresh problem). See FlapResetThreshold.
    private DateTimeOffset _meshSessionAttachedAt = DateTimeOffset.MinValue;
    // uuid of the row currently mid-Pair/Connect (manual, UI-driven only), so its button shows
    // "...". Automatic background reconnects don't touch this — they have no row-level feedback.
    private string? _connectingUuid;
    // Dialer-side-only: the "Connecting.../enter the code" window for the pairing attempt
    // currently in flight, so EnterPinAsync (called deep inside RunAsync) can reach it.
    private PinEntryWindow? _pendingPinEntryWindow;
    // Listener-side-only: the PIN confirm window for the pairing attempt currently in flight,
    // kept open (showing "Connecting...") after Allow is clicked instead of closing immediately,
    // so the user sees it is still waiting on the dialer. Closed once HandleIncomingAsync's
    // RunAsync call actually finishes.
    private PinConfirmWindow? _pendingConfirmWindow;
    // Listener side only: the incoming PairingSession currently in flight, so a Cancel click on
    // the "Connecting..." confirm window (after the user already clicked Allow) can abort it by
    // closing the socket — RunAsync's own exception handling turns that into a PairingFailed
    // outcome instead of crashing.
    private PairingSession? _pendingIncomingSession;
    // Dialer side only: the outgoing PairingSession currently in flight, so a Cancel click on
    // the "Connecting..." window (before the user has even been asked to type a code) can abort
    // it by closing the socket, same mechanism as _pendingIncomingSession on the listener side.
    private PairingSession? _pendingDialerSession;

    /// <summary>One row bound by <c>ChatBubbleTemplate</c>: the stored message plus whether it
    /// was sent by us (right-aligned/blue) or the peer (left-aligned/gray), and which
    /// conversation it belongs to (so a click handler on the shared template knows where to send
    /// an edit/delete).</summary>
    private sealed record ChatRowVm(ChatMessage Message, bool IsOwn, string PeerUuid);

    /// <summary>The live UI pieces of one open device tab, so later events (a received message,
    /// a connection-state change) can be applied to the right tab without rebuilding it.</summary>
    private sealed class DeviceTabUi
    {
        public required string Uuid { get; init; }
        public required TabItem TabItem { get; init; }
        public required ObservableCollection<ChatRowVm> Messages { get; init; }
        public required ListBox MessageListBox { get; init; }
        public required TextBox InputBox { get; init; }
        public required Button SendButton { get; init; }
        public required TextBlock HintText { get; init; }
    }

    public MainWindow()
    {
        InitializeComponent();
        DeviceListBox.ItemsSource = _deviceRows;

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
        RebuildDeviceRows(); // show already-paired devices immediately, even before mDNS finds any
    }

    // Device tabs ----------------------------------------------------------------------------

    /// <summary>Opens a browser-style tab for this device (chat history, regardless of whether
    /// it's the currently-connected peer) or, if already open, just selects it. Closing a tab
    /// (see <see cref="CloseDeviceTab"/>) never unpairs or disconnects — it only hides the UI.</summary>
    private void OpenOrFocusDeviceTab(string uuid, string name)
    {
        if (_openDeviceTabs.TryGetValue(uuid, out var existing))
        {
            MainTabControl.SelectedItem = existing.TabItem;
            return;
        }
        var tab = BuildDeviceTab(uuid, name);
        _openDeviceTabs[uuid] = tab;
        MainTabControl.Items.Add(tab.TabItem);
        MainTabControl.SelectedItem = tab.TabItem;
        ReloadDeviceTab(uuid);
        RefreshTabConnectionState(tab);
    }

    private void CloseDeviceTab(string uuid)
    {
        if (!_openDeviceTabs.Remove(uuid, out var tab)) return;
        MainTabControl.Items.Remove(tab.TabItem);
    }

    /// <summary>Reloads one open tab's messages from <see cref="ChatHistoryStore"/>. A no-op if
    /// that device's tab isn't currently open — nothing to refresh.</summary>
    private void ReloadDeviceTab(string uuid)
    {
        if (!_openDeviceTabs.TryGetValue(uuid, out var tab)) return;
        tab.Messages.Clear();
        foreach (var message in ChatHistoryStore.GetAllForPeer(_chatDbPath, uuid))
            tab.Messages.Add(new ChatRowVm(message, message.SenderUuid == _selfUuid, uuid));
    }

    /// <summary>Enables a tab's Send box only while its device is the one single peer currently
    /// connected (see the project's multi-tab design: tabs are just views, only one connection is
    /// ever live). Called from <see cref="RebuildDeviceRows"/>, which already runs after every
    /// event that could change this.</summary>
    private void RefreshTabConnectionState(DeviceTabUi tab)
    {
        var isActive = _meshPeerUuid == tab.Uuid && _meshSession != null;
        tab.InputBox.IsEnabled = isActive;
        tab.SendButton.IsEnabled = isActive;
        tab.HintText.Visibility = isActive ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshAllTabsConnectionState()
    {
        foreach (var tab in _openDeviceTabs.Values) RefreshTabConnectionState(tab);
    }

    /// <summary>Builds one device tab's header ("name" + browser-style close ✕) and content (its
    /// own chat ListBox bound to <see cref="ChatBubbleTemplate"/> plus an input row), all in code
    /// since tabs are created dynamically. See MainWindow.xaml's ChatBubbleTemplate/Style
    /// resources for the bubble look.</summary>
    private DeviceTabUi BuildDeviceTab(string uuid, string name)
    {
        var messages = new ObservableCollection<ChatRowVm>();

        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        headerPanel.Children.Add(new TextBlock
        {
            Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        });
        var closeButton = new Button
        {
            Content = "✕", Padding = new Thickness(4, 0, 4, 0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Brushes.Gray
        };
        closeButton.Click += (_, _) => CloseDeviceTab(uuid);
        headerPanel.Children.Add(closeButton);

        var listBox = new ListBox
        {
            ItemTemplate = (DataTemplate)FindResource("ChatBubbleTemplate"),
            ItemsSource = messages,
            BorderThickness = new Thickness(0),
            // Without this, each ListBoxItem only takes its content's natural width, so the
            // bubble's own HorizontalAlignment (left/right, see ChatBubbleBorderStyle) has no
            // room to move within and every bubble renders stuck on one side.
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        messages.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add && messages.Count > 0)
                listBox.ScrollIntoView(messages[^1]);
        };

        var hintText = new TextBlock
        {
            Text = "Not connected — press Connect on the Devices tab to send.",
            Foreground = Brushes.Gray, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        var inputBox = new TextBox { VerticalAlignment = VerticalAlignment.Center };
        var sendButton = new Button { Content = "Send", Width = 70, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        void SendClick()
        {
            var text = inputBox.Text;
            if (string.IsNullOrEmpty(text)) return;
            inputBox.Clear();
            SendChatMessage(uuid, text);
        }
        sendButton.Click += (_, _) => SendClick();
        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            SendClick();
            e.Handled = true;
        };

        var inputDock = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(sendButton, Dock.Right);
        inputDock.Children.Add(sendButton);
        inputDock.Children.Add(inputBox);

        // Second-row section strip (see BuildSectionStrip): currently just "Chat", always
        // selected since it's the only section. Not closable — it's a part of this device tab,
        // not a tab of its own. Future sections (images, files) would join it here.
        var sectionStrip = BuildSectionStrip(new[] { "Chat" }, "Chat");

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(sectionStrip, 0);
        Grid.SetRow(listBox, 1);
        Grid.SetRow(hintText, 2);
        Grid.SetRow(inputDock, 3);
        grid.Children.Add(sectionStrip);
        grid.Children.Add(listBox);
        grid.Children.Add(hintText);
        grid.Children.Add(inputDock);

        var tabItem = new TabItem { Header = headerPanel, Content = grid };

        return new DeviceTabUi
        {
            Uuid = uuid, TabItem = tabItem, Messages = messages, MessageListBox = listBox,
            InputBox = inputBox, SendButton = sendButton, HintText = hintText
        };
    }

    /// <summary>Opens/focuses a paired device's tab when its row (not one of its buttons) is
    /// clicked. Mirrors the "click a device in Devices to find it again" part of the tab spec.</summary>
    private void DeviceRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return; // a button's own click
        if (sender is not Grid { DataContext: DeviceRow row } || !row.IsPaired) return;
        OpenOrFocusDeviceTab(row.Uuid, row.Name);
    }

    /// <summary>Builds a device tab's second-row section strip (currently always just "Chat";
    /// future sections like images/files would be added here, selectable the same way). Fixed
    /// height, matching <see cref="DevicesSectionStrip"/>'s empty placeholder, so the strip row
    /// reads as a permanent, always-present second tab row across every tab.</summary>
    private static UIElement BuildSectionStrip(IReadOnlyList<string> sections, string active)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var section in sections)
        {
            var isActive = section == active;
            strip.Children.Add(new Border
            {
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 4, 0),
                Background = isActive ? Brushes.White : Brushes.Transparent,
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1, 1, 1, 0),
                Child = new TextBlock
                {
                    Text = section, FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal
                }
            });
        }
        return new Border
        {
            Height = 32, Margin = new Thickness(0, 0, 0, 8),
            BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = strip
        };
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
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
    /// found/lost, pairing/unpairing, or the mesh session's connected peer/state.
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
            return new DeviceRow(uuid, name, isOnline, isPaired, isConnectedNow, isConnecting, ip, port);
        })
        .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

        _deviceRows.Clear();
        foreach (var row in rows) _deviceRows.Add(row);
        RefreshAllTabsConnectionState();
    }

    private async Task ListenLoopAsync()
    {
        // Runs for the window's lifetime: one incoming connection at a time, then back to
        // listening. Continuations resume on the UI thread (captured dispatcher context).
        await _listener.RunAcceptLoopAsync(_identity, HandleIncomingAsync,
            ex =>
            {
                DebugLog.Write($"ListenLoopAsync: accept/handler error {ex.GetType().Name}: {ex.Message}");
                Dispatcher.InvokeAsync(() => StatusText.Text = $"Incoming connection failed: {ex.Message}");
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
            // RunAsync only returns Reconnected after the peer's certificate already matched our
            // trust record, so it is always a background reconnect of an already-paired device,
            // never a new pairing. Report it quietly (status text only), the same way our own
            // outgoing background reconnect does, instead of popping a dialog on every network
            // blip. (Previously this also required _meshSession/_meshPeerUuid to already point at
            // this peer, which missed the reconnect right after an app restart — and any other
            // case where that bookkeeping lagged behind reality — popping the dialog every single
            // time the peer reconnected in a loop.)
            if (outcome.Result == PairingResult.Reconnected)
                StatusText.Text = $"Reconnected to {peerName} (trusted).";
            else
                ShowOutcome(outcome, peerName);
            if (outcome.Result is PairingResult.Paired or PairingResult.Reconnected)
            {
                // The socket we accepted came from the peer's ephemeral port, so its listening
                // endpoint (needed to reconnect later) comes from mDNS when we have it.
                var endpoint = ResolvePeerEndpoint(outcome.PeerUuid!, (session.PeerIp, 0));
                await AttachMeshSessionAsync(session, outcome.PeerUuid!, peerName, endpoint);
                // Only a first-time pairing auto-opens a tab — a silent background/manual
                // reconnect to an already-paired device must not pop UI on its own.
                if (outcome.Result == PairingResult.Paired) OpenOrFocusDeviceTab(outcome.PeerUuid!, peerName);
            }
            RebuildDeviceRows();
        }
        finally
        {
            _pairingInFlight = false;
            _pendingIncomingSession = null;
            if (_pendingConfirmWindow is { IsVisible: true }) _pendingConfirmWindow.Close();
            _pendingConfirmWindow = null;
        }
    }

    /// <summary>First-time pairing: the device isn't trusted yet. Always a manual click — there
    /// is no automatic path that reaches an unpaired device.</summary>
    private async void PairButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DeviceRow target }) return;
        await ConnectOrPairAsync(target, isFirstTimePair: true);
    }

    /// <summary>Manual reconnect to an already-paired device. If a different device is currently
    /// connected, that session is dropped first (only one active connection at a time).</summary>
    private async void ConnectRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DeviceRow target }) return;
        if (_meshSession != null && _meshPeerUuid != target.Uuid) await DisconnectCurrentSessionAsync();
        await ConnectOrPairAsync(target, isFirstTimePair: false);
    }

    private async Task ConnectOrPairAsync(DeviceRow target, bool isFirstTimePair)
    {
        if (_pairingInFlight) return;
        _pairingInFlight = true;
        _connectingUuid = target.Uuid;
        RebuildDeviceRows();
        // Always shown, even for a manual reconnect to an already-trusted device: usually there
        // is nothing for the dialer to see or type (the listener's own lightweight "allow?"
        // prompt is all that happens, and this closes right away) — but if the peer's trust
        // record was removed on its side since we last connected, the protocol falls back to a
        // full PIN exchange and EnterPinAsync needs this window to exist to prompt for the code.
        var connectingWindow = new PinEntryWindow(target.Name) { Owner = this };
        connectingWindow.ConnectingCancelRequested += () => _pendingDialerSession?.Dispose();
        connectingWindow.Show();
        _pendingPinEntryWindow = connectingWindow;
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
            _pendingDialerSession = session;

            // On success the transport is detached into a MeshSession, which makes this
            // using's Dispose a no-op; on every other outcome it closes the socket as before.
            using (session)
            {
                var outcome = await session.RunAsync(_selfUuid, _selfName, _pairedDevicesPath, this,
                    isManualConnect: true);
                var peerName = outcome.PeerName ?? target.Name;
                // No success popup — the row's own Connect/Disconnect coloring is the
                // confirmation. Only report failures, and only as status text (no modal), so a
                // declined/failed manual attempt doesn't block on a dialog the user has to dismiss.
                StatusText.Text = outcome.Result switch
                {
                    PairingResult.Paired => $"Paired with {peerName}.",
                    PairingResult.Reconnected => $"Connected to {peerName}.",
                    PairingResult.ConnectionFailed => $"Connection failed: {outcome.Detail}",
                    _ => $"Pairing failed: {outcome.Detail}"
                };
                if (outcome.Result is PairingResult.Paired or PairingResult.Reconnected)
                {
                    await AttachMeshSessionAsync(session, outcome.PeerUuid!, peerName,
                        (target.IpAddress, target.Port));
                    if (outcome.Result == PairingResult.Paired) OpenOrFocusDeviceTab(outcome.PeerUuid!, peerName);
                }
            }
        }
        finally
        {
            // Reset the flag before anything that could throw (e.g. closing a window), so a
            // problem here can never leave _pairingInFlight stuck true and silently dropping
            // every later incoming/outgoing attempt.
            _pairingInFlight = false;
            _connectingUuid = null;
            _pendingDialerSession = null;
            _pendingPinEntryWindow = null;
            if (connectingWindow is { IsVisible: true }) connectingWindow.Close();
            RebuildDeviceRows();
        }
    }

    private async void DisconnectRow_Click(object sender, RoutedEventArgs e) => await DisconnectCurrentSessionAsync();

    /// <summary>
    /// Closes the current mesh session on purpose and stops the reconnect loop. No further
    /// background reconnect happens for this peer until the user reconnects (Connect button, or
    /// an incoming trusted-reconnect from the peer).
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
        StatusText.Text = $"Disconnected by {_meshPeerName}";
        session.Dispose();
        RebuildDeviceRows();
    }

    /// <summary>
    /// "Connection failed" = TCP/TLS/transport problem (incl. the 10 s timeout);
    /// "Pairing failed" = connected, but the PIN was rejected/declined/timed out.
    /// Status-line only — no dialog, so it never blocks the accept loop or demands dismissal.
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
        DebugLog.Write($"OnMeshSessionStateChanged: state={state} isCurrent={ReferenceEquals(session, _meshSession)}");
        if (!ReferenceEquals(session, _meshSession)) return; // a replaced/disposed session
        if (ReferenceEquals(session, _meshTrustLostSession)) return; // keep "Re-pairing required"
        StatusText.Text = state switch
        {
            MeshSessionState.Connected => $"Connected to {_meshPeerName}",
            MeshSessionState.Reconnecting => $"Reconnecting to {_meshPeerName}...",
            MeshSessionState.Disconnected => $"Disconnected from {_meshPeerName}",
            _ => StatusText.Text
        };
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
                    StatusText.Text = $"{peerName} is no longer trusted (certificate changed or pairing " +
                        "removed). Press Connect to pair again.";
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
                // Automatic background reconnect: never manual, so the listener never gets the
                // "allow this reconnect?" prompt added for the Connect button. Must still be sent
                // — RunAsync on the other side expects this exact step, in this exact order.
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
                StatusText.Text = $"Could not update paired-device record: {ex.Message}";
            }

            var name = session.PeerName ?? peerName;
            await AttachMeshSessionAsync(session, peerUuid, name, (ip, port));
            StatusText.Text = $"Reconnected to {name} (trusted).";
            return ReconnectAttemptResult.Reconnected;
        }
    }

    /// <summary>The peer's listening endpoint as currently advertised over mDNS, if seen.</summary>
    private (string Ip, int Port) ResolvePeerEndpoint(string peerUuid, (string Ip, int Port) fallback) =>
        _discovered.TryGetValue(peerUuid, out var discovered) ? (discovered.IpAddress, discovered.Port) : fallback;

    /// <summary>Removes the local trust record for the row's device, after confirmation. Local-
    /// only (see <see cref="PairedDeviceStore.Remove"/>): the peer's own trust of us is untouched.
    /// Also drops the live session if this happened to be the currently-connected device.</summary>
    private async void UnpairButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DeviceRow target }) return;
        var confirmed = await ShowConfirmAsync("Unpair", $"Unpair {target.Name}? You'll need to pair again.",
            pin: null, "Unpair", "Cancel", CancellationToken.None);
        if (!confirmed) return;

        if (_meshPeerUuid == target.Uuid) await DisconnectCurrentSessionAsync();
        PairedDeviceStore.Remove(_pairedDevicesPath, target.Uuid);
        // Unpairing loses the chat too, same as the trust record — the peer's own copy is
        // untouched (unpair is local-only) and restores ours if we re-pair with them later (see
        // AttachMeshSessionAsync's history sync).
        ChatHistoryStore.DeleteForPeer(_chatDbPath, target.Uuid);
        CloseDeviceTab(target.Uuid); // nothing meaningful left to show; a re-pair reopens it fresh
        RebuildDeviceRows();
    }

    // Chat ---------------------------------------------------------------------------------

    private void SendChatMessage(string peerUuid, string text)
    {
        if (text.Length > MaxChatTextLength)
        {
            StatusText.Text = "Message too long.";
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
            StatusText.Text = $"Could not save chat message: {ex.Message}";
            return;
        }
        // Reload rather than append, so the UI list reflects the DB's own per-peer cap eviction
        // exactly (the 50th-oldest message disappearing from the DB also disappears here).
        if (entry != null) ReloadDeviceTab(peerUuid);
        if (_meshPeerUuid != peerUuid) return; // tab's Send box is disabled for this case already
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
    /// live to the peer if they're the one currently connected (see the project's multi-tab
    /// design — an edit made while that peer is offline never gets retried/queued).</summary>
    private void EditChatMessage(string peerUuid, string msgId, string newText)
    {
        if (string.IsNullOrEmpty(newText) || newText.Length > MaxChatTextLength) return;
        try
        {
            ChatHistoryStore.UpdateText(_chatDbPath, peerUuid, msgId, newText);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not edit chat message: {ex.Message}";
            return;
        }
        ReloadDeviceTab(peerUuid);
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
            StatusText.Text = $"Could not delete chat message: {ex.Message}";
            return;
        }
        ReloadDeviceTab(peerUuid);
        if (_meshPeerUuid == peerUuid) _ = _meshSession?.SendAsync(new ChatDeleteMessage(msgId));
    }

    private void OnMeshMessageReceived(MeshMessage message, string peerUuid, string peerName, MeshSession session)
    {
        // Raised on MeshSession's read loop (background thread).
        DebugLog.Write($"OnMeshMessageReceived: {message.GetType().Name} sessionNull={session == null}");
        switch (message)
        {
            case GoodbyeMessage:
                Dispatcher.InvokeAsync(() => OnPeerGoodbye(session));
                return;
            case ChatHistorySyncMessage sync:
                OnChatHistorySyncReceived(sync, peerUuid, peerName);
                return;
            case ChatEditMessage edit:
                try { ChatHistoryStore.UpdateText(_chatDbPath, peerUuid, edit.Id, edit.Text); }
                catch (Exception ex)
                {
                    Dispatcher.InvokeAsync(() => StatusText.Text = $"Could not save chat edit: {ex.Message}");
                    return;
                }
                Dispatcher.InvokeAsync(() => ReloadDeviceTab(peerUuid));
                return;
            case ChatDeleteMessage delete:
                try { ChatHistoryStore.DeleteMessage(_chatDbPath, peerUuid, delete.Id); }
                catch (Exception ex)
                {
                    Dispatcher.InvokeAsync(() => StatusText.Text = $"Could not save chat delete: {ex.Message}");
                    return;
                }
                Dispatcher.InvokeAsync(() => ReloadDeviceTab(peerUuid));
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
            Dispatcher.InvokeAsync(() => StatusText.Text = $"Could not save chat message: {ex.Message}");
            return;
        }
        if (entry == null) return; // already had this id (shouldn't happen for a live send, but harmless)
        Dispatcher.InvokeAsync(() => ReloadDeviceTab(peerUuid));
    }

    /// <summary>Merges the peer's own history for this conversation into ours, deduping by
    /// <see cref="ChatMessage.MsgId"/>. Restores a chat after we unpaired and re-paired with the
    /// same device (see UnpairButton_Click).</summary>
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
        Dispatcher.InvokeAsync(() => ReloadDeviceTab(peerUuid));
    }

    /// <summary>
    /// A bubble's text box was left-clicked. Native drag-selection already happened before this
    /// mouse-up fires (so <c>SelectionLength &gt; 0</c> there means "the user just finished
    /// selecting text, don't also pop a menu"); a plain click with nothing selected is the
    /// "short tap" the spec asks for, opening Copy/Edit/Delete instead.
    /// </summary>
    private void ChatBubble_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.Tag is not ChatRowVm row) return;
        if (!textBox.IsReadOnly) return; // already mid-inline-edit; let typing/clicks through
        if (textBox.SelectionLength > 0) return; // end of a drag-select, not a tap
        ShowChatBubbleMenu(textBox, row);
    }

    private void ShowChatBubbleMenu(TextBox textBox, ChatRowVm row)
    {
        var menu = new ContextMenu();
        var copyItem = new MenuItem { Header = "Copy" };
        copyItem.Click += (_, _) =>
        {
            try { Clipboard.SetText(row.Message.Text); }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                StatusText.Text = $"Clipboard is busy, try again: {ex.Message}";
            }
        };
        menu.Items.Add(copyItem);
        if (row.IsOwn)
        {
            var editItem = new MenuItem { Header = "Edit" };
            editItem.Click += (_, _) => BeginInlineEdit(textBox, row);
            menu.Items.Add(editItem);
            var deleteItem = new MenuItem { Header = "Delete" };
            deleteItem.Click += async (_, _) =>
            {
                var confirmed = await ShowConfirmAsync("Delete message", "Delete this message?", null,
                    "Delete", "Cancel", CancellationToken.None);
                if (confirmed) DeleteChatMessage(row.PeerUuid, row.Message.MsgId);
            };
            menu.Items.Add(deleteItem);
        }
        textBox.ContextMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>Turns a bubble's (own-message) TextBox editable in place instead of opening a
    /// separate dialog. Enter or losing focus commits; Escape reverts to the stored text.</summary>
    private void BeginInlineEdit(TextBox textBox, ChatRowVm row)
    {
        textBox.IsReadOnly = false;
        textBox.Text = row.Message.Text; // edit the full text, not the list's truncated preview
        textBox.SelectAll();
        textBox.Focus();

        void Commit()
        {
            textBox.KeyDown -= OnKeyDown;
            textBox.LostFocus -= OnLostFocus;
            textBox.IsReadOnly = true;
            var newText = textBox.Text;
            if (newText != row.Message.Text && !string.IsNullOrEmpty(newText))
                EditChatMessage(row.PeerUuid, row.Message.MsgId, newText);
            else
                textBox.Text = row.Message.DisplayText; // no real change: restore the preview text
        }

        void Revert()
        {
            textBox.KeyDown -= OnKeyDown;
            textBox.LostFocus -= OnLostFocus;
            textBox.IsReadOnly = true;
            textBox.Text = row.Message.DisplayText;
        }

        void OnKeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Revert(); e.Handled = true; }
        }

        void OnLostFocus(object s, RoutedEventArgs e) => Commit();

        textBox.KeyDown += OnKeyDown;
        textBox.LostFocus += OnLostFocus;
    }

    // IPairingPrompt -------------------------------------------------------------------

    public Task<bool> ConfirmCertificateChangedAsync(string peerName, CancellationToken ct) =>
        ShowConfirmAsync("Certificate changed",
            $"Certificate changed for {peerName} — re-pair?\n\nThis device no longer presents the " +
            "certificate you paired with. Only continue if you know why (e.g. the app was reinstalled).",
            pin: null, "Re-pair", "Cancel", ct);

    public Task<bool> ConfirmPinAsync(string peerName, string pin, CancellationToken ct) =>
        ShowConfirmAsync("Pairing request", $"{peerName} wants to connect. Allow?", pin, "Allow", "Deny", ct,
            keepOpenOnYes: true, onCancelWhileConnecting: () => _pendingIncomingSession?.Dispose());

    /// <summary>Dialer side: morphs the "Connecting..." window already shown by the Pair/Connect
    /// click into a code-entry prompt. Returns null (never matches) if that window is gone
    /// (closed early, or this got called for some other reason it shouldn't have).</summary>
    public Task<string?> EnterPinAsync(string peerName, CancellationToken ct) =>
        _pendingPinEntryWindow is { IsVisible: true } window
            ? window.PromptForCodeAsync(ct)
            : Task.FromResult<string?>(null);

    /// <summary>Listener side, already-trusted peer, manual Connect only: lightweight "X wants to
    /// connect, allow?" with no PIN — trust is already established.</summary>
    public Task<bool> ConfirmReconnectAsync(string peerName, CancellationToken ct) =>
        ShowConfirmAsync("Connect?", $"{peerName} wants to connect. Allow?", pin: null, "Allow", "Deny", ct);

    /// <summary>
    /// Shows a modeless yes/no confirm window and resolves once an answer is known.
    /// <paramref name="keepOpenOnYes"/> (PIN confirm only): a "Yes" click does not close the
    /// window — it switches to a "Connecting..." placeholder (<see cref="PinConfirmWindow.ShowConnecting"/>)
    /// and the window is tracked in <see cref="_pendingConfirmWindow"/> for the caller
    /// (HandleIncomingAsync's finally) to close once the pairing attempt actually finishes. A
    /// "No" click, or the window closing any other way, resolves immediately and closes as before.
    /// <paramref name="onCancelWhileConnecting"/> fires if the user then clicks the relabelled
    /// negative button during that wait — this does not change the already-resolved answer, it
    /// just lets the caller abort the attempt (e.g. close the socket).
    /// </summary>
    private async Task<bool> ShowConfirmAsync(string title, string message, string? pin,
        string yes, string no, CancellationToken ct, bool keepOpenOnYes = false,
        Action? onCancelWhileConnecting = null)
    {
        var window = new PinConfirmWindow(title, message, pin, yes, no) { Owner = this };
        var decided = new TaskCompletionSource<bool>();
        window.Decided += result =>
        {
            if (result && keepOpenOnYes)
            {
                window.ShowConnecting();
                _pendingConfirmWindow = window;
            }
            else
            {
                window.Close();
            }
            decided.TrySetResult(result);
        };
        if (onCancelWhileConnecting != null) window.CancelRequested += onCancelWhileConnecting;
        // Closing any other way (the [x] button, or the ct.Register callback below) still
        // resolves "not matching" if Decided never fired.
        window.Closed += (_, _) => decided.TrySetResult(window.Result);
        // Timeout or peer abort: close the dialog, which counts as "not matching".
        using var registration = ct.Register(() => Dispatcher.InvokeAsync(() =>
        {
            if (window.IsVisible) window.Close();
        }));
        window.Show();
        return await decided.Task;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _shutdown.Cancel();
        _meshSession?.Dispose();
        _listener.Dispose();
        _discovery.Dispose();
    }
}
