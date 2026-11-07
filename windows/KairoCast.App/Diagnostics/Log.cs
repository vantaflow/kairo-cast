using System.Diagnostics;
using System.Text;
using SharpGen.Runtime;

namespace KairoCast.App.Diagnostics;

/// <summary>
/// Journal fichier simple : %LOCALAPPDATA%\KairoCast\log.txt (+ sortie Debug, + console si attachée).
/// C'est ce fichier qu'on lit pendant le test réel : il doit dire ce qui a été choisi et pourquoi ça a échoué.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly object Lock = new();
    private static StreamWriter? _writer;
    private static bool _opened;

    public static bool ToConsole { get; set; }

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KairoCast", "log.txt");

    public static void Info(string msg) => Write("INFO ", msg);

    public static void Warn(string msg) => Write("WARN ", msg);

    public static void Error(string msg, Exception? e = null) =>
        Write("ERROR", e == null ? msg : $"{msg} : {Describe(e)}");

    /// <summary>« 0x887A0005 (DXGI_ERROR_DEVICE_REMOVED) message » : HRESULT toujours en hexadécimal.</summary>
    public static string Describe(Exception e)
    {
        var sb = new StringBuilder();
        sb.Append(e.GetType().Name).Append(' ').Append(Hr(e)).Append(' ').Append(e.Message.Trim());
        if (e.InnerException != null) sb.Append(" <- ").Append(Describe(e.InnerException));
        return sb.ToString();
    }

    public static string Hr(Exception e) => e is SharpGenException sg
        ? $"hr=0x{sg.ResultCode.Code:X8}"
        : $"hr=0x{e.HResult:X8}";

    public static string Hr(int hr) => $"0x{hr:X8}";

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {level} {msg}";
        Debug.WriteLine(line);
        lock (Lock)
        {
            if (ToConsole) { try { Console.WriteLine(line); } catch { } }
            try
            {
                if (!_opened) Open();
                _writer?.WriteLine(line);
            }
            catch { /* le journal ne doit jamais faire tomber l'application */ }
        }
    }

    private static void Open()
    {
        _opened = true;
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        try
        {
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > MaxBytes)
                File.Move(FilePath, Path.Combine(dir, "log.old.txt"), overwrite: true);
        }
        catch { }
        var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
    }
}
