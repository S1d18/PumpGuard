using System.Diagnostics;
using Microsoft.Extensions.Options;
using PumpGuard.Core;

namespace PumpGuard.Service;

public interface IShutdownExecutor
{
    void Shutdown(string reason);
}

public sealed class WindowsShutdownExecutor(IOptions<PumpGuardOptions> options, ILogger<WindowsShutdownExecutor> log) : IShutdownExecutor
{
    public void Shutdown(string reason)
    {
        if (options.Value.DryRun)
        {
            log.LogCritical("[DRY RUN] Здесь ПК был бы выключен: {Reason}", reason);
            return;
        }

        log.LogCritical("АВАРИЙНОЕ ВЫКЛЮЧЕНИЕ: {Reason}", reason);
        var comment = $"PumpGuard: {reason}";
        if (comment.Length > 500) comment = comment[..500];
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
            {
                ArgumentList = { "/s", "/f", "/t", "0", "/c", comment },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Не удалось запустить shutdown.exe");
        }
    }
}
