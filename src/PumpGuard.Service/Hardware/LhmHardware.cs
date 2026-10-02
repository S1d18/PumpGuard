using LibreHardwareMonitor.Hardware;
using PumpGuard.Core;

namespace PumpGuard.Service.Hardware;

public interface ISensorSource
{
    IReadOnlyList<SensorReading> Read();
}

public interface IFanControl
{
    void Apply(IReadOnlyList<FanDecision> decisions);
    void RestoreDefaults();
}

/// <summary>
/// CPU, motherboard SuperIO (fans, pump, controls) and USB cooling controllers via LibreHardwareMonitor.
/// Needs administrator rights (the service runs as LocalSystem) and the PawnIO driver.
/// Must only be used from one thread at a time.
/// </summary>
public sealed class LhmHardware : ISensorSource, IFanControl, IDisposable
{
    private readonly ILogger<LhmHardware> _log;
    private readonly Dictionary<string, ISensor> _controls = new();
    private readonly Dictionary<string, double?> _applied = new();
    private Computer? _computer;
    private DateTimeOffset _nextOpenAttempt;

    public LhmHardware(ILogger<LhmHardware> log) => _log = log;

    public IReadOnlyList<SensorReading> Read()
    {
        if (!EnsureOpen()) return [];
        var result = new List<SensorReading>();
        _controls.Clear();
        foreach (var hw in _computer!.Hardware) Collect(hw, result);
        return result;
    }

    /// <summary>GPU nodes LHM found: id prefix ("/gpu-nvidia/0"), name and Windows device path (for the PCI bus).</summary>
    public IReadOnlyList<(string Prefix, string Name, string? DevicePath)> Gpus =>
        _computer?.Hardware
            .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .Select(h => (h.Identifier.ToString(), h.Name, (h as LibreHardwareMonitor.Hardware.Gpu.GenericGpu)?.DeviceId))
            .ToList() ?? [];

    public void Apply(IReadOnlyList<FanDecision> decisions)
    {
        foreach (var d in decisions)
        {
            if (_applied.TryGetValue(d.ControlId, out var prev) && prev == d.Percent) continue;
            if (!_controls.TryGetValue(d.ControlId, out var sensor) || sensor.Control is null)
            {
                _log.LogWarning("Канал управления {Id} не найден или не поддерживает управление", d.ControlId);
                _applied[d.ControlId] = d.Percent;
                continue;
            }
            try
            {
                if (d.Percent is { } pct) sensor.Control.SetSoftware((float)pct);
                else sensor.Control.SetDefault();
                _applied[d.ControlId] = d.Percent;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Не удалось установить {Id} = {Pct}", d.ControlId, d.Percent);
            }
        }
    }

    /// <summary>Hands every fan we touched back to the BIOS, so nothing is left at a low fixed speed.</summary>
    public void RestoreDefaults()
    {
        foreach (var id in _applied.Keys)
        {
            try { if (_controls.TryGetValue(id, out var s)) s.Control?.SetDefault(); }
            catch (Exception ex) { _log.LogError(ex, "Не удалось вернуть {Id} под управление BIOS", id); }
        }
        _applied.Clear();
    }

    public void Dispose()
    {
        RestoreDefaults();
        _computer?.Close();
    }

    private bool EnsureOpen()
    {
        if (_computer is not null) return true;
        if (DateTimeOffset.UtcNow < _nextOpenAttempt) return false;
        try
        {
            var c = new Computer
            {
                IsCpuEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsGpuEnabled = true, // Hot Spot / junction temperature (NVAPI, AMD ADL); NVML adds core, HBM, power
            };
            c.Open();
            _computer = c;
            _log.LogInformation("LibreHardwareMonitor: {Hw}", string.Join(", ", c.Hardware.Select(h => h.Name)));
            return true;
        }
        catch (Exception ex)
        {
            _nextOpenAttempt = DateTimeOffset.UtcNow.AddSeconds(30);
            _log.LogError(ex, "LibreHardwareMonitor не запустился (нужны права администратора и драйвер PawnIO)");
            return false;
        }
    }

    private void Collect(IHardware hw, List<SensorReading> result)
    {
        hw.Update();
        foreach (var s in hw.Sensors)
        {
            var id = s.Identifier.ToString();
            if (s.SensorType == SensorType.Control) _controls[id] = s;
            var (kind, unit) = Map(s.SensorType);
            result.Add(new SensorReading(id, s.Name, hw.Name, kind, s.Value, unit));
        }
        foreach (var sub in hw.SubHardware) Collect(sub, result);
    }

    private static (SensorKind, string) Map(SensorType t) => t switch
    {
        SensorType.Temperature => (SensorKind.Temperature, "°C"),
        SensorType.Fan => (SensorKind.Fan, "RPM"),
        SensorType.Control => (SensorKind.Control, "%"),
        SensorType.Load => (SensorKind.Load, "%"),
        SensorType.Power => (SensorKind.Power, "W"),
        SensorType.Clock => (SensorKind.Clock, "MHz"),
        SensorType.Voltage => (SensorKind.Voltage, "V"),
        SensorType.Flow => (SensorKind.Flow, "L/h"),
        SensorType.Data => (SensorKind.Data, "GB"),
        SensorType.SmallData => (SensorKind.Data, "MB"),
        _ => (SensorKind.Other, t.ToString()),
    };
}
