using System.Text.Json;

namespace Mesh.Core;

public static class PairedDeviceStore
{
    public static List<PairedDevice> Load(string path)
    {
        if (!File.Exists(path)) return new List<PairedDevice>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<PairedDevice>>(json) ?? new List<PairedDevice>();
        }
        catch (JsonException)
        {
            return new List<PairedDevice>();
        }
    }

    public static void Save(string path, List<PairedDevice> devices)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(devices);
        File.WriteAllText(path, json);
    }

    public static void Upsert(string path, PairedDevice device)
    {
        var devices = Load(path);
        devices.RemoveAll(d => d.Uuid == device.Uuid);
        devices.Add(device);
        Save(path, devices);
    }
}
