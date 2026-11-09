using System.Buffers.Binary;
using System.Globalization;

namespace KairoCast.Core;

public static class Pcm
{
    /// <summary>Échantillons float 32 bits (petit-boutiste) vers PCM 16 bits signé.</summary>
    public static byte[] FloatToS16(ReadOnlySpan<byte> f32)
    {
        int n = f32.Length / 4;
        var o = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            float v = BinaryPrimitives.ReadSingleLittleEndian(f32[(i * 4)..]);
            if (float.IsNaN(v)) v = 0;
            v = Math.Clamp(v, -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(o.AsSpan(i * 2), (short)(v * 32767f));
        }
        return o;
    }

    /// <summary>PCM 16 bits entrelacé à n canaux (5.1, 7.1…) : ne garde que avant gauche / avant droit.</summary>
    public static byte[] ToStereo(byte[] s16, int channels)
    {
        if (channels <= 2) return s16;
        int frames = s16.Length / (2 * channels);
        var o = new byte[frames * 4];
        for (int f = 0; f < frames; f++)
            Buffer.BlockCopy(s16, f * channels * 2, o, f * 4, 4);
        return o;
    }
}

public static class Ffmpeg
{
    /// <summary>Encodeurs essayés dans l'ordre : matériels (NVIDIA, Intel, AMD), puis Media Foundation.</summary>
    public static readonly string[] Encoders = { "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf" };

    /// <summary>Taille de sortie : celle de l'écran, réduite pour tenir dans le maximum de la box, paire.</summary>
    public static (int, int) Fit(int srcW, int srcH, int maxW, int maxH)
    {
        double s = Math.Min(1.0, Math.Min((double)maxW / srcW, (double)maxH / srcH));
        int w = (int)Math.Round(srcW * s), h = (int)Math.Round(srcH * s);
        return (w & ~1, h & ~1);
    }

    /// <summary>Débit en kbit/s : 12 Mbit/s en 1080p60, borné entre 4 et 30 Mbit/s.</summary>
    public static int Bitrate(int w, int h, int fps)
    {
        long k = (long)w * h * fps / 10368;
        return (int)Math.Clamp((k + 500) / 1000 * 1000, 4000, 30000);
    }

    static string EncoderOptions(string encoder) => encoder switch
    {
        "h264_nvenc" => "-preset p1 -tune ull -zerolatency 1 -rc cbr",
        "h264_qsv" => "-preset veryfast -low_power 0 -async_depth 1",
        "h264_amf" => "-usage ultralowlatency -quality speed -rc cbr",
        _ => "-scenario display_remoting -rate_control cbr -hw_encoding 0",
    };

    public static string Arguments(string encoder, int output, int width, int height, int fps, int bitrateKbps)
    {
        var inv = CultureInfo.InvariantCulture;
        string b = bitrateKbps.ToString(inv);
        // ddagrab sort des images D3D11 ; hwdownload + format pour les encodeurs qui veulent de la mémoire système.
        string scale = encoder is "h264_nvenc" or "h264_amf" or "h264_qsv" or "h264_mf"
            ? $"hwdownload,format=bgra,scale={width}:{height}:flags=bicubic,format=nv12"
            : $"hwdownload,format=bgra,scale={width}:{height},format=nv12";
        return string.Join(' ',
            "-hide_banner -loglevel error -nostdin",
            $"-f lavfi -i ddagrab=output_idx={output}:framerate={fps}:draw_mouse=1",
            $"-vf {scale}",
            $"-c:v {encoder} {EncoderOptions(encoder)}",
            $"-b:v {b}k -maxrate {b}k -bufsize {bitrateKbps / 2}k -g {fps * 2} -bf 0 -profile:v high",
            "-bsf:v h264_metadata=aud=insert",
            "-flush_packets 1 -f h264 -");
    }
}
