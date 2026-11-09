using System.Reflection;

namespace KairoCast;

/// <summary>ffmpeg (LGPL) : à côté de l'exe s'il y est, sinon extrait une fois depuis l'exe dans %LOCALAPPDATA%\KairoCast.</summary>
static class FfmpegLocator
{
    const string Resource = "KairoCast.ffmpeg.exe";

    public static string Path()
    {
        var beside = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside)) return beside;
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
            ?? throw new FileNotFoundException("ffmpeg.exe introuvable : placez-le à côté de KairoCast.exe.");
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KairoCast");
        Directory.CreateDirectory(dir);
        var target = System.IO.Path.Combine(dir, $"ffmpeg-{res.Length}.exe");
        if (File.Exists(target) && new FileInfo(target).Length == res.Length) return target;
        var tmp = target + ".tmp";
        using (var f = File.Create(tmp)) res.CopyTo(f);
        File.Move(tmp, target, overwrite: true);
        return target;
    }
}
