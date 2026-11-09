using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using KairoCast.Link;

namespace KairoCast.App.Ui;

/// <summary>Règles d'affichage de la liste des box (fonctions pures, testées).</summary>
public static class BoxDisplay
{
    /// <summary>
    /// Vrai si <paramref name="ip"/> est dans l'un des sous-réseaux IPv4 locaux. Sans sous-réseau connu
    /// (détection impossible), on n'écarte rien.
    /// </summary>
    public static bool OnLocalSubnet(IPAddress ip, IReadOnlyCollection<(IPAddress Address, IPAddress Mask)> nets)
    {
        if (nets.Count == 0 || ip.AddressFamily != AddressFamily.InterNetwork) return true;
        var a = ip.GetAddressBytes();
        foreach (var (addr, mask) in nets)
        {
            var b = addr.GetAddressBytes();
            var m = mask.GetAddressBytes();
            if (b.Length != 4 || m.Length != 4) continue;
            bool same = true;
            for (int i = 0; i < 4 && same; i++) same = (a[i] & m[i]) == (b[i] & m[i]);
            if (same) return true;
        }
        return false;
    }

    /// <summary>
    /// Box à montrer : celles ajoutées à la main toujours ; les box découvertes seulement si elles sont
    /// sur un réseau du PC (une box vue sur l'ancien réseau disparaît quand on change de réseau).
    /// </summary>
    public static List<BoxInfo> Visible(IEnumerable<BoxInfo> boxes, Func<BoxInfo, bool> isManual,
        IReadOnlyCollection<(IPAddress Address, IPAddress Mask)> nets) =>
        boxes.Where(b => isManual(b) || OnLocalSubnet(b.Address, nets)).ToList();

    /// <summary>Nom affiché : l'adresse est ajoutée quand plusieurs box visibles portent le même nom.</summary>
    public static string Label(BoxInfo box, IReadOnlyCollection<BoxInfo> visible)
    {
        bool twin = visible.Count(b => string.Equals(b.Name, box.Name, StringComparison.OrdinalIgnoreCase)) > 1;
        bool nameIsAddress = box.Name == box.Address.ToString();
        return twin && !nameIsAddress ? $"{box.Name}  ·  {box.Address}" : box.Name;
    }

    /// <summary>Sous-réseaux IPv4 des interfaces actives du PC.</summary>
    public static List<(IPAddress, IPAddress)> LocalNetworks()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null)
                .Select(u => (u.Address, u.IPv4Mask))
                .ToList();
        }
        catch (NetworkInformationException) { return new(); }
    }
}
