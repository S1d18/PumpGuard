using System.Diagnostics;
using Microsoft.Extensions.Options;
using PumpGuard.Core;

namespace PumpGuard.Service;

public interface IShutdownExecutor
{
    /// <summary>Returns false when the shutdown could not be started, so the caller can retry.</summary>
    bool Shutdown(string reason);
}

public sealed class WindowsShutdownExecutor(IOptions<PumpGuardOptions> options, ILogger<WindowsShutdownExecutor> log) : IShutdownExecutor
{
    public bool Shutdown(string reason)
    {
        if (options.Value.DryRun)
        {
            log.LogCritical("[DRY RUN] Здесь ПК был бы выключен: {Reason}", reason);
            return true;
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
            // shutdown.exe returns at once; a non-zero code means Windows refused (e.g. 1190: already pending is fine).
            if (p is null || !p.WaitForExit(10_000)) return p is not null;
            if (p.ExitCode is 0 or 1190) return true;
            log.LogCritical("shutdown.exe завершился с кодом {Code}, повторю через 30 с", p.ExitCode);
            return false;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Не удалось запустить shutdown.exe, повторю через 30 с");
            return false;
        }
    }
}
