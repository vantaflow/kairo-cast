using KairoCast.Link;

// Diagnostic : liste les box Kairo trouvées pendant N secondes (défaut 8).
int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 8;
using var d = new Discovery();
d.Found += b => Console.WriteLine($"FOUND {b.Name} {b.Address}:{b.Port}");
d.Lost += b => Console.WriteLine($"LOST  {b.Name} {b.Address}:{b.Port}");
d.Start();
await Task.Delay(TimeSpan.FromSeconds(seconds));
