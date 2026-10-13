namespace Mesh.Core;

/// <summary>
/// Stable per-install device UUID. Peers key their trust records by this value, so it
/// must survive restarts — it is generated once and persisted next to identity.pfx.
/// </summary>
public static class DeviceIdentity
{
    public static string GetOrCreateUuid(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (Guid.TryParse(existing, out var parsed)) return parsed.ToString();
            // Corrupt/empty file: fall through and regenerate.
        }

        var uuid = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, uuid);
        return uuid;
    }
}
