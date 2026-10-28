using System.Text.Json;

namespace Mesh.Core;

/// <summary>
/// The blacklist, stored next to the trust store in the same JSON-file way
/// (<see cref="PairedDeviceStore"/>). Blocking is purely local — the peer is never told, it just
/// stops getting anywhere — so a device unblocked here is an ordinary unpaired device again.
/// Mirrors the Android app's BlockedDeviceDao.
/// </summary>
public static class BlockedDeviceStore
{
    private static readonly object WriteLock = new();

    public static List<BlockedDevice> Load(string path)
    {
        if (!File.Exists(path)) return new List<BlockedDevice>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<BlockedDevice>>(json) ?? new List<BlockedDevice>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable store: treat as empty rather than crashing the app.
            return new List<BlockedDevice>();
        }
    }

    public static bool IsBlocked(string path, string uuid) => Load(path).Any(d => d.Uuid == uuid);

    /// <summary>Atomic write: serialize to a temp file next to the target, then replace.</summary>
    public static void Save(string path, List<BlockedDevice> devices)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var json = JsonSerializer.Serialize(devices);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public static void Add(string path, BlockedDevice device)
    {
        lock (WriteLock)
        {
            var devices = Load(path);
            devices.RemoveAll(d => d.Uuid == device.Uuid);
            devices.Add(device);
            Save(path, devices);
        }
    }

    public static void Remove(string path, string uuid)
    {
        lock (WriteLock)
        {
            var devices = Load(path);
            if (devices.RemoveAll(d => d.Uuid == uuid) > 0) Save(path, devices);
        }
    }
}
