using System.Globalization;

namespace PumpGuard.Core;

/// <summary>
/// Pure decision logic: takes sensor values and the current time, returns the guard state
/// and whether the machine must be shut down now. Holds only timing state, no I/O.
/// </summary>
public sealed class SafetyMonitor
{
    private const double MinValidTemp = 0, MaxValidTemp = 150, MaxValidRpm = 20000;

    private readonly PumpGuardOptions _o;
    private readonly Dictionary<string, DateTimeOffset> _since = new();
    private readonly object _lock = new();
    private DateTimeOffset? _shutdownAt;
    private DateTimeOffset? _snoozedUntil;
    private string? _shutdownReason;
    private bool _shutdownIssued;
    private bool _pumpSeen;
    private GpuSetup _gpus = GpuSetup.Empty;
    private IReadOnlyList<string> _notices = [];

    public SafetyMonitor(PumpGuardOptions options) => _o = options;

    /// <summary>Rules and widget rows generated for the GPUs found at runtime, on top of the config's own.</summary>
    public void SetGpus(GpuSetup gpus, IReadOnlyList<string> notices)
    {
        lock (_lock)
        {
            _gpus = gpus;
            _notices = notices;
        }
    }

    public GuardEvaluation Evaluate(IReadOnlyDictionary<string, double?> readings, DateTimeOffset now)
    {
        lock (_lock)
        {
            var issues = new List<Issue>();
            var pump = EvaluatePump(readings, now, issues);
            var temps = _o.Temperatures.Concat(_gpus.Rules)
                .Where(r => r.AllSensorIds().Count > 0)
                .Select(r => EvaluateTemperature(r, readings, now, issues))
                .ToList();
            var extras = _o.Extras.Concat(_gpus.Extras)
                .Select(e => new ExtraValue(e.Name, Finite(Get(readings, e.SensorId)), e.Unit, e.Group,
                    e.LimitSensorId is { } limitId ? Finite(Get(readings, limitId)) : null))
                .ToList();

            var critical = issues.FirstOrDefault(i => i.Severity == IssueSeverity.Critical);
            var alarm = issues.FirstOrDefault(i => i.Severity == IssueSeverity.Alarm);
            GuardState state;

            if (_shutdownIssued)
            {
                state = GuardState.ShuttingDown;
            }
            else if (critical is not null)
            {
                _shutdownReason = critical.Message;
                _shutdownAt = now;
                state = GuardState.ShuttingDown;
            }
            else if (alarm is not null && _snoozedUntil > now)
            {
                state = GuardState.Warning;
            }
            else if (alarm is not null)
            {
                _snoozedUntil = null;
                _shutdownAt ??= now.AddSeconds(_o.ShutdownCountdownSeconds);
                _shutdownReason = alarm.Message;
                state = now >= _shutdownAt ? GuardState.ShuttingDown : GuardState.Alarm;
            }
            else
            {
                _shutdownAt = null;
                _shutdownReason = null;
                state = issues.Count > 0 ? GuardState.Warning : GuardState.Normal;
            }

            var execute = state == GuardState.ShuttingDown && !_shutdownIssued;
            if (execute) _shutdownIssued = true;

            var status = new GuardStatus(
                now, state, pump, temps, extras, issues,
                _shutdownAt,
                _shutdownAt is { } at ? Math.Max(0, (at - now).TotalSeconds) : null,
                _snoozedUntil > now ? _snoozedUntil : null,
                _shutdownReason,
                _o.DryRun) { Notices = _notices };
            return new GuardEvaluation(status, execute);
        }
    }

    /// <summary>Cancels a running countdown and snoozes alarm-level issues. Returns false if there is nothing to cancel.</summary>
    public bool CancelCountdown(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_shutdownIssued || _shutdownAt is null) return false;
            _shutdownAt = null;
            _shutdownReason = null;
            _snoozedUntil = now.AddSeconds(_o.CancelSnoozeSeconds);
            return true;
        }
    }

    private PumpStatus EvaluatePump(IReadOnlyDictionary<string, double?> readings, DateTimeOffset now, List<Issue> issues)
    {
        var p = _o.Pump;
        if (string.IsNullOrWhiteSpace(p.SensorId))
        {
            issues.Add(new Issue("pump", IssueSeverity.Warning, "Датчик помпы не настроен (Pump:SensorId)"));
            return new PumpStatus(null, null, p.MinRpm, false);
        }

        var rpm = Get(readings, p.SensorId) is { } v && double.IsFinite(v) && v is >= 0 and <= MaxValidRpm ? v : (double?)null;
        if (rpm is null)
        {
            Held("pump:low", false, now, 0);
            var lost = Held("pump:lost", true, now, _o.SensorLostAfterSeconds);
            // A sensor that never answered since start-up is a setup problem (wrong id, no PawnIO driver), not a
            // stopped pump: alarming on it would power the PC off ~40 s after every boot.
            if (lost && !_pumpSeen)
                issues.Add(new Issue("pump", IssueSeverity.Warning,
                    $"Датчик помпы {p.SensorId} не отвечает с момента запуска — проверьте Pump:SensorId (--list-sensors)"));
            else if (lost)
                issues.Add(new Issue("pump",
                    p.TreatSensorLossAsFailure ? IssueSeverity.Alarm : IssueSeverity.Warning,
                    $"Нет данных с датчика помпы дольше {_o.SensorLostAfterSeconds} с"));
            return new PumpStatus(p.SensorId, null, p.MinRpm, false);
        }

        _pumpSeen = true;
        Held("pump:lost", false, now, 0);
        var low = rpm < p.MinRpm;
        if (low)
        {
            var failed = Held("pump:low", true, now, p.FailAfterSeconds);
            issues.Add(failed
                ? new Issue("pump", IssueSeverity.Alarm, $"Помпа остановилась: {F(rpm)} об/мин (минимум {F(p.MinRpm)})")
                : new Issue("pump", IssueSeverity.Warning, $"Низкие обороты помпы: {F(rpm)} об/мин"));
        }
        else
        {
            Held("pump:low", false, now, 0);
        }
        return new PumpStatus(p.SensorId, rpm, p.MinRpm, !low);
    }

    private TemperatureStatus EvaluateTemperature(TemperatureRule r, IReadOnlyDictionary<string, double?> readings,
        DateTimeOffset now, List<Issue> issues)
    {
        // Keyed by block + name: generated GPU rules can be rebuilt without resetting timers, and a hand-written
        // rule that happens to share a name with a generated one still gets its own timers.
        var key = $"t:{r.Group}:{r.Name}";
        // The hottest valid reading wins: a sensor that under-reports must not hide an overheat.
        var (hottestId, t) = r.AllSensorIds()
            .Select(id => (Id: id, Value: IsValidTemp(Get(readings, id)) ? Get(readings, id) : null))
            .Where(x => x.Value is not null)
            .OrderByDescending(x => x.Value)
            .FirstOrDefault();
        TemperatureStatus Result(string level) => new(r.Name, hottestId ?? r.AllSensorIds()[0], t, r.WarnC, r.ShutdownC, r.CriticalC, level, r.Group, r.Label);

        var ids = r.AllSensorIds();
        var lost = ids.Where(id => Held($"{key}:lost:{id}", !IsValidTemp(Get(readings, id)), now, _o.SensorLostAfterSeconds)).ToList();
        if (lost.Count == ids.Count)
            issues.Add(new Issue(r.Name, IssueSeverity.Warning, $"{r.Name}: нет данных с датчика"));
        else if (lost.Count > 0)
            issues.Add(new Issue(r.Name, IssueSeverity.Warning, $"{r.Name}: не отвечает {string.Join(", ", lost)}"));

        if (t is null)
        {
            Held(key + ":crit", false, now, 0);
            Held(key + ":shut", false, now, 0);
            return Result("unknown");
        }

        var critical = Held(key + ":crit", t >= r.CriticalC, now, r.CriticalHoldSeconds);
        var shutdown = Held(key + ":shut", t >= r.ShutdownC, now, r.ShutdownHoldSeconds);

        if (critical)
        {
            issues.Add(new Issue(r.Name, IssueSeverity.Critical, $"{r.Name}: {F(t)} °C — критическая температура (≥ {F(r.CriticalC)} °C)"));
            return Result("critical");
        }
        if (t >= r.ShutdownC)
        {
            issues.Add(new Issue(r.Name, shutdown ? IssueSeverity.Alarm : IssueSeverity.Warning,
                $"{r.Name}: {F(t)} °C — перегрев (≥ {F(r.ShutdownC)} °C)"));
            return Result("shutdown");
        }
        if (t >= r.WarnC)
        {
            issues.Add(new Issue(r.Name, IssueSeverity.Warning, $"{r.Name}: {F(t)} °C — высокая температура"));
            return Result("warn");
        }
        return Result("ok");
    }

    /// <summary>True once <paramref name="condition"/> has been continuously true for <paramref name="seconds"/>.</summary>
    private bool Held(string key, bool condition, DateTimeOffset now, int seconds)
    {
        if (!condition)
        {
            _since.Remove(key);
            return false;
        }
        if (!_since.TryGetValue(key, out var since))
            _since[key] = since = now;
        return now - since >= TimeSpan.FromSeconds(seconds);
    }

    private static double? Get(IReadOnlyDictionary<string, double?> readings, string id) =>
        readings.TryGetValue(id, out var v) ? v : null;

    private static bool IsValidTemp(double? v) => v is { } x && double.IsFinite(x) && x is > MinValidTemp and <= MaxValidTemp;

    private static double? Finite(double? v) => v is { } x && double.IsFinite(x) ? x : null;

    private static string F(double? v) => v?.ToString("0", CultureInfo.InvariantCulture) ?? "—";
}
