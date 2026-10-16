namespace Mesh.App;

/// <summary>
/// One row in the unified device list: every paired device (whether or not it is currently
/// visible over mDNS) plus every currently-discovered-but-unpaired device. Replaced wholesale in
/// <c>MainWindow</c>'s <c>ObservableCollection</c> whenever anything changes (mDNS, pairing,
/// session state, the row's popup opening or closing) — same "replace, don't mutate" convention
/// <c>DiscoveredDevice</c> already used, so no <c>INotifyPropertyChanged</c> plumbing is needed here.
/// Mirrors the Android app's DeviceRow.
/// </summary>
public sealed record DeviceRow(
    string Uuid,
    string Name,
    bool IsOnline,
    bool IsPaired,
    bool IsConnectedNow,
    bool IsConnecting,
    string IpAddress,
    int Port,
    bool IsHighlighted)
{
    /// <summary>The State column: empty when never paired, otherwise whichever of
    /// Connecting/Connected/Trusted currently applies.</summary>
    public string StateLabel =>
        !IsPaired ? "" : IsConnecting ? "Connecting" : IsConnectedNow ? "Connected" : "Trusted";
}
