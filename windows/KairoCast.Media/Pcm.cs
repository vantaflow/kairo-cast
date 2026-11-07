using System.Buffers.Binary;

namespace KairoCast.Media;

public enum PcmEncoding { Int16, Int24, Int32, Float32 }

/// <summary>Format du son capturé. <paramref name="ChannelMask"/> : masque WAVEFORMATEXTENSIBLE (0 = ordre standard).</summary>
public sealed record PcmInputFormat(int SampleRate, int Channels, PcmEncoding Encoding, int ChannelMask = 0)
{
    public int BytesPerSample => Encoding switch
    {
        PcmEncoding.Int16 => 2,
        PcmEncoding.Int24 => 3,
        _ => 4,
    };

    public int BlockAlign => BytesPerSample * Channels;

    public override string ToString() => $"{SampleRate} Hz, {Channels} canaux, {Encoding}, masque 0x{ChannelMask:X}";
}

/// <summary>
/// Convertit le son capturé (format du mélangeur Windows) en PCM s16le 48 kHz stéréo entrelacé :
/// décodage, réduction des canaux (5.1/7.1 → stéréo), rééchantillonnage linéaire si besoin.
/// Garde l'état entre deux appels (octets incomplets, phase du rééchantillonneur). Non thread-safe.
/// </summary>
public sealed class PcmConverter
{
    public const int OutputRate = 48000;
    private const float Minus3dB = 0.70710678f;

    private readonly PcmInputFormat _fmt;
    private readonly float[] _gainL;
    private readonly float[] _gainR;
    private readonly double _step;
    private readonly byte[] _carry;
    private int _carryLen;

    // Rééchantillonneur : dernière image d'entrée de l'appel précédent et position fractionnaire.
    private bool _havePrev;
    private float _prevL, _prevR;
    private double _pos;

    public PcmConverter(PcmInputFormat fmt)
    {
        if (fmt.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(fmt), "fréquence invalide");
        if (fmt.Channels <= 0 || fmt.Channels > 32) throw new ArgumentOutOfRangeException(nameof(fmt), "nombre de canaux invalide");
        _fmt = fmt;
        (_gainL, _gainR) = BuildDownmix(fmt.Channels, fmt.ChannelMask);
        _step = (double)fmt.SampleRate / OutputRate;
        _carry = new byte[fmt.BlockAlign];
    }

    public bool Resampling => _fmt.SampleRate != OutputRate;

    /// <summary>Convertit un bloc ; renvoie des octets s16le stéréo 48 kHz (éventuellement vide).</summary>
    public byte[] Convert(ReadOnlySpan<byte> input)
    {
        var block = _fmt.BlockAlign;
        // Reconstitue les images à cheval sur deux appels.
        var total = _carryLen + input.Length;
        var frames = total / block;
        var stereo = new float[frames * 2];
        var f = 0;
        var inPos = 0;
        if (_carryLen > 0 && frames > 0)
        {
            var need = block - _carryLen;
            var tmp = new byte[block];
            _carry.AsSpan(0, _carryLen).CopyTo(tmp);
            input[..need].CopyTo(tmp.AsSpan(_carryLen));
            DecodeFrame(tmp, stereo, f++);
            inPos = need;
            _carryLen = 0;
        }
        for (; f < frames; f++, inPos += block) DecodeFrame(input.Slice(inPos, block), stereo, f);
        var rest = input.Length - inPos;
        if (rest > 0)
        {
            input[inPos..].CopyTo(_carry.AsSpan(_carryLen));
            _carryLen += rest;
        }

        var outStereo = Resampling ? Resample(stereo, frames) : stereo;
        var outp = new byte[outStereo.Length * 2];
        for (var i = 0; i < outStereo.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(outp.AsSpan(i * 2), ToS16(outStereo[i]));
        return outp;
    }

    private static short ToS16(float v)
    {
        var s = Math.Round(v * 32768.0);
        if (s > short.MaxValue) return short.MaxValue;
        if (s < short.MinValue) return short.MinValue;
        return (short)s;
    }

    private void DecodeFrame(ReadOnlySpan<byte> frame, float[] stereo, int index)
    {
        float l = 0, r = 0;
        var bps = _fmt.BytesPerSample;
        for (var c = 0; c < _fmt.Channels; c++)
        {
            var v = ReadSample(frame.Slice(c * bps, bps));
            l += v * _gainL[c];
            r += v * _gainR[c];
        }
        stereo[index * 2] = l;
        stereo[index * 2 + 1] = r;
    }

    private float ReadSample(ReadOnlySpan<byte> s) => _fmt.Encoding switch
    {
        PcmEncoding.Int16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
        PcmEncoding.Int24 => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
        PcmEncoding.Int32 => (float)(BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648.0),
        _ => SanitizeFloat(BinaryPrimitives.ReadSingleLittleEndian(s)),
    };

    private static float SanitizeFloat(float v) => float.IsFinite(v) ? v : 0f;

    private float[] Resample(float[] stereo, int frames)
    {
        // Tampon logique : [prev] + images de cet appel.
        var offset = _havePrev ? 1 : 0;
        var count = frames + offset;
        if (count < 2)
        {
            if (frames == 1) { _prevL = stereo[0]; _prevR = stereo[1]; _havePrev = true; }
            return Array.Empty<float>();
        }
        float L(int i) => i < offset ? _prevL : stereo[(i - offset) * 2];
        float R(int i) => i < offset ? _prevR : stereo[(i - offset) * 2 + 1];

        var outp = new List<float>((int)(frames / _step) * 2 + 4);
        while (true)
        {
            var i = (int)_pos;
            if (i + 1 >= count) break;
            var t = (float)(_pos - i);
            outp.Add(L(i) + (L(i + 1) - L(i)) * t);
            outp.Add(R(i) + (R(i + 1) - R(i)) * t);
            _pos += _step;
        }
        _prevL = L(count - 1);
        _prevR = R(count - 1);
        _havePrev = true;
        _pos -= count - 1;
        return outp.ToArray();
    }

    private static (float[] l, float[] r) BuildDownmix(int channels, int mask)
    {
        var l = new float[channels];
        var r = new float[channels];
        if (channels == 1)
        {
            l[0] = r[0] = 1f;
            return (l, r);
        }
        if (mask == 0) mask = DefaultMask(channels);
        var c = 0;
        for (var bit = 0; bit < 32 && c < channels; bit++)
        {
            if ((mask & (1 << bit)) == 0) continue;
            (l[c], r[c]) = (1 << bit) switch
            {
                0x1 => (1f, 0f),            // avant gauche
                0x2 => (0f, 1f),            // avant droite
                0x4 => (Minus3dB, Minus3dB),// centre
                0x8 => (0f, 0f),            // basses (ignorées)
                0x10 => (Minus3dB, 0f),     // arrière gauche
                0x20 => (0f, Minus3dB),     // arrière droite
                0x40 => (1f, 0f),           // avant centre gauche
                0x80 => (0f, 1f),           // avant centre droite
                0x100 => (0.5f, 0.5f),      // arrière centre
                0x200 => (Minus3dB, 0f),    // côté gauche
                0x400 => (0f, Minus3dB),    // côté droit
                _ => (0f, 0f),
            };
            c++;
        }
        // Canaux sans bit dans le masque : ignorés. Normalisation pour ne jamais écrêter.
        var norm = Math.Max(l.Sum(), r.Sum());
        if (norm > 1f)
            for (var i = 0; i < channels; i++) { l[i] /= norm; r[i] /= norm; }
        return (l, r);
    }

    private static int DefaultMask(int channels) => channels switch
    {
        2 => 0x3,
        3 => 0x7,
        4 => 0x33,
        5 => 0x37,
        6 => 0x3F,
        7 => 0x13F,
        8 => 0x63F,
        _ => (int)((1L << Math.Min(channels, 31)) - 1),
    };
}

/// <summary>
/// Découpe le PCM s16le stéréo 48 kHz en blocs de taille fixe et les date sur l'horloge commune :
/// le dernier échantillon reçu est daté de l'instant de réception. Les dates ne reculent jamais.
/// </summary>
public sealed class PcmChunker
{
    private const int BytesPerFrame = 4;
    private readonly int _chunkFrames;
    private readonly byte[] _buf;
    private int _len;
    private long _lastPts = long.MinValue;

    public PcmChunker(int chunkFrames = 480)
    {
        if (chunkFrames <= 0) throw new ArgumentOutOfRangeException(nameof(chunkFrames));
        _chunkFrames = chunkFrames;
        _buf = new byte[chunkFrames * BytesPerFrame];
    }

    public int PendingFrames => _len / BytesPerFrame;

    public long ChunkDurationUs => _chunkFrames * 1_000_000L / PcmConverter.OutputRate;

    public void Push(ReadOnlySpan<byte> pcm, long nowUs, Action<ReadOnlyMemory<byte>, long> emit)
    {
        // Date du premier octet en attente = maintenant - durée de tout ce qui est en attente après ajout.
        var totalFrames = (_len + pcm.Length) / BytesPerFrame;
        var startUs = nowUs - totalFrames * 1_000_000L / PcmConverter.OutputRate;
        var emitted = 0;
        while (pcm.Length > 0)
        {
            var n = Math.Min(pcm.Length, _buf.Length - _len);
            pcm[..n].CopyTo(_buf.AsSpan(_len));
            _len += n;
            pcm = pcm[n..];
            if (_len < _buf.Length) break;
            var pts = startUs + (long)emitted * _chunkFrames * 1_000_000L / PcmConverter.OutputRate;
            if (_lastPts != long.MinValue && pts < _lastPts + ChunkDurationUs) pts = _lastPts + ChunkDurationUs;
            _lastPts = pts;
            emit(_buf.ToArray(), pts);
            emitted++;
            _len = 0;
        }
    }
}
