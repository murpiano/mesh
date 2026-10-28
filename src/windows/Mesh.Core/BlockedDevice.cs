namespace Mesh.Core;

/// <summary>One device the user has blacklisted: it is kept out of the device list and every
/// connection it attempts is hung up on. Mirrors the Android app's BlockedDevice.</summary>
public class BlockedDevice
{
    public required string Uuid { get; set; }
    public required string Name { get; set; }
    public required DateTimeOffset BlockedAt { get; set; }
}
