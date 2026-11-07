using System.Text.Json;
using System.Text.Json.Serialization;

namespace KairoCast.Link;

public sealed record Hello(
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("w")] int W,
    [property: JsonPropertyName("h")] int H,
    [property: JsonPropertyName("fps")] int Fps,
    [property: JsonPropertyName("audio")] string Audio)
{
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static Hello Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<Hello>(bytes) ?? throw new LinkProtocolException("HELLO vide");
        }
        catch (JsonException e)
        {
            throw new LinkProtocolException("HELLO invalide", e);
        }
    }
}
