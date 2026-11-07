namespace KairoCast.App.Shell;

/// <summary>
/// Gère une fin de session survenue avant que le démarrage ne rende la session (fin « précoce »).
/// Une fin normale, après le démarrage, ne marque jamais le partage suivant.
/// </summary>
public sealed class EarlyEndGate<T> where T : class
{
    private T? _self;
    private bool _endedEarly;
    private Exception? _earlyEnd;

    /// <summary>Session de ce partage, une fois démarrée.</summary>
    public T? Session => _self;

    /// <summary>À appeler au début de chaque partage.</summary>
    public void Reset() { _self = null; _endedEarly = false; _earlyEnd = null; }

    /// <summary>Appelé à chaque fin de session (sur le fil de l'interface).</summary>
    public void Ended(Exception? e, Action<T, Exception?> onEnded)
    {
        var self = _self;
        if (self != null) onEnded(self, e);
        else { _earlyEnd = e; _endedEarly = true; }
    }

    /// <summary>La session est démarrée ; renvoie true si elle s'est déjà terminée entre-temps.</summary>
    public bool Started(T session, out Exception? earlyEnd)
    {
        _self = session;
        earlyEnd = _earlyEnd;
        var early = _endedEarly;
        _endedEarly = false;
        _earlyEnd = null;
        return early;
    }
}
