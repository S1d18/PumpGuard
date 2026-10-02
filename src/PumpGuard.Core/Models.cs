namespace PumpGuard.Core;

public enum SensorKind { Temperature, Fan, Control, Load, Power, Clock, Voltage, Flow, Data, Other }

public sealed record SensorReading(string Id, string Name, string Hardware, SensorKind Kind, double? Value, string Unit);

public enum GuardState { Normal, Warning, Alarm, ShuttingDown }

public enum IssueSeverity { Warning, Alarm, Critical }

public sealed record Issue(string Source, IssueSeverity Severity, string Message);

public sealed record PumpStatus(string? SensorId, double? Rpm, double MinRpm, bool Ok);

/// <summary>Level: ok | warn | shutdown | critical | unknown. Group/Label only drive the widget layout.</summary>
public sealed record TemperatureStatus(
    string Name, string SensorId, double? Value,
    double WarnC, double ShutdownC, double CriticalC, string Level,
    string Group = "", string? Label = null);

/// <summary>Limit: the ceiling this value runs against (GPU power limit, VRAM size), shown as "value / limit".</summary>
public sealed record ExtraValue(string Name, double? Value, string Unit, string Group = "", double? Limit = null);

public sealed record GuardStatus(
    DateTimeOffset Timestamp,
    GuardState State,
    PumpStatus Pump,
    IReadOnlyList<TemperatureStatus> Temperatures,
    IReadOnlyList<ExtraValue> Extras,
    IReadOnlyList<Issue> Issues,
    DateTimeOffset? ShutdownAt,
    double? SecondsToShutdown,
    DateTimeOffset? SnoozedUntil,
    string? ShutdownReason,
    bool DryRun)
{
    public IReadOnlyList<FanStatus> Fans { get; init; } = [];

    /// <summary>Informational lines that do not change the state.</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];

    /// <summary>Every GPU found, including ones without a driver (they have a Note and no rows).</summary>
    public IReadOnlyList<GpuCard> Gpus { get; init; } = [];

    /// <summary>Adds warnings found outside the safety rules (e.g. NVLink); they can only raise Normal to Warning.</summary>
    public GuardStatus WithWarnings(IReadOnlyList<Issue> warnings) => warnings.Count == 0 ? this : this with
    {
        Issues = [.. Issues, .. warnings],
        State = State == GuardState.Normal ? GuardState.Warning : State,
    };
    public string? FanPreset { get; init; }
    public bool FanControlEnabled { get; init; }

    /// <summary>Every plottable value of this status under a stable key (for history and sparklines).</summary>
    public IEnumerable<(string Key, double? Value)> SeriesPoints()
    {
        yield return ("pump", Pump.Rpm);
        foreach (var t in Temperatures) yield return ($"temp:{t.Name}", t.Value);
        foreach (var e in Extras) yield return ($"extra:{e.Group}/{e.Name}", e.Value);
        foreach (var f in Fans) yield return ($"fan:{f.Name}", f.Rpm);
    }

    /// <summary>Adds fan data; fan problems are warnings and can only raise Normal to Warning.</summary>
    public GuardStatus WithFans(FanControlResult fans, bool controlEnabled) => this with
    {
        Fans = fans.Fans,
        FanPreset = fans.ActivePreset,
        FanControlEnabled = controlEnabled,
        Issues = [.. Issues, .. fans.Issues],
        State = State == GuardState.Normal && fans.Issues.Count > 0 ? GuardState.Warning : State,
    };
}

public sealed record GuardEvaluation(GuardStatus Status, bool ExecuteShutdown);
