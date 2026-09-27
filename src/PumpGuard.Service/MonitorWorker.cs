using Microsoft.Extensions.Options;
using PumpGuard.Core;
using PumpGuard.Service.Hardware;

namespace PumpGuard.Service;

/// <summary>The safety loop: read sensors → decide → drive fans → publish → shut down if required.</summary>
public sealed class MonitorWorker(
    IEnumerable<ISensorSource> sources,
    IFanControl fanControl,
    SafetyMonitor monitor,
    FanController fans,
    StatusHub hub,
    Simulation simulation,
    IShutdownExecutor shutdown,
    IOptions<PumpGuardOptions> options,
    ILogger<MonitorWorker> log) : BackgroundService
{
    private GuardState _lastState = GuardState.Normal;
    private HashSet<string> _lastIssues = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        log.LogWarning("PumpGuard запущен. Помпа: {Pump}, правил температуры: {Rules}, управление вентиляторами: {Fans}, DryRun: {DryRun}",
            string.IsNullOrWhiteSpace(o.Pump.SensorId) ? "НЕ НАСТРОЕНА" : o.Pump.SensorId, o.Temperatures.Count, fans.ControlEnabled, o.DryRun);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(250, o.PollIntervalMs)));
        try
        {
            do Tick(o);
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        finally
        {
            fanControl.RestoreDefaults();
            log.LogWarning("PumpGuard остановлен, вентиляторы возвращены под управление BIOS");
        }
    }

    private void Tick(PumpGuardOptions o)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var sensors = new List<SensorReading>();
            foreach (var source in sources)
            {
                try { sensors.AddRange(source.Read()); }
                catch (Exception ex) { log.LogError(ex, "Ошибка чтения датчиков {Source}", source.GetType().Name); }
            }

            var readings = new Dictionary<string, double?>();
            foreach (var s in sensors) readings.TryAdd(s.Id, s.Value);
            if (o.DryRun && simulation.PumpFailureUntil > now && o.Pump.SensorId is { } pumpId)
                readings[pumpId] = 0;

            var eval = monitor.Evaluate(readings, now);
            var status = eval.Status;
            try
            {
                var fanResult = fans.Update(readings, eval.Status, now);
                fanControl.Apply(fanResult.Decisions);
                status = status.WithFans(fanResult, fans.ControlEnabled);
            }
            catch (Exception ex) { log.LogError(ex, "Ошибка управления вентиляторами"); }

            hub.Publish(status, sensors);

            if (eval.ExecuteShutdown)
                shutdown.Shutdown(status.ShutdownReason ?? "перегрев");

            LogChanges(status);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ошибка цикла мониторинга");
        }
    }

    private void LogChanges(GuardStatus status)
    {
        // Keyed by source+severity: messages carry live values and would otherwise log every tick.
        var issues = new Dictionary<string, string>();
        foreach (var i in status.Issues) issues.TryAdd($"{i.Source} [{i.Severity}]", i.Message);
        foreach (var (key, message) in issues.Where(i => !_lastIssues.Contains(i.Key)))
            log.LogWarning("Проблема: [{Key}] {Issue}", key, message);
        foreach (var gone in _lastIssues.Where(k => !issues.ContainsKey(k)))
            log.LogInformation("Проблема ушла: {Issue}", gone);
        _lastIssues = [.. issues.Keys];

        if (status.State != _lastState)
        {
            log.LogWarning("Состояние: {From} → {To}{Countdown}", _lastState, status.State,
                status.State == GuardState.Alarm ? $", выключение через {status.SecondsToShutdown:0} с" : "");
            _lastState = status.State;
        }
    }
}
