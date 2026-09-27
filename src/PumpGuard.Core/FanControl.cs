using System.Globalization;

namespace PumpGuard.Core;

public sealed class FanControlOptions
{
    /// <summary>When false, channels are only monitored and the BIOS keeps control of the fans.</summary>
    public bool Enabled { get; set; }

    public string ActivePreset { get; set; } = "Balanced";

    /// <summary>How fast a curve may lower the speed, in percent per tick. Raising is instant.</summary>
    public double RampDownPercentPerTick { get; set; } = 3;

    /// <summary>Run every controlled channel at its maximum while any temperature is at warning level or above.</summary>
    public bool MaxOnWarning { get; set; } = true;

    /// <summary>How long a warning-level temperature must last before fans go to maximum.</summary>
    public int WarnHoldSeconds { get; set; } = 3;

    /// <summary>Once at maximum because of heat, stay there at least this long.</summary>
    public int MaxHoldSeconds { get; set; } = 10;

    public List<FanChannel> Channels { get; set; } = [];

    /// <summary>User presets; a preset with a built-in name replaces the built-in one.</summary>
    public Dictionary<string, FanPreset> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FanChannel
{
    public string Name { get; set; } = "";

    /// <summary>LHM control sensor (…/control/N). Empty means monitor only.</summary>
    public string? ControlId { get; set; }

    /// <summary>LHM fan sensor (…/fan/N) that reports this channel's RPM.</summary>
    public string? FanSensorId { get; set; }

    /// <summary>Temperatures the curve follows; the hottest one is used.</summary>
    public List<string> SourceSensorIds { get; set; } = [];

    public double MinPercent { get; set; } = 20;
    public double MaxPercent { get; set; } = 100;

    /// <summary>
    /// A pump channel is driven to MaxPercent when the pump is failing, and is never handed to the BIOS.
    /// Keep MaxPercent below 100 for pumps whose RPM signal is unreliable at 100 % duty.
    /// </summary>
    public bool IsPump { get; set; }

    /// <summary>Warn when RPM stays below this value (0 disables the check).</summary>
    public double FailMinRpm { get; set; }
    public int FailAfterSeconds { get; set; } = 10;

    /// <summary>Per-preset curve overrides for this channel, keyed by preset name.</summary>
    public Dictionary<string, List<CurvePoint>> Curves { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum FanPresetMode { Curve, Fixed, Bios }

public sealed class FanPreset
{
    public FanPresetMode Mode { get; set; } = FanPresetMode.Curve;
    public List<CurvePoint> Curve { get; set; } = [];
    public double FixedPercent { get; set; } = 100;
}

public sealed class CurvePoint
{
    public CurvePoint() { }
    public CurvePoint(double tempC, double percent) => (TempC, Percent) = (tempC, percent);

    public double TempC { get; set; }
    public double Percent { get; set; }
}

/// <summary>Mode: Curve | Fixed | Manual | Bios | Max | Monitor.</summary>
public sealed record FanStatus(
    string Name, double? Rpm, double? Percent, double? TargetPercent,
    string Mode, double? SourceTemp, bool Controllable);

/// <summary>Percent null hands the channel back to the BIOS.</summary>
public sealed record FanDecision(string ControlId, double? Percent);

public sealed record FanControlResult(
    IReadOnlyList<FanDecision> Decisions, IReadOnlyList<FanStatus> Fans, IReadOnlyList<Issue> Issues, string ActivePreset);

/// <summary>Runtime choices that survive a restart (the service stores them on disk).</summary>
public sealed class FanSettings
{
    public string? ActivePreset { get; set; }
    public Dictionary<string, double> Manual { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FanPreset> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Pure fan decision logic: readings + guard state in, target duty per channel out.</summary>
public sealed class FanController
{
    public static IReadOnlyDictionary<string, FanPreset> BuiltInPresets { get; } =
        new Dictionary<string, FanPreset>(StringComparer.OrdinalIgnoreCase)
        {
            ["Silent"] = Curve((30, 20), (50, 30), (65, 50), (75, 80), (85, 100)),
            ["Balanced"] = Curve((30, 30), (50, 45), (65, 65), (75, 90), (80, 100)),
            ["Performance"] = Curve((30, 50), (50, 70), (65, 90), (70, 100)),
            ["Full"] = new FanPreset { Mode = FanPresetMode.Fixed, FixedPercent = 100 },
            ["Bios"] = new FanPreset { Mode = FanPresetMode.Bios },
        };

    private const double MaxValidTemp = 150, MaxValidRpm = 20000;

    private readonly FanControlOptions _o;
    private readonly object _lock = new();
    private readonly Dictionary<string, FanPreset> _userPresets;
    private readonly Dictionary<string, double> _manual = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _lastTarget = new();
    private readonly Dictionary<string, DateTimeOffset> _lowSince = new();
    private string _activePreset;
    private DateTimeOffset? _warnSince;
    private DateTimeOffset _maxUntil;

    public FanController(FanControlOptions options)
    {
        _o = options;
        _userPresets = new(options.Presets, StringComparer.OrdinalIgnoreCase);
        _activePreset = PresetsUnlocked().ContainsKey(options.ActivePreset) ? options.ActivePreset : "Balanced";
    }

    /// <summary>Raised after a preset, manual value or user preset changes, so the host can persist it.</summary>
    public event Action? SettingsChanged;

    public bool ControlEnabled => _o.Enabled;

    public string ActivePreset { get { lock (_lock) return _activePreset; } }

    public IReadOnlyDictionary<string, FanPreset> Presets { get { lock (_lock) return PresetsUnlocked(); } }

    public IReadOnlyDictionary<string, double> ManualOverrides
    {
        get { lock (_lock) return new Dictionary<string, double>(_manual, StringComparer.OrdinalIgnoreCase); }
    }

    public bool SetPreset(string name)
    {
        lock (_lock)
        {
            var match = PresetsUnlocked().Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is null) return false;
            _activePreset = match;
        }
        SettingsChanged?.Invoke();
        return true;
    }

    /// <summary>Pins a channel to a fixed duty, or returns it to the preset when <paramref name="percent"/> is null.</summary>
    public bool SetManual(string channel, double? percent)
    {
        lock (_lock)
        {
            var ch = _o.Channels.FirstOrDefault(c => c.Name.Equals(channel, StringComparison.OrdinalIgnoreCase));
            if (ch is null || string.IsNullOrWhiteSpace(ch.ControlId)) return false;
            if (percent is null) _manual.Remove(ch.Name);
            else _manual[ch.Name] = Math.Clamp(percent.Value, 0, 100);
        }
        SettingsChanged?.Invoke();
        return true;
    }

    /// <summary>Adds or replaces a user preset. Returns an error message, or null on success.</summary>
    public string? SavePreset(string name, FanPreset preset)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Пустое имя пресета";
        if (preset.Mode == FanPresetMode.Curve && preset.Curve.Count == 0) return "Кривая должна содержать хотя бы одну точку";
        if (preset.Curve.Any(p => p.Percent is < 0 or > 100 || p.TempC is < 0 or > MaxValidTemp))
            return "Точки кривой: температура 0–150 °C, скорость 0–100 %";
        if (preset.FixedPercent is < 0 or > 100) return "FixedPercent должен быть в диапазоне 0–100";
        lock (_lock) _userPresets[name] = preset;
        SettingsChanged?.Invoke();
        return null;
    }

    public bool DeletePreset(string name)
    {
        lock (_lock)
        {
            if (!_userPresets.Remove(name)) return false;
            if (!PresetsUnlocked().ContainsKey(_activePreset)) _activePreset = "Balanced";
        }
        SettingsChanged?.Invoke();
        return true;
    }

    public FanSettings ExportSettings()
    {
        lock (_lock)
            return new FanSettings
            {
                ActivePreset = _activePreset,
                Manual = new(_manual, StringComparer.OrdinalIgnoreCase),
                Presets = new(_userPresets, StringComparer.OrdinalIgnoreCase),
            };
    }

    public void ImportSettings(FanSettings s)
    {
        lock (_lock)
        {
            foreach (var (name, preset) in s.Presets) _userPresets[name] = preset;
            foreach (var (name, pct) in s.Manual)
                if (_o.Channels.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    _manual[name] = Math.Clamp(pct, 0, 100);
            if (s.ActivePreset is { } p && PresetsUnlocked().ContainsKey(p)) _activePreset = p;
        }
    }

    public FanControlResult Update(IReadOnlyDictionary<string, double?> readings, GuardStatus guard, DateTimeOffset now)
    {
        lock (_lock)
        {
            var decisions = new List<FanDecision>();
            var fans = new List<FanStatus>();
            var issues = new List<Issue>();
            var presets = PresetsUnlocked();
            var preset = presets.GetValueOrDefault(_activePreset) ?? BuiltInPresets["Balanced"];

            // Real overheat → max at once. Warning level must last WarnHoldSeconds (CPU Tdie spikes by 10 °C for a
            // second under light load). Once triggered, max is held MaxHoldSeconds so fans do not pulse.
            var hardHeat = guard.State is GuardState.Alarm or GuardState.ShuttingDown
                || guard.Temperatures.Any(t => t.Level is "shutdown" or "critical");
            var warm = _o.MaxOnWarning && guard.Temperatures.Any(t => t.Level == "warn");
            _warnSince = warm ? _warnSince ?? now : null;
            if (hardHeat || now - _warnSince >= TimeSpan.FromSeconds(_o.WarnHoldSeconds))
                _maxUntil = now.AddSeconds(_o.MaxHoldSeconds);
            var overheating = hardHeat || now < _maxUntil;
            var pumpFailing = guard.Pump.SensorId is not null && !guard.Pump.Ok;

            foreach (var ch in _o.Channels)
            {
                var rpm = Get(readings, ch.FanSensorId) is { } r && double.IsFinite(r) && r is >= 0 and <= MaxValidRpm ? r : (double?)null;
                var temp = ch.SourceSensorIds
                    .Select(id => Get(readings, id))
                    .Where(t => t is { } v && double.IsFinite(v) && v is > 0 and <= MaxValidTemp)
                    .Max();
                var currentPercent = Get(readings, ch.ControlId);
                var controllable = _o.Enabled && !string.IsNullOrWhiteSpace(ch.ControlId);

                CheckRpm(ch, rpm, now, issues);

                if (!controllable)
                {
                    fans.Add(new FanStatus(ch.Name, rpm, currentPercent, null, "Monitor", temp, false));
                    continue;
                }

                double? target;
                string mode;
                if (overheating || (ch.IsPump && pumpFailing))
                {
                    (target, mode) = (ch.MaxPercent, "Max");
                }
                else if (_manual.TryGetValue(ch.Name, out var manual))
                {
                    (target, mode) = (Clamp(ch, manual), "Manual");
                }
                else if (preset.Mode == FanPresetMode.Bios)
                {
                    // A pump stays under our control at its (safe) maximum: the BIOS may run it at 100 %,
                    // where some pumps' tach output turns to garbage and would look like a stopped pump.
                    (target, mode) = ch.IsPump ? ((double?)ch.MaxPercent, "Max") : (null, "Bios");
                }
                else if (preset.Mode == FanPresetMode.Fixed)
                {
                    (target, mode) = (Clamp(ch, preset.FixedPercent), "Fixed");
                }
                else if (temp is null)
                {
                    (target, mode) = (ch.MaxPercent, "Max");
                    issues.Add(new Issue(ch.Name, IssueSeverity.Warning, $"{ch.Name}: нет температуры для кривой — работает на максимуме"));
                }
                else
                {
                    var curve = ch.Curves.GetValueOrDefault(_activePreset) is { Count: > 0 } own ? own : preset.Curve;
                    target = Clamp(ch, Interpolate(curve, temp.Value));
                    mode = "Curve";
                    if (_lastTarget.TryGetValue(ch.ControlId!, out var last) && target < last)
                        target = Math.Max(target.Value, last - _o.RampDownPercentPerTick);
                }

                if (target is { } t) _lastTarget[ch.ControlId!] = t;
                else _lastTarget.Remove(ch.ControlId!);

                decisions.Add(new FanDecision(ch.ControlId!, target));
                fans.Add(new FanStatus(ch.Name, rpm, currentPercent, target, mode, temp, true));
            }

            return new FanControlResult(decisions, fans, issues, _activePreset);
        }
    }

    public static double Interpolate(IReadOnlyList<CurvePoint> curve, double temp)
    {
        if (curve.Count == 0) return 100;
        var pts = curve.OrderBy(p => p.TempC).ToList();
        if (temp <= pts[0].TempC) return pts[0].Percent;
        for (var i = 1; i < pts.Count; i++)
        {
            if (temp > pts[i].TempC) continue;
            var (a, b) = (pts[i - 1], pts[i]);
            return b.TempC == a.TempC ? b.Percent : a.Percent + (b.Percent - a.Percent) * (temp - a.TempC) / (b.TempC - a.TempC);
        }
        return pts[^1].Percent;
    }

    private void CheckRpm(FanChannel ch, double? rpm, DateTimeOffset now, List<Issue> issues)
    {
        if (ch.FailMinRpm <= 0 || string.IsNullOrWhiteSpace(ch.FanSensorId)) return;
        if (rpm >= ch.FailMinRpm)
        {
            _lowSince.Remove(ch.Name);
            return;
        }
        if (!_lowSince.TryGetValue(ch.Name, out var since)) _lowSince[ch.Name] = since = now;
        if (now - since >= TimeSpan.FromSeconds(ch.FailAfterSeconds))
            issues.Add(new Issue(ch.Name, IssueSeverity.Warning, rpm is null
                ? $"{ch.Name}: нет данных об оборотах"
                : $"{ch.Name}: {rpm.Value.ToString("0", CultureInfo.InvariantCulture)} об/мин — кулер не крутится?"));
    }

    private Dictionary<string, FanPreset> PresetsUnlocked()
    {
        var all = new Dictionary<string, FanPreset>(BuiltInPresets, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in _userPresets) all[k] = v;
        return all;
    }

    private static double Clamp(FanChannel ch, double v) => Math.Clamp(v, ch.MinPercent, Math.Max(ch.MinPercent, ch.MaxPercent));

    private static double? Get(IReadOnlyDictionary<string, double?> readings, string? id) =>
        id is not null && readings.TryGetValue(id, out var v) ? v : null;

    private static FanPreset Curve(params (double t, double p)[] points) =>
        new() { Mode = FanPresetMode.Curve, Curve = points.Select(x => new CurvePoint(x.t, x.p)).ToList() };
}
