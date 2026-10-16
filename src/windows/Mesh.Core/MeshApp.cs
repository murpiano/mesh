namespace Mesh.Core;

/// <summary>
/// Release and debug builds of the desktop app are two separate apps on one PC, the same way
/// the phone has "Mesh" and "Mesh Dev": a debug build gets its own data folder, so a build from
/// source never shares the identity, pairings or chat of the installed release, and a device
/// name that tells it apart on the other side.
/// </summary>
public static class MeshApp
{
#if DEBUG
    public const string Name = "Mesh Dev";
    public const string DeviceNameSuffix = " (dev)";
#else
    public const string Name = "Mesh";
    public const string DeviceNameSuffix = "";
#endif

    /// <summary>%AppData%\Mesh, or %AppData%\Mesh Dev for a debug build.</summary>
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);
}
