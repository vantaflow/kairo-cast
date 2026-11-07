using System.Net;
using System.Net.Sockets;

namespace KairoCast.App.Shell;

/// <summary>Saisie manuelle « 192.168.1.20 » ou « 192.168.1.20:7300 ».</summary>
public static class ManualAddress
{
    public const int DefaultPort = 7300;

    public static bool TryParse(string? text, out IPAddress address, out int port)
    {
        address = IPAddress.None;
        port = DefaultPort;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        var host = text;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], out port) || port is <= 0 or > 65535) return false;
        }
        if (host.Count(c => c == '.') != 3 || !IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        address = ip;
        return true;
    }
}
