namespace KairoCast.Media;

/// <summary>
/// Gère un petit anneau de textures NV12 partagées entre la capture (écrit) et l'encodeur (lit).
/// Une seule image en attente : une nouvelle image remplace celle que l'encodeur n'a pas encore prise
/// (on perd des images capturées, jamais des images encodées). Une texture détenue par l'encodeur,
/// en cours d'écriture, en attente ou la dernière image (pour la répétition) n'est jamais réécrite.
/// Thread-safe.
/// </summary>
public sealed class FrameSlots
{
    private readonly object _lock = new();
    private readonly int[] _refs;
    private readonly bool[] _writing;
    private int _pending = -1;
    private long _pendingPts;
    private int _last = -1;

    public FrameSlots(int count)
    {
        if (count < 2) throw new ArgumentOutOfRangeException(nameof(count));
        _refs = new int[count];
        _writing = new bool[count];
    }

    public int Count => _refs.Length;

    /// <summary>Images en attente remplacées avant d'avoir été encodées.</summary>
    public long Replaced { get; private set; }

    public int RefCount(int slot) { lock (_lock) return _refs[slot]; }

    /// <summary>Réserve une texture libre pour l'écriture, ou -1 si aucune n'est libre.</summary>
    public int TryAcquireWrite()
    {
        lock (_lock)
        {
            for (var i = 0; i < _refs.Length; i++)
            {
                if (_refs[i] == 0 && !_writing[i] && i != _pending && i != _last)
                {
                    _writing[i] = true;
                    return i;
                }
            }
            return -1;
        }
    }

    public void CancelWrite(int slot)
    {
        lock (_lock) _writing[slot] = false;
    }

    /// <summary>Publie une texture écrite : elle devient l'image en attente et la dernière image.</summary>
    public void Publish(int slot, long ptsUs)
    {
        lock (_lock)
        {
            _writing[slot] = false;
            if (_pending >= 0 && _pending != slot) Replaced++;
            _pending = slot;
            _pendingPts = ptsUs;
            _last = slot;
        }
    }

    /// <summary>Prend l'image en attente (l'encodeur la détient jusqu'à <see cref="Release"/>).</summary>
    public bool TryTakePending(out int slot, out long ptsUs)
    {
        lock (_lock)
        {
            slot = _pending;
            ptsUs = _pendingPts;
            if (slot < 0) return false;
            _pending = -1;
            _refs[slot]++;
            return true;
        }
    }

    /// <summary>Reprend la dernière image (écran immobile, demande d'image clé).</summary>
    public bool TryTakeLastForRepeat(out int slot)
    {
        lock (_lock)
        {
            slot = _last;
            if (slot < 0) return false;
            _refs[slot]++;
            return true;
        }
    }

    public void Release(int slot)
    {
        lock (_lock)
        {
            if (_refs[slot] <= 0) throw new InvalidOperationException($"texture {slot} non détenue");
            _refs[slot]--;
        }
    }
}
