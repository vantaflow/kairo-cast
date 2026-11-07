using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KairoCast.Link;

/// <summary>Une box Kairo découverte sur le réseau local.</summary>
public sealed record BoxInfo(string Name, IPAddress Address, int Port);

/// <summary>
/// Suit l'état des enregistrements mDNS d'un service et déduit les box apparues/disparues.
/// Pur (horloge injectée) donc testable depuis des messages en mémoire.
/// </summary>
public sealed class BoxTracker
{
    private sealed class Entry
    {
        public DateTime PtrExpiry;
        public string? Target; public int Port; public DateTime SrvExpiry;
        public string? DisplayName;
        public BoxInfo? Announced;
    }

    private readonly string _service;
    private readonly Dictionary<string, Entry> _byInstance = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (IPAddress Addr, DateTime Expiry)> _v4 = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (IPAddress Addr, DateTime Expiry)> _v6 = new(StringComparer.OrdinalIgnoreCase);

    public event Action<BoxInfo>? Found;
    public event Action<BoxInfo>? Lost;

    public BoxTracker(string service = "_kairocast._tcp.local") => _service = service.TrimEnd('.');

    /// <summary>Instances connues pour lesquelles il manque SRV ou adresse (à re-interroger).</summary>
    public IEnumerable<(string Instance, string? Target)> Unresolved =>
        _byInstance.Where(e => e.Value.Announced == null).Select(e => (e.Key, e.Value.Target)).ToList();

    public void Feed(IEnumerable<DnsRecord> records, DateTime now)
    {
        foreach (var r in records)
        {
            var exp = now.AddSeconds(r.Ttl);
            switch (r.Type)
            {
                case DnsRecord.TypePtr when Eq(r.Name, _service) && r.Target != null:
                    if (r.Ttl == 0) Remove(r.Target);
                    else Get(r.Target).PtrExpiry = exp;
                    break;
                case DnsRecord.TypeSrv when r.Target != null && IsInstance(r.Name):
                    if (r.Ttl == 0) Remove(r.Name);
                    else { var e = Get(r.Name); e.Target = r.Target; e.Port = r.Port; e.SrvExpiry = exp; }
                    break;
                case DnsRecord.TypeTxt when r.Txt != null && IsInstance(r.Name):
                    if (r.Ttl != 0 && _byInstance.TryGetValue(r.Name, out var te)
                        && r.Txt.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n))
                        te.DisplayName = n;
                    break;
                case DnsRecord.TypeA when r.Address != null:
                    if (r.Ttl == 0) _v4.Remove(r.Name); else _v4[r.Name] = (r.Address, exp);
                    break;
                case DnsRecord.TypeAaaa when r.Address != null:
                    if (r.Ttl == 0) _v6.Remove(r.Name); else _v6[r.Name] = (r.Address, exp);
                    break;
            }
        }
        Evaluate(now);
    }

    public void Sweep(DateTime now) => Evaluate(now);

    private bool IsInstance(string name) => name.EndsWith("." + _service, StringComparison.OrdinalIgnoreCase);
    private static bool Eq(string a, string b) => string.Equals(a.TrimEnd('.'), b, StringComparison.OrdinalIgnoreCase);
    private Entry Get(string instance)
    {
        if (!_byInstance.TryGetValue(instance, out var e)) _byInstance[instance] = e = new Entry();
        return e;
    }

    private void Remove(string instance)
    {
        if (!_byInstance.Remove(instance, out var e)) return;
        if (e.Announced != null) Lost?.Invoke(e.Announced);
    }

    private void Evaluate(DateTime now)
    {
        foreach (var (inst, e) in _byInstance.ToList())
        {
            // PTR périmé ou SRV périmé : la box n'est plus annoncée.
            if ((e.PtrExpiry != default && e.PtrExpiry <= now) || (e.SrvExpiry != default && e.SrvExpiry <= now))
            {
                Remove(inst);
                continue;
            }
            if (e.Announced != null || e.PtrExpiry == default || e.Target == null) continue;
            var addr = Resolve(e.Target, now);
            if (addr == null) continue;
            var label = inst[..^(_service.Length + 1)];
            var info = new BoxInfo(e.DisplayName ?? label, addr, e.Port);
            e.Announced = info;
            Found?.Invoke(info);
        }
        foreach (var k in _v4.Where(x => x.Value.Expiry <= now).Select(x => x.Key).ToList()) _v4.Remove(k);
        foreach (var k in _v6.Where(x => x.Value.Expiry <= now).Select(x => x.Key).ToList()) _v6.Remove(k);
    }

    private IPAddress? Resolve(string host, DateTime now)
    {
        if (_v4.TryGetValue(host, out var a) && a.Expiry > now) return a.Addr;
        if (_v6.TryGetValue(host, out var b) && b.Expiry > now) return b.Addr;
        return null;
    }
}

/// <summary>Navigateur mDNS minimal pour le service <c>_kairocast._tcp</c> (UDP 5353, multicast IPv4).</summary>
public sealed class Discovery : IDisposable
{
    public const string ServiceName = "_kairocast._tcp.local";
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    private readonly BoxTracker _tracker = new(ServiceName);
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Socket> _sockets = new();
    private bool _started;

    public event Action<BoxInfo>? Found { add => _tracker.Found += value; remove => _tracker.Found -= value; }
    public event Action<BoxInfo>? Lost { add => _tracker.Lost += value; remove => _tracker.Lost -= value; }

    public void Start()
    {
        if (_started) return;
        _started = true;
        foreach (var ip in LocalIPv4())
        {
            try { _sockets.Add(Open(ip)); } catch (SocketException) { }
        }
        foreach (var s in _sockets) _ = Task.Run(() => ReceiveLoop(s));
        _ = Task.Run(MaintenanceLoop);
    }

    private static IEnumerable<IPAddress> LocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address).ToList();

    private static Socket Open(IPAddress local)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try { s.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)15 /* SO_REUSEPORT (macOS/Linux) */, true); } catch { }
        s.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, local));
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        return s;
    }

    private async Task ReceiveLoop(Socket s)
    {
        var buf = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await s.ReceiveFromAsync(buf, SocketFlags.None, any, _cts.Token).ConfigureAwait(false);
                var recs = DnsMessage.ParseRecords(buf.AsSpan(0, r.ReceivedBytes));
                if (recs.Count == 0) continue;
                lock (_lock) _tracker.Feed(recs, DateTime.UtcNow);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { await Task.Delay(500).ConfigureAwait(false); }
        }
    }

    private async Task MaintenanceLoop()
    {
        int delay = 1000, tick = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Query(DnsMessage.BuildQuery((ServiceName, DnsRecord.TypePtr)));
                // Relance ciblée SRV/TXT/A pour les instances incomplètes.
                List<(string Instance, string? Target)> pending;
                lock (_lock) pending = _tracker.Unresolved.ToList();
                foreach (var (inst, target) in pending)
                {
                    var qs = new List<(string, ushort)> { (inst, DnsRecord.TypeSrv), (inst, DnsRecord.TypeTxt) };
                    if (target != null) qs.Add((target, DnsRecord.TypeA));
                    Query(DnsMessage.BuildQuery(qs.ToArray()));
                }
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                lock (_lock) _tracker.Sweep(DateTime.UtcNow);
                if (++tick >= 3) delay = Math.Min(delay * 2, 30000);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Query(byte[] q)
    {
        foreach (var s in _sockets.ToList())
        {
            try { s.SendTo(q, new IPEndPoint(Group, MdnsPort)); } catch (SocketException) { } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var s in _sockets) { try { s.Dispose(); } catch { } }
        _sockets.Clear();
        _cts.Dispose();
    }
}
