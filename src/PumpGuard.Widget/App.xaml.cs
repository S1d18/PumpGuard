using System.Windows;

namespace PumpGuard.Widget;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, @"Local\PumpGuardWidget", out var first);
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
