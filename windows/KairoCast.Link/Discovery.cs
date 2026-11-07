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
        public string? Target; public int Port; public DateTime SrvExpiry, SrvAt; public uint SrvTtl;
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

    /// <summary>Box annoncées dont le SRV a dépassé 80 % de son TTL : à re-interroger (SRV + A) avant expiration.</summary>
    public IEnumerable<(string Instance, string? Target)> NeedsRefresh(DateTime now) =>
        _byInstance.Where(e => e.Value.Announced != null && e.Value.SrvTtl > 0
                               && now >= e.Value.SrvAt.AddSeconds(e.Value.SrvTtl * 0.8))
                   .Select(e => (e.Key, e.Value.Target)).ToList();

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
                    else { var e = Get(r.Name); e.Target = r.Target; e.Port = r.Port; e.SrvExpiry = exp; e.SrvAt = now; e.SrvTtl = r.Ttl; }
                    break;
                case DnsRecord.TypeTxt when r.Txt != null && IsInstance(r.Name):
                    if (r.Ttl != 0 && r.Txt.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n))
                        Get(r.Name).DisplayName = n;
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

public enum DiscoveryStatus { Stopped, Ok, NoInterface, Error }

/// <summary>
/// Navigateur mDNS minimal pour le service <c>_kairocast._tcp</c> (UDP 5353, multicast IPv4).
/// Les événements <see cref="Found"/>, <see cref="Lost"/>, <see cref="StatusChanged"/> et <see cref="Log"/>
/// sont déclenchés sur un thread du pool, hors de tout verrou interne.
/// Les interfaces sont ré-énumérées à chaque changement d'adresse réseau (anti-rebond 2 s).
/// </summary>
public sealed class Discovery : IDisposable
{
    public const string ServiceName = "_kairocast._tcp.local";
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    private readonly BoxTracker _tracker = new(ServiceName);
    private readonly object _lock = new();       // protège _tracker et _pending
    private readonly object _sockLock = new();   // protège _sockets / _ifaceKey / _status
    private readonly List<(bool Found, BoxInfo Box)> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Socket> _sockets = new();
    private readonly Timer _debounce;
    private string _ifaceKey = "";
    private DiscoveryStatus _status = DiscoveryStatus.Stopped;
    private bool _started, _disposed;

    public event Action<BoxInfo>? Found;
    public event Action<BoxInfo>? Lost;
    public event Action<DiscoveryStatus>? StatusChanged;
    public event Action<string>? Log;

    public DiscoveryStatus Status { get { lock (_sockLock) return _status; } }

    public Discovery()
    {
        _tracker.Found += b => _pending.Add((true, b));
        _tracker.Lost += b => _pending.Add((false, b));
        _debounce = new Timer(_ => SafeRebuild(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        lock (_sockLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Discovery));
            if (_started) return;
            _started = true;
        }
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        SafeRebuild();
        _ = Task.Run(MaintenanceLoop);
    }

    private void OnAddressChanged(object? s, EventArgs e)
    {
        try { _debounce.Change(2000, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void Emit(string msg)
    {
        try { Log?.Invoke(msg); } catch { }
    }

    private void SafeRebuild()
    {
        try { Rebuild(); }
        catch (Exception ex) { Emit("Rebuild: " + ex.Message); SetStatus(DiscoveryStatus.Error); }
    }

    private void SetStatus(DiscoveryStatus st)
    {
        lock (_sockLock)
        {
            if (_status == st) return;
            _status = st;
        }
        try { StatusChanged?.Invoke(st); } catch (Exception ex) { Emit("StatusChanged: " + ex.Message); }
    }

    private static List<IPAddress> LocalIPv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address).ToList();
        }
        catch (NetworkInformationException) { return new List<IPAddress>(); }
    }

    private void Rebuild()
    {
        var addrs = LocalIPv4();
        var key = string.Join(",", addrs.Select(a => a.ToString()).OrderBy(x => x));
        DiscoveryStatus st;
        lock (_sockLock)
        {
            if (_disposed) return;
            if (key == _ifaceKey && _sockets.Count > 0) return;
            foreach (var s in _sockets) { try { s.Dispose(); } catch { } }
            _sockets.Clear();
            _ifaceKey = key;
            foreach (var ip in addrs)
            {
                try
                {
                    var s = Open(ip);
                    _sockets.Add(s);
                    _ = Task.Run(() => ReceiveLoop(s));
                }
                catch (Exception ex) { Emit($"Interface {ip} : {ex.Message}"); }
            }
            st = addrs.Count == 0 ? DiscoveryStatus.NoInterface
               : _sockets.Count == 0 ? DiscoveryStatus.Error : DiscoveryStatus.Ok;
            if (st == DiscoveryStatus.NoInterface) Emit("Aucune interface réseau utilisable");
        }
        SetStatus(st);
        Query(DnsMessage.BuildQuery((ServiceName, DnsRecord.TypePtr)));
    }

    private static Socket Open(IPAddress local)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try { s.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)15 /* SO_REUSEPORT (macOS/Linux) */, true); } catch { }
            if (OperatingSystem.IsWindows())
            {
                // Évite que les ICMP « port unreachable » ne fassent échouer la réception (SIO_UDP_CONNRESET).
                try { s.IOControl(-1744830452, new byte[] { 0 }, null); } catch { }
            }
            s.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, local));
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    private bool IsCurrent(Socket s)
    {
        lock (_sockLock) return !_disposed && _sockets.Contains(s);
    }

    private async Task ReceiveLoop(Socket s)
    {
        var buf = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested && IsCurrent(s))
        {
            try
            {
                var r = await s.ReceiveFromAsync(buf, SocketFlags.None, any, _cts.Token).ConfigureAwait(false);
                var recs = DnsMessage.ParseRecords(buf.AsSpan(0, r.ReceivedBytes));
                if (recs.Count == 0) continue;
                Process(() => _tracker.Feed(recs, DateTime.UtcNow));
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                if (!IsCurrent(s)) return;
                Emit("Réception : " + ex.Message);
                try { await Task.Delay(500, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Exécute une action sur le suivi sous verrou, puis déclenche les événements hors verrou.</summary>
    private void Process(Action act)
    {
        List<(bool Found, BoxInfo Box)> events;
        lock (_lock)
        {
            act();
            events = _pending.ToList();
            _pending.Clear();
        }
        foreach (var (found, box) in events)
        {
            try { (found ? Found : Lost)?.Invoke(box); }
            catch (Exception ex) { Emit("Gestionnaire d'événement : " + ex.Message); }
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
                List<(string Instance, string? Target)> pending;
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    pending = _tracker.Unresolved.Concat(_tracker.NeedsRefresh(now)).ToList();
                }
                foreach (var (inst, target) in pending)
                {
                    var qs = new List<(string, ushort)> { (inst, DnsRecord.TypeSrv), (inst, DnsRecord.TypeTxt) };
                    if (target != null) qs.Add((target, DnsRecord.TypeA));
                    Query(DnsMessage.BuildQuery(qs.ToArray()));
                }
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                Process(() => _tracker.Sweep(DateTime.UtcNow));
                if (++tick >= 3) delay = Math.Min(delay * 2, 10000);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                Emit("Maintenance : " + ex.Message);
                try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private void Query(byte[] q)
    {
        List<Socket> socks;
        lock (_sockLock) socks = _sockets.ToList();
        foreach (var s in socks)
        {
            try { s.SendTo(q, new IPEndPoint(Group, MdnsPort)); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { Emit("Envoi : " + ex.Message); }
        }
    }

    public void Dispose()
    {
        lock (_sockLock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var s in _sockets) { try { s.Dispose(); } catch { } }
            _sockets.Clear();
        }
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _debounce.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }
}
