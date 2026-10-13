using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Mesh.Core;

namespace Mesh.App;

public partial class MainWindow : Window
{
    private readonly MeshDiscovery _discovery = new();
    private readonly List<DiscoveredDevice> _devices = new();
    private readonly string _identityPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Mesh", "identity.pfx");
    private readonly string _pairedDevicesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Mesh", "paired_devices.json");

    public MainWindow()
    {
        InitializeComponent();
        var identity = IdentityStore.LoadOrCreate(_identityPath);
        var selfUuid = Guid.NewGuid().ToString(); // stable per-install id; persisted separately in a future task
        _discovery.Advertise(selfUuid, Environment.MachineName, "windows", 51555);
        _discovery.StartBrowsing(device =>
        {
            Dispatcher.Invoke(() =>
            {
                if (_devices.Any(d => d.Uuid == device.Uuid)) return;
                _devices.Add(device);
                DeviceListBox.Items.Add(device);
            });
        });
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceListBox.SelectedItem is not DiscoveredDevice target) return;

        var identity = IdentityStore.LoadOrCreate(_identityPath);
        var session = new PairingSession();
        try
        {
            await session.ConnectTo(identity, target.IpAddress, target.Port);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Connection failed: {ex.Message}");
            return;
        }

        if (session.IsPeerTrusted(_pairedDevicesPath, target.Uuid))
        {
            MessageBox.Show($"Reconnected to {target.Name} (trusted).");
            return;
        }

        var pin = session.ComputePin();
        var pinWindow = new PinConfirmWindow(pin);
        var confirmed = pinWindow.ShowDialog() == true;
        if (!confirmed)
        {
            MessageBox.Show("Pairing failed.");
            return;
        }

        PairedDeviceStore.Upsert(_pairedDevicesPath, new PairedDevice
        {
            Uuid = target.Uuid,
            Name = target.Name,
            Fingerprint = session.PeerFingerprint!,
            LastIp = target.IpAddress,
            PairedAt = DateTimeOffset.UtcNow
        });
        MessageBox.Show($"Paired with {target.Name}.");
    }
}
