using Microsoft.Extensions.Options;
using PumpGuard.Core;

namespace PumpGuard.Service.Hardware;

/// <summary>
/// Finds the GPUs (NVML + LHM, matched by PCI bus), builds their rules and widget blocks, and adds what Windows
/// knows about every card (PCIe link, power state), including cards it could not start a driver for.
/// Re-checked once a minute; cheap the rest of the time.
/// </summary>
public sealed class GpuDiscovery(NvmlSensorSource nvml, LhmHardware lhm, IOptions<PumpGuardOptions> options, ILogger<GpuDiscovery> log)
{
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, int?> _busByPath = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _nextCheck;
    private string _lastSignature = "";

    public GpuSetup Setup { get; private set; } = GpuSetup.Empty;
    public IReadOnlyList<GpuCard> Cards { get; private set; } = [];

    /// <summary>Returns true when the GPU set, its rules or its card details changed.</summary>
    public bool Refresh(IReadOnlyList<SensorReading> sensors, DateTimeOffset now)
    {
        var o = options.Value.Gpus;
        if (!o.Auto || now < _nextCheck) return false;
        _nextCheck = now + RecheckEvery;

        bool Ignored(string name) => o.IgnoreNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

        var pci = PciDevices.DisplayAdapters(log);
        var nvmlDevices = nvml.Devices.Where(d => !Ignored(d.Name)).ToList();
        var lhmDevices = lhm.Gpus.Where(g => !Ignored(g.Name))
            .Select(g => new GpuSourceDevice(g.Prefix, g.Name, g.DevicePath is { } p ? BusOf(p) : null)).ToList();
        if (Simulation.CloneGpus(options.Value) is var clones and > 0)
            (nvmlDevices, lhmDevices) = (Simulation.Clone(nvmlDevices, clones), Simulation.Clone(lhmDevices, clones));
        var driverless = pci.Where(d => d.ErrorCode != 0 && !Ignored(d.Name))
            .Select(d => new DriverlessGpu(d.Name, d.Bus, d.ErrorCode)).ToList();

        var inventory = GpuInventory.Merge(nvmlDevices, lhmDevices, driverless);
        var setup = GpuSetup.Build(inventory, sensors, o);
        var cards = inventory.Cards
            .Select(c => c with { Info = pci.FirstOrDefault(p => c.PciBus is not null && p.Bus == c.PciBus)?.Info ?? [] })
            .ToList();

        var signature = string.Join("|", setup.Rules.Select(r => r.Name).Concat(setup.Extras.Select(e => e.SensorId))
            .Concat(cards.Select(c => $"{c.Group}:{c.Note}:{string.Join(",", c.Info)}")));
        if (signature == _lastSignature) return false;
        _lastSignature = signature;
        (Setup, Cards) = (setup, cards);
        log.LogWarning("Видеокарты: {Gpus}",
            cards.Count == 0 ? "не найдены"
                : string.Join("; ", cards.Select(c => $"{c.Group} (PCI {c.PciBus}{string.Concat(c.Info.Select(i => $", {i.Label} {i.Value}"))})"
                    + (c.Note is { } n ? " — " + n : ""))));
        return true;
    }

    private int? BusOf(string devicePath)
    {
        if (_busByPath.TryGetValue(devicePath, out var cached)) return cached;
        return _busByPath[devicePath] = PciDevices.BusOf(devicePath, log);
    }
}
