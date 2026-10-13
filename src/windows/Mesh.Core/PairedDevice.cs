namespace Mesh.Core;

public class PairedDevice
{
    public required string Uuid { get; set; }
    public required string Name { get; set; }
    public required string Fingerprint { get; set; }
    public required string LastIp { get; set; }
    public required DateTimeOffset PairedAt { get; set; }
}
