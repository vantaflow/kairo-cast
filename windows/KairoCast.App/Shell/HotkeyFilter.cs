namespace KairoCast.App.Shell;

public enum HotkeyDecision
{
    /// <summary>Laisser l'événement au système.</summary>
    Pass,
    /// <summary>Avaler l'événement.</summary>
    Swallow,
    /// <summary>Avaler l'événement et ouvrir le panneau.</summary>
    OpenPanel,
    /// <summary>Touche Windows relâchée après un raccourci avalé : avaler, puis rejouer une touche factice puis le relâchement,
    /// pour que le menu Démarrer ne s'ouvre pas.</summary>
    ReplayWinUp,
}

/// <summary>
/// Logique pure du crochet clavier : Win+K ouvre le panneau (ou Win+Alt+K si « Remplacer Win+K » est désactivé),
/// un Win+K synthétique est laissé passer une fois (« Autres écrans (Windows)… »).
/// Garde un petit état : K avalé (pour avaler aussi sa répétition et son relâchement) et Win « utilisée ».
/// </summary>
public sealed class HotkeyFilter
{
    public const int VkK = 0x4B;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;
    /// <summary>Touche factice (non attribuée) injectée avant le relâchement de Win.</summary>
    public const int VkDummy = 0xE8;

    private bool _passOnce;
    private bool _kSwallowed;
    private bool _winUsed;

    public bool PassThroughArmed => _passOnce;
    public void ArmPassThrough() => _passOnce = true;
    public void DisarmPassThrough() => _passOnce = false;

    public HotkeyDecision Decide(int vk, bool isKeyUp, bool winDown, bool altDown, bool replaceWinK)
    {
        if (vk == VkK)
        {
            if (isKeyUp)
            {
                if (!_kSwallowed) return HotkeyDecision.Pass;
                _kSwallowed = false;
                return HotkeyDecision.Swallow;
            }
            if (_kSwallowed) return HotkeyDecision.Swallow; // répétition automatique
            if (!winDown) return HotkeyDecision.Pass;
            if (_passOnce)
            {
                _passOnce = false;
                return HotkeyDecision.Pass;
            }
            var ours = replaceWinK ? !altDown : altDown;
            if (!ours) return HotkeyDecision.Pass;
            _kSwallowed = true;
            _winUsed = true;
            return HotkeyDecision.OpenPanel;
        }
        if ((vk == VkLWin || vk == VkRWin) && isKeyUp && _winUsed)
        {
            _winUsed = false;
            return HotkeyDecision.ReplayWinUp;
        }
        return HotkeyDecision.Pass;
    }
}
