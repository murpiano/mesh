namespace Mesh.Core;

/// <summary>
/// Temporary diagnostic log for the "clipboard sync stops after first success" repro: appends
/// timestamped lines to debug.log in the app's data folder (<see cref="MeshApp.DataDir"/>). Not
/// for production telemetry — remove once that bug is found and fixed.
/// </summary>
public static class DebugLog
{
    private static readonly string Path = System.IO.Path.Combine(MeshApp.DataDir, "debug.log");
    private static readonly object Lock = new();

    public static void Write(string line)
    {
        try
        {
            lock (Lock)
            {
                System.IO.File.AppendAllText(Path, $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {line}\n");
            }
        }
        catch
        {
            // Diagnostics must never affect the thing they're diagnosing.
        }
    }
}
