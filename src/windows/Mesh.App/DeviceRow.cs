using System.Windows;
using System.Windows.Media;

namespace Mesh.App;

/// <summary>
/// One row in the unified device list: every paired device (whether or not it is currently
/// visible over mDNS) plus every currently-discovered-but-unpaired device. Replaced wholesale in
/// <c>MainWindow</c>'s <c>ObservableCollection</c> whenever anything changes (mDNS, pairing,
/// session state) — same "replace, don't mutate" convention <c>DiscoveredDevice</c> already used,
/// so no <c>INotifyPropertyChanged</c> plumbing is needed here.
/// </summary>
public sealed record DeviceRow(
    string Uuid,
    string Name,
    bool IsOnline,
    bool IsPaired,
    bool IsConnectedNow,
    bool IsConnecting,
    string IpAddress,
    int Port)
{
    public Visibility PairVisibility => IsPaired ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PairedVisibility => IsPaired ? Visibility.Visible : Visibility.Collapsed;

    public bool PairEnabled => IsOnline && !IsConnecting;

    public string ConnectLabel => IsConnecting ? "..." : (IsConnectedNow ? "Connected" : "Connect");
    public bool ConnectEnabled => IsPaired && IsOnline && !IsConnectedNow && !IsConnecting;
    public Brush ConnectBrush => IsConnectedNow ? Brushes.MediumSeaGreen : Brushes.LightGray;

    public bool DisconnectEnabled => IsPaired && IsConnectedNow;
    public Brush DisconnectBrush => IsConnectedNow ? Brushes.LightGray : Brushes.IndianRed;

    // Removing a pairing is a local, offline-safe operation — it never needs the device to be
    // reachable right now.
    public bool UnpairEnabled => IsPaired;

    public Brush NameBrush => IsOnline ? Brushes.Black : Brushes.Gray;
}
