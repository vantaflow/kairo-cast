using System.Runtime.InteropServices;
using KairoCast.App.Diagnostics;

namespace KairoCast.App.Shell;

/// <summary>
/// Crochet clavier bas niveau (WH_KEYBOARD_LL), à créer sur le fil de l'interface (qui a une boucle de messages).
/// Win+K ouvre le panneau ; la décision est dans <see cref="HotkeyFilter"/>.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const uint KEYEVENTF_KEYUP = 2;
    private const uint INPUT_KEYBOARD = 1;
    private static readonly nint Magic = 0x4B434B43; // « KCKC » : nos propres événements injectés

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);

    private readonly HookProc _proc; // gardé en champ : le ramasse-miettes ne doit pas le récupérer
    private readonly SynchronizationContext _ui;
    private readonly HotkeyFilter _filter = new();
    private nint _hook;
    private bool _winTracked;
    private volatile bool _replaceWinK = true;

    /// <summary>Le panneau est demandé (sur le fil de l'interface).</summary>
    public event Action? PanelRequested;

    public KeyboardHook(SynchronizationContext ui)
    {
        _ui = ui;
        _proc = Proc;
    }

    public bool ReplaceWinK { get => _replaceWinK; set => _replaceWinK = value; }
    public bool Installed => _hook != 0;

    public bool Install()
    {
        if (_hook != 0) return true;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == 0) Log.Warn($"Crochet clavier : installation impossible (erreur {Marshal.GetLastWin32Error()})");
        else Log.Info("Crochet clavier installé");
        return _hook != 0;
    }

    private nint Proc(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (k.dwExtraInfo != Magic)
            {
                var msg = (int)wParam;
                var up = msg is WM_KEYUP or WM_SYSKEYUP;
                var vk = (int)k.vkCode;
                if (vk is HotkeyFilter.VkLWin or HotkeyFilter.VkRWin) _winTracked = !up;
                var win = _winTracked || (GetAsyncKeyState(HotkeyFilter.VkLWin) & 0x8000) != 0 || (GetAsyncKeyState(HotkeyFilter.VkRWin) & 0x8000) != 0;
                var alt = (k.flags & 0x20) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0;
                switch (_filter.Decide(vk, up, win, alt, _replaceWinK))
                {
                    case HotkeyDecision.Swallow:
                        return 1;
                    case HotkeyDecision.OpenPanel:
                        _ui.Post(_ => PanelRequested?.Invoke(), null);
                        return 1;
                    case HotkeyDecision.ReplayWinUp:
                        // Touche factice entre l'appui et le relâchement de Win : le menu Démarrer ne s'ouvre pas.
                        Send(Key(HotkeyFilter.VkDummy, false), Key(HotkeyFilter.VkDummy, true), Key(vk, true));
                        return 1;
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn("Crochet clavier : " + e.Message);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>« Autres écrans (Windows)… » : envoie un Win+K que le crochet laisse passer une fois. Faux si l'envoi échoue.</summary>
    public bool SendNativeWinK()
    {
        _filter.ArmPassThrough();
        var sent = Send(Key(HotkeyFilter.VkLWin, false, magic: false), Key(HotkeyFilter.VkK, false, magic: false),
                        Key(HotkeyFilter.VkK, true, magic: false), Key(HotkeyFilter.VkLWin, true, magic: false));
        if (!sent) _filter.DisarmPassThrough();
        else
        {
            // Sécurité : si l'événement n'est jamais arrivé, on ne garde pas l'exception armée.
            _ = Task.Delay(1500).ContinueWith(_ => _filter.DisarmPassThrough());
        }
        return sent;
    }

    private static INPUT Key(int vk, bool up, bool magic = true) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? KEYEVENTF_KEYUP : 0, dwExtraInfo = magic ? Magic : 0 } },
    };

    private static bool Send(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;

    public void Dispose()
    {
        if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
    }
}
