using System.Text.Json;

namespace Mesh.Core;

public static class PairedDeviceStore
{
    private static readonly object WriteLock = new();

    public static List<PairedDevice> Load(string path)
    {
        if (!File.Exists(path)) return new List<PairedDevice>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<PairedDevice>>(json) ?? new List<PairedDevice>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable store: treat as empty rather than crashing the app.
            return new List<PairedDevice>();
        }
    }

    public static PairedDevice? Find(string path, string uuid) =>
        Load(path).FirstOrDefault(d => d.Uuid == uuid);

    /// <summary>Atomic write: serialize to a temp file next to the target, then replace.</summary>
    public static void Save(string path, List<PairedDevice> devices)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var json = JsonSerializer.Serialize(devices);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public static void Upsert(string path, PairedDevice device)
    {
        lock (WriteLock)
        {
            var devices = Load(path);
            devices.RemoveAll(d => d.Uuid == device.Uuid);
            devices.Add(device);
            Save(path, devices);
        }
    }
}
