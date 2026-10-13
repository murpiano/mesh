using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Mesh.Core;

namespace Mesh.App;

public partial class MainWindow : Window, IPairingPrompt
{
    private static readonly string MeshDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mesh");
    private readonly string _pairedDevicesPath = Path.Combine(MeshDir, "paired_devices.json");

    private readonly MeshDiscovery _discovery = new();
    private readonly ObservableCollection<DiscoveredDevice> _devices = new();
    private readonly X509Certificate2 _identity;
    private readonly string _selfUuid;
    private readonly string _selfName = Environment.MachineName;
    private readonly PairingListener _listener;
    private readonly CancellationTokenSource _shutdown = new();

    // All pairing work is marshalled onto the UI thread, so a plain flag is enough to keep
    // a double-click or an incoming connection from racing an attempt already in flight.
    private bool _pairingInFlight;

    public MainWindow()
    {
        InitializeComponent();
        DeviceListBox.ItemsSource = _devices;

        // Loaded once and cached for the window's lifetime.
        _identity = IdentityStore.LoadOrCreate(Path.Combine(MeshDir, "identity.pfx"));
        _selfUuid = DeviceIdentity.GetOrCreateUuid(Path.Combine(MeshDir, "device_id.txt"));

        // Port 0: the OS picks a free ephemeral port; that actual port is what we advertise.
        _listener = new PairingListener(0);
        _ = ListenLoopAsync();
        StartDiscovery();

        Closed += OnWindowClosed;
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
            ShowOutcome(outcome, outcome.PeerName ?? session.PeerIp);
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

            using (session)
            {
                var outcome = await session.RunAsync(_selfUuid, _selfName, _pairedDevicesPath, this);
                ShowOutcome(outcome, outcome.PeerName ?? target.Name);
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
        _listener.Dispose();
        _discovery.Dispose();
    }
}
