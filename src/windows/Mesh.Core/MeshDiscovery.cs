using System.Net.Sockets;
using Makaretu.Dns;

namespace Mesh.Core;

public class DiscoveredDevice
{
    public required string Uuid { get; set; }
    public required string Name { get; set; }
    public required string Platform { get; set; }
    public required string IpAddress { get; set; }
    public required int Port { get; set; }

    /// <summary>True when the device announced that it can take a screen cast. Builds older
    /// than casting leave the TXT key out, so they are never offered it — their read loop would
    /// throw on the new message tags.</summary>
    public bool SupportsCast { get; set; }
}

/// <summary>Raised/reported when mDNS cannot be used (no interface, socket blocked, ...).</summary>
public sealed class MeshDiscoveryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// mDNS advertise + browse for <c>_meshlink._tcp</c>.
///
/// Browsing issues an active PTR query on start (and on every <see cref="StartBrowsing"/>
/// call, which doubles as "refresh"), so peers that were already running before we started
/// are found, not only those whose unsolicited announcements we happen to hear. Answers are
/// resolved per service-instance name: SRV (port + target host), TXT (id/name/platform) and
/// A (IPv4 of the SRV target) are tracked separately and correlated by name, querying for
/// whichever piece is still missing, instead of taking the first SRV/A record in a message.
/// </summary>
public class MeshDiscovery : IDisposable
{
    private const string ServiceType = "_meshlink._tcp";
    private const string InstanceSuffix = "._meshlink._tcp.local";

    /// <summary>The TXT key that says this build understands the cast messages.</summary>
    private const string CastTxtKey = "cast";

    private readonly object _lock = new();
    private MulticastService? _mdns;
    private ServiceDiscovery? _discovery;
    private bool _started;
    private bool _browseHandlersAttached;
    private ServiceProfile? _profile;

    private Action<DiscoveredDevice>? _onFound;
    private Action<string>? _onLost;

    // Resolution state, keyed by lower-cased DNS names.
    private readonly Dictionary<string, SRVRecord> _srv = new();
    private readonly Dictionary<string, Dictionary<string, string>> _txt = new();
    private readonly Dictionary<string, string> _hostIps = new();
    private readonly Dictionary<string, string> _emitted = new(); // instance -> "uuid|ip|port"
    private readonly HashSet<string> _queriedHosts = new();

    private void EnsureCreated()
    {
        _mdns ??= new MulticastService();
        _discovery ??= new ServiceDiscovery(_mdns);
    }

    private void EnsureStarted()
    {
        if (_started) return;
        try
        {
            _mdns!.Start();
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or IOException)
        {
            throw new MeshDiscoveryException("mDNS socket could not be opened.", ex);
        }
        _started = true;
        if (!MulticastService.GetNetworkInterfaces().Any())
            throw new MeshDiscoveryException("No multicast-capable network interface is available.");
    }

    /// <summary>Advertise this device. Idempotent. Throws <see cref="MeshDiscoveryException"/> on failure.</summary>
    public void Advertise(string uuid, string name, string platform, int port)
    {
        if (_profile != null) return;
        try
        {
            EnsureCreated();
            var profile = new ServiceProfile(name, ServiceType, (ushort)port);
            profile.AddProperty("id", uuid);
            profile.AddProperty("name", name);
            profile.AddProperty("platform", platform);
            profile.AddProperty(CastTxtKey, "1");

            _discovery!.Advertise(profile);
            EnsureStarted();
            _discovery.Announce(profile);
            _profile = profile;
        }
        catch (MeshDiscoveryException) { throw; }
        catch (Exception ex)
        {
            throw new MeshDiscoveryException("mDNS advertise failed.", ex);
        }
    }

    /// <summary>
    /// Start (or refresh) browsing. Safe to call repeatedly: handlers are attached once and
    /// every call sends a fresh active query. Failures are reported through
    /// <paramref name="onError"/> (or thrown if it is null).
    /// </summary>
    public void StartBrowsing(Action<DiscoveredDevice> onFound, Action<string>? onLost = null,
        Action<Exception>? onError = null)
    {
        _onFound = onFound;
        _onLost = onLost;
        try
        {
            EnsureCreated();
            if (!_browseHandlersAttached)
            {
                _discovery!.ServiceInstanceDiscovered += OnInstanceDiscovered;
                _discovery.ServiceInstanceShutdown += OnInstanceShutdown;
                _mdns!.AnswerReceived += OnAnswerReceived;
                _browseHandlersAttached = true;
            }
            EnsureStarted();
            lock (_lock) _queriedHosts.Clear();
            _discovery!.QueryServiceInstances(ServiceType);
        }
        catch (Exception ex)
        {
            var wrapped = ex as MeshDiscoveryException ?? new MeshDiscoveryException("mDNS browse failed.", ex);
            if (onError == null) throw wrapped;
            onError(wrapped);
        }
    }

    private static string Key(DomainName name) => name.ToString().TrimEnd('.').ToLowerInvariant();

    private static bool IsMeshInstance(string key) => key.EndsWith(InstanceSuffix, StringComparison.Ordinal);

    private void OnInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        var key = Key(e.ServiceInstanceName);
        if (!IsMeshInstance(key)) return;
        bool needSrv, needTxt;
        lock (_lock)
        {
            needSrv = !_srv.ContainsKey(key);
            needTxt = !_txt.ContainsKey(key);
        }
        // The PTR answer usually carries SRV/TXT/A as additional records (handled in
        // OnAnswerReceived); only query for what is still missing.
        if (needSrv) _mdns?.SendQuery(e.ServiceInstanceName, type: DnsType.SRV);
        if (needTxt) _mdns?.SendQuery(e.ServiceInstanceName, type: DnsType.TXT);
    }

    private void OnInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        var key = Key(e.ServiceInstanceName);
        if (!IsMeshInstance(key)) return;
        string? uuid = null;
        lock (_lock)
        {
            if (_txt.TryGetValue(key, out var props)) props.TryGetValue("id", out uuid);
            _srv.Remove(key);
            _txt.Remove(key);
            _emitted.Remove(key);
        }
        if (uuid != null) _onLost?.Invoke(uuid);
    }

    private void OnAnswerReceived(object? sender, MessageEventArgs e)
    {
        var records = e.Message.Answers.Concat(e.Message.AdditionalRecords).ToList();
        var toEmit = new List<DiscoveredDevice>();
        var hostsToQuery = new List<DomainName>();
        lock (_lock)
        {
            foreach (var a in records.OfType<ARecord>())
                _hostIps[Key(a.Name)] = a.Address.ToString();

            foreach (var srv in records.OfType<SRVRecord>())
            {
                var key = Key(srv.Name);
                if (IsMeshInstance(key) && srv.TTL > TimeSpan.Zero) _srv[key] = srv;
            }

            foreach (var txt in records.OfType<TXTRecord>())
            {
                var key = Key(txt.Name);
                if (!IsMeshInstance(key) || txt.TTL <= TimeSpan.Zero) continue;
                _txt[key] = txt.Strings
                    .Select(s => s.Split('=', 2))
                    .Where(parts => parts.Length == 2)
                    .GroupBy(parts => parts[0])
                    .ToDictionary(g => g.Key, g => g.First()[1]);
            }

            foreach (var (instance, srv) in _srv)
            {
                if (!_txt.TryGetValue(instance, out var props) || !props.TryGetValue("id", out var uuid))
                    continue;
                var target = Key(srv.Target);
                if (!_hostIps.TryGetValue(target, out var ip))
                {
                    if (_queriedHosts.Add(target)) hostsToQuery.Add(srv.Target);
                    continue;
                }
                var signature = $"{uuid}|{ip}|{srv.Port}|{props.GetValueOrDefault(CastTxtKey)}";
                if (_emitted.TryGetValue(instance, out var prev) && prev == signature) continue;
                _emitted[instance] = signature;
                toEmit.Add(new DiscoveredDevice
                {
                    Uuid = uuid,
                    Name = props.GetValueOrDefault("name", uuid),
                    Platform = props.GetValueOrDefault("platform", "unknown"),
                    IpAddress = ip,
                    Port = srv.Port,
                    SupportsCast = props.GetValueOrDefault(CastTxtKey) == "1"
                });
            }
        }
        foreach (var host in hostsToQuery) _mdns?.SendQuery(host, type: DnsType.A);
        foreach (var device in toEmit) _onFound?.Invoke(device);
    }

    public void Stop()
    {
        _mdns?.Stop();
        _started = false;
    }

    public void Dispose()
    {
        try
        {
            if (_profile != null) _discovery?.Unadvertise(_profile); // goodbye packet (TTL 0)
        }
        catch (Exception) { /* best effort on shutdown */ }
        Stop();
        _discovery?.Dispose();
        _mdns?.Dispose();
        GC.SuppressFinalize(this);
    }
}
