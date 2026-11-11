using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace KairoCast.Link;

/// <summary>
/// État d'un récepteur (une source de la box) : libre, ou occupé par un Mac/iPhone (AirPlay) ou un PC (Kairo Link).
/// Obtenu par une trame STATUS, sans toucher à la recopie en cours. Une box trop ancienne ne répond pas : null.
/// </summary>
public sealed record BoxStatus(bool Busy, string Source, string By)
{
    /// <summary>Texte affiché à côté du nom : « libre », « occupée par PC Bureau », « occupée (Mac ou iPhone) ».</summary>
    public string Describe() => !Busy ? "libre"
        : Source == "link" && By.Length > 0 ? $"occupée par {By}"
        : Source == "airplay" ? "occupée (Mac ou iPhone)"
        : "occupée";

    public static BoxStatus? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("busy", out var busy)
                || busy.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            static string Str(JsonElement o, string name) =>
                o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return new BoxStatus(busy.GetBoolean(), Str(r, "source"), Str(r, "by"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Demande l'état à la box ; null si elle ne répond pas ou ne connaît pas la question (version ancienne).</summary>
    public static async Task<BoxStatus?> QueryAsync(IPEndPoint ep, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(ep, cts.Token);
            var s = tcp.GetStream();
            await s.WriteAsync(LinkCodec.Encode(LinkType.Status, 0, 0, ReadOnlySpan<byte>.Empty), cts.Token);
            while (true)
            {
                var f = await LinkCodec.ReadAsync(s, cts.Token);
                if (f == null) return null;
                if (f.Type == LinkType.Status) return Parse(f.Payload);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or IOException or LinkProtocolException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }
}
