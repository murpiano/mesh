using Makaretu.Dns;

namespace Mesh.Core;

public class DiscoveredDevice
{
    public required string Uuid { get; set; }
    public required string Name { get; set; }
    public required string Platform { get; set; }
    public required string IpAddress { get; set; }
    public required int Port { get; set; }
}

public class MeshDiscovery : IDisposable
{
    private const string ServiceType = "_meshlink._tcp";
    private MulticastService? _mdns;
    private ServiceDiscovery? _discovery;
    private bool _advertised;

    public void Advertise(string uuid, string name, string platform, int port)
    {
        if (_advertised) return;
        _mdns ??= new MulticastService();
        _discovery ??= new ServiceDiscovery(_mdns);

        var profile = new ServiceProfile(name, ServiceType, (ushort)port);
        profile.AddProperty("id", uuid);
        profile.AddProperty("name", name);
        profile.AddProperty("platform", platform);

        _discovery.Advertise(profile);
        _mdns.Start();
        _discovery.Announce(profile);
        _advertised = true;
    }

    public void StartBrowsing(Action<DiscoveredDevice> onFound)
    {
        _mdns ??= new MulticastService();
        _discovery ??= new ServiceDiscovery(_mdns);

        _discovery.ServiceInstanceDiscovered += (s, e) =>
        {
            _mdns!.SendQuery(e.ServiceInstanceName, type: Makaretu.Dns.DnsType.TXT);
        };

        _mdns.AnswerReceived += (s, e) =>
        {
            var txtRecords = e.Message.Answers.OfType<TXTRecord>();
            foreach (var txt in txtRecords)
            {
                var props = txt.Strings
                    .Select(s2 => s2.Split('=', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0], parts => parts[1]);

                if (!props.TryGetValue("id", out var uuid)) continue;

                // Depending on whether the response came from an unsolicited
                // announcement or a direct query, the A/SRV records may appear
                // in either the Answers or AdditionalRecords section.
                var allRecords = e.Message.Answers.Concat(e.Message.AdditionalRecords);
                var address = allRecords.OfType<ARecord>().FirstOrDefault();
                var srv = allRecords.OfType<SRVRecord>().FirstOrDefault();
                if (address == null || srv == null) continue;

                onFound(new DiscoveredDevice
                {
                    Uuid = uuid,
                    Name = props.GetValueOrDefault("name", uuid),
                    Platform = props.GetValueOrDefault("platform", "unknown"),
                    IpAddress = address.Address.ToString(),
                    Port = srv.Port
                });
            }
        };

        _mdns.Start();
    }

    public void Stop()
    {
        _mdns?.Stop();
    }

    public void Dispose()
    {
        Stop();
        _discovery?.Dispose();
        _mdns?.Dispose();
    }
}
