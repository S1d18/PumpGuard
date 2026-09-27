namespace PumpGuard.Core;

public sealed class PumpGuardOptions
{
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>A sensor that returns no value for this long is considered lost.</summary>
    public int SensorLostAfterSeconds { get; set; } = 10;

    /// <summary>Time between an alarm and the actual shutdown; the user can cancel within it.</summary>
    public int ShutdownCountdownSeconds { get; set; } = 30;

    /// <summary>After a cancel, alarm-level issues do not start a new countdown for this long. Critical ones still do.</summary>
    public int CancelSnoozeSeconds { get; set; } = 300;

    /// <summary>Log instead of shutting down; also enables the simulation endpoint.</summary>
    public bool DryRun { get; set; }

    public string ApiUrl { get; set; } = "http://127.0.0.1:8765";

    /// <summary>If set, POST endpoints require this value in the X-PumpGuard-Token header.</summary>
    public string? ApiToken { get; set; }

    public PumpOptions Pump { get; set; } = new();

    public List<TemperatureRule> Temperatures { get; set; } = [];

    /// <summary>Extra values shown in the widget and status (GPU load, power, ...). No safety logic.</summary>
    public List<ExtraSensor> Extras { get; set; } = [];
}

public sealed class PumpOptions
{
    public string? SensorId { get; set; }
    public double MinRpm { get; set; } = 500;
    public int FailAfterSeconds { get; set; } = 5;
    public bool TreatSensorLossAsFailure { get; set; } = true;
}

public sealed class TemperatureRule
{
    public string Name { get; set; } = "";
    public string SensorId { get; set; } = "";

    /// <summary>Widget block this value is shown in ("CPU", "GPU", ...), and its name inside the block.</summary>
    public string Group { get; set; } = "";
    public string? Label { get; set; }

    /// <summary>More sensors for the same rule; the hottest valid reading wins (e.g. NVML + NVAPI for redundancy).</summary>
    public List<string> SensorIds { get; set; } = [];

    public double WarnC { get; set; } = 75;
    public double ShutdownC { get; set; } = 85;
    public int ShutdownHoldSeconds { get; set; } = 5;
    public double CriticalC { get; set; } = 92;
    public int CriticalHoldSeconds { get; set; } = 2;

    public IReadOnlyList<string> AllSensorIds() =>
        SensorIds.Prepend(SensorId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
}

public sealed class ExtraSensor
{
    public string Name { get; set; } = "";
    public string SensorId { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Group { get; set; } = "";

    /// <summary>Optional sensor holding the maximum for this value, e.g. /nvml/0/power/limit.</summary>
    public string? LimitSensorId { get; set; }
}
