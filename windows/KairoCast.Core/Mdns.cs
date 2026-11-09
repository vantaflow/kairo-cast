using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KairoCast.Core;

public sealed record Receiver(string Name, IPAddress Address, int Port)
{
    public override string ToString() => $"{Name} ({Address})";
}

/// <summary>Découverte mDNS minimale : une requête PTR, lecture des PTR/SRV/A en retour.</summary>
public static class Mdns
{
    static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static byte[] BuildQuery(string service)
    {
        var b = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var l in service.Split('.'))
        {
            b.Add((byte)l.Length);
            b.AddRange(Encoding.ASCII.GetBytes(l));
        }
        b.AddRange(new byte[] { 0, 0, 12, 0x80, 1 }); // PTR, IN, réponse unicast demandée (bit QU)
        return b.ToArray();
    }

    /// <summary>Récepteurs annoncés dans une réponse ; sans enregistrement A, l'adresse de l'expéditeur.</summary>
    public static List<Receiver> Parse(byte[] p, IPAddress from)
    {
        var result = new List<Receiver>();
        try
        {
            if (p.Length < 12) return result;
            int count = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4)) + BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6))
                + BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(8)) + BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(10));
            int qd = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4));
            int pos = 12;
            for (int i = 0; i < qd; i++) { ReadName(p, ref pos); pos += 4; }
            count -= qd;
            var instances = new List<string>();
            var srv = new Dictionary<string, (string host, int port)>(StringComparer.OrdinalIgnoreCase);
            var hosts = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count && pos < p.Length; i++)
            {
                var name = ReadName(p, ref pos);
                int type = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos));
                int len = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos + 8));
                int rd = pos + 10;
                if (rd + len > p.Length) break;
                if (type == 12 && name.StartsWith("_kairocast._tcp", StringComparison.OrdinalIgnoreCase))
                {
                    int q = rd; instances.Add(ReadName(p, ref q));
                }
                else if (type == 33)
                {
                    int port = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(rd + 4));
                    int q = rd + 6; srv[name] = (ReadName(p, ref q), port);
                }
                else if (type == 1 && len == 4)
                {
                    hosts[name] = new IPAddress(p.AsSpan(rd, 4));
                }
                else if (type == 16)
                {
                    // TXT : « n=<nom de la box> »
                    for (int t = rd; t < rd + len;)
                    {
                        int l = p[t];
                        var kv = Encoding.UTF8.GetString(p, t + 1, Math.Min(l, rd + len - t - 1));
                        if (kv.StartsWith("n=")) names[name] = kv[2..];
                        t += l + 1;
                    }
                }
                pos = rd + len;
            }
            foreach (var inst in instances)
            {
                // Le nom de la box peut contenir des points : on retire seulement le suffixe du service.
                var label = names.TryGetValue(inst, out var n) && n.Length > 0 ? n
                    : inst.EndsWith('.' + LinkProtocol.ServiceType, StringComparison.OrdinalIgnoreCase)
                    ? inst[..^(LinkProtocol.ServiceType.Length + 1)]
                    : inst.Split('.')[0];
                if (srv.TryGetValue(inst, out var s))
                    result.Add(new Receiver(label, hosts.TryGetValue(s.host, out var a) ? a : from, s.port));
                else
                    result.Add(new Receiver(label, from, LinkProtocol.Port));
            }
        }
        catch (ArgumentOutOfRangeException) { }
        catch (IndexOutOfRangeException) { }
        return result;
    }

    static string ReadName(byte[] p, ref int pos)
    {
        var labels = new List<string>();
        int i = pos, jumps = 0;
        bool jumped = false;
        while (true)
        {
            int len = p[i];
            if (len == 0) { if (!jumped) pos = i + 1; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (++jumps > 20) throw new IndexOutOfRangeException();
                int target = ((len & 0x3F) << 8) | p[i + 1];
                if (!jumped) pos = i + 2;
                jumped = true;
                i = target;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(p, i + 1, len));
            i += len + 1;
        }
        return string.Join('.', labels);
    }

    /// <summary>
    /// Interroge le réseau pendant <paramref name="duration"/> et rend les récepteurs trouvés. La requête part
    /// de chaque interface IPv4 active ; les réponses sont écoutées en unicast (port éphémère) et en
    /// multicast (port 5353, partagé avec le service mDNS de Windows quand c'est possible).
    /// </summary>
    public static async Task<List<Receiver>> DiscoverAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, Receiver>();
        var q = BuildQuery(LinkProtocol.ServiceType);
        var sockets = new List<UdpClient>();
        try
        {
            foreach (var local in LocalAddresses())
            {
                try
                {
                    var u = new UdpClient(AddressFamily.InterNetwork);
                    u.Client.Bind(new IPEndPoint(local, 0));
                    u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    sockets.Add(u);
                }
                catch (SocketException) { }
            }
            try
            {
                var m = new UdpClient(AddressFamily.InterNetwork);
                m.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                m.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
                foreach (var local in LocalAddresses())
                    try { m.JoinMulticastGroup(Group.Address, local); } catch (SocketException) { }
                sockets.Add(m);
            }
            catch (SocketException) { }

            using var stopAt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stopAt.CancelAfter(duration);
            var listeners = sockets.Select(u => Task.Run(async () =>
            {
                try
                {
                    while (!stopAt.IsCancellationRequested)
                    {
                        var r = await u.ReceiveAsync(stopAt.Token);
                        foreach (var rc in Parse(r.Buffer, r.RemoteEndPoint.Address)) found[rc.Address + ":" + rc.Port] = rc;
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
            })).ToList();
            for (int i = 0; i < 3 && !stopAt.IsCancellationRequested; i++)
            {
                foreach (var u in sockets.Take(sockets.Count))
                    try { await u.SendAsync(q, Group, stopAt.Token); } catch (Exception e) when (e is SocketException or OperationCanceledException) { }
                try { await Task.Delay(600, stopAt.Token); } catch (OperationCanceledException) { }
            }
            await Task.WhenAll(listeners);
        }
        finally
        {
            foreach (var u in sockets) u.Dispose();
        }
        return found.Values.OrderBy(r => r.Name).ToList();
    }

    static IEnumerable<IPAddress> LocalAddresses() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && n.SupportsMulticast
                && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .Distinct();
}
