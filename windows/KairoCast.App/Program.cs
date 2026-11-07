using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using KairoCast.App.Diagnostics;
using KairoCast.App.Streaming;
using KairoCast.Link;
using KairoCast.Media;

namespace KairoCast.App;

internal static class Program
{
    private const string Usage =
        "KairoCast.exe            interface (fenêtre et zone de notification) ; --tray : démarrage discret\n" +
        "KairoCast.exe --box <ip>[:port] [--seconds N] [--width W] [--height H] [--fps F] [--kbps K] [--no-audio]\n" +
        "  --box      adresse de la box Kairo (port 7300 par défaut)\n" +
        "  --seconds  durée du partage, 0 = jusqu'à Ctrl+C ou fin de session (défaut 0)\n" +
        "  défauts : 1920x1080, 30 i/s, 8000 kb/s, son activé\n" +
        "Journal : %LOCALAPPDATA%\\KairoCast\\log.txt";

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        var console = AttachConsole(-1);
        Log.ToConsole = console;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Exception non gérée", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Warn($"Tâche non observée : {Log.Describe(e.Exception)}"); e.SetObserved(); };
        Log.Info($"Kairo Cast {typeof(Program).Assembly.GetName().Version} démarré : {Environment.ProcessPath} {string.Join(' ', args)}");

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Show(console, Usage);
            return 0;
        }
        if (args.Length == 0 || args.All(a => a == "--tray"))
            return RunUi(showWindow: args.Length == 0);
        if (!CliOptions.TryParse(args, out var cli, out var error))
        {
            Log.Error($"Arguments invalides : {error}");
            Show(console, error + "\n\n" + Usage);
            return 2;
        }
        try
        {
            return RunAsync(cli!).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Log.Error("Partage impossible", e);
            Show(console, $"Partage impossible : {e.Message}\n\nDétails : {Log.FilePath}");
            return 1;
        }
    }

    private static int RunUi(bool showWindow)
    {
        using var mutex = new Mutex(true, @"Local\KairoCast.SingleInstance", out var first);
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\KairoCast.Show");
        if (!first)
        {
            // Une instance tourne déjà : on la ramène devant.
            signal.Set();
            return 0;
        }
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Ui.TrayApp(showWindow, signal));
            return 0;
        }
        catch (Exception e)
        {
            Log.Error("Application arrêtée", e);
            MessageBox.Show($"Kairo Cast s'est arrêté : {e.Message}\n\nDétails : {Log.FilePath}", "Kairo Cast", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void Show(bool console, string text)
    {
        if (console) Console.WriteLine(text);
        else MessageBox.Show(text, "Kairo Cast", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static async Task<int> RunAsync(CliOptions cli)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Log.Info("Ctrl+C : arrêt demandé"); cts.Cancel(); };

        var address = await ResolveAsync(cli.Host);
        var box = new BoxInfo(cli.Host, address, cli.Port);
        var session = await StreamSession.StartAsync(box, cli.Options, cts.Token);
        var ended = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Ended += e => ended.TrySetResult(e);
        Log.Info($"Encodeur : {(session.UsingHardwareEncoder ? "matériel" : "LOGICIEL")} « {session.EncoderName} », son {(session.AudioActive ? "actif" : "inactif")}");

        try
        {
            var wait = cli.Seconds > 0 ? Task.Delay(TimeSpan.FromSeconds(cli.Seconds), cts.Token) : Task.Delay(Timeout.Infinite, cts.Token);
            await Task.WhenAny(wait, ended.Task);
        }
        finally
        {
            await session.DisposeAsync();
        }
        var cause = ended.Task.IsCompletedSuccessfully ? ended.Task.Result : null;
        Log.Info($"Fin : {session.Status}");
        return cause == null ? 0 : 1;
    }

    private static async Task<IPAddress> ResolveAsync(string host)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var all = await Dns.GetHostAddressesAsync(host);
        return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? all.FirstOrDefault()
               ?? throw new InvalidOperationException($"adresse introuvable pour {host}");
    }
}

internal sealed record CliOptions(string Host, int Port, int Seconds, StreamOptions Options)
{
    public const int DefaultPort = 7300;

    public static bool TryParse(string[] args, out CliOptions? result, out string error)
    {
        result = null;
        error = "";
        string? host = null;
        var port = DefaultPort;
        var seconds = 0;
        var opt = new StreamOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new FormatException($"valeur manquante après {a}");
                return args[++i];
            }
            int NextInt()
            {
                var v = Next();
                return int.TryParse(v, out var n) && n >= 0 ? n : throw new FormatException($"nombre invalide pour {a} : {v}");
            }
            try
            {
                switch (a)
                {
                    case "--box":
                        var v = Next();
                        var colon = v.LastIndexOf(':');
                        if (colon > 0 && !v.Contains("::") && int.TryParse(v[(colon + 1)..], out var p)) { host = v[..colon]; port = p; }
                        else host = v;
                        break;
                    case "--port": port = NextInt(); break;
                    case "--seconds": seconds = NextInt(); break;
                    case "--width": opt = opt with { Width = NextInt() }; break;
                    case "--height": opt = opt with { Height = NextInt() }; break;
                    case "--fps": opt = opt with { Fps = NextInt() }; break;
                    case "--kbps": opt = opt with { BitrateKbps = NextInt() }; break;
                    case "--no-audio": opt = opt with { Audio = false }; break;
                    default: error = $"option inconnue : {a}"; return false;
                }
            }
            catch (FormatException e)
            {
                error = e.Message;
                return false;
            }
        }
        if (string.IsNullOrWhiteSpace(host)) { error = "--box est obligatoire"; return false; }
        if (port is <= 0 or > 65535) { error = $"port invalide : {port}"; return false; }
        result = new CliOptions(host, port, seconds, opt);
        return true;
    }
}
