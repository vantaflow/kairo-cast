using System.Text.Json;
using System.Text.Json.Serialization;

namespace KairoCast.Link;

public sealed record Hello(
    [property: JsonRequired, JsonPropertyName("v")] int V,
    [property: JsonRequired, JsonPropertyName("name")] string Name,
    [property: JsonRequired, JsonPropertyName("w")] int W,
    [property: JsonRequired, JsonPropertyName("h")] int H,
    [property: JsonRequired, JsonPropertyName("fps")] int Fps,
    [property: JsonRequired, JsonPropertyName("audio")] string Audio)
{
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static Hello Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var h = JsonSerializer.Deserialize<Hello>(bytes) ?? throw new LinkProtocolException("HELLO vide");
            if (h.Name == null || h.Audio == null) throw new LinkProtocolException("HELLO incomplet");
            return h;
        }
        catch (JsonException e)
        {
            throw new LinkProtocolException("HELLO invalide", e);
        }
    }
}
