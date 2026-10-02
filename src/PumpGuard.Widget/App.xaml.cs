using System.IO;
using System.Windows;

namespace PumpGuard.Widget;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (Array.IndexOf(e.Args, "--settings") is var i and >= 0 && i + 1 < e.Args.Length)
            WidgetSettings.FilePath = Path.GetFullPath(e.Args[i + 1]);
        // One widget per settings file: a test instance with its own --settings runs beside the user's widget.
        var name = WidgetSettings.IsDefaultFile ? "PumpGuardWidget" : $"PumpGuardWidget-{(uint)WidgetSettings.FilePath.ToLowerInvariant().GetHashCode():x8}";
        _single = new Mutex(true, $@"Local\{name}", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
        var main = new MainWindow();
        main.Show();
        if (e.Args.Contains("--fans")) main.OpenFans();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
