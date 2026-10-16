namespace Mesh.Core;

/// <summary>
/// Temporary diagnostic log for the "clipboard sync stops after first success" repro: appends
/// timestamped lines to %AppData%\Mesh\debug.log. Not for production telemetry — remove once
/// that bug is found and fixed.
/// </summary>
public static class DebugLog
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mesh", "debug.log");
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
