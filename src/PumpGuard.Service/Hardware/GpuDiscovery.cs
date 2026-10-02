using System.Management;
using Microsoft.Extensions.Options;
using PumpGuard.Core;

namespace PumpGuard.Service.Hardware;

/// <summary>
/// Finds the GPUs (NVML + LHM, matched by PCI bus), builds their rules and widget blocks, and notes cards that
/// Windows sees but could not start a driver for. Re-checked once a minute; cheap the rest of the time.
/// </summary>
public sealed class GpuDiscovery(NvmlSensorSource nvml, LhmHardware lhm, IOptions<PumpGuardOptions> options, ILogger<GpuDiscovery> log)
{
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, int?> _busByPath = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _nextCheck;
    private string _lastSignature = "";

    public GpuSetup Setup { get; private set; } = GpuSetup.Empty;
    public IReadOnlyList<GpuCard> Cards { get; private set; } = [];

    /// <summary>Returns true when the GPU set (and so the rules) changed.</summary>
    public bool Refresh(IReadOnlyList<SensorReading> sensors, DateTimeOffset now)
    {
        var o = options.Value.Gpus;
        if (!o.Auto || now < _nextCheck) return false;
        _nextCheck = now + RecheckEvery;

        bool Ignored(string name) => o.IgnoreNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

        var nvmlDevices = nvml.Devices.Where(d => !Ignored(d.Name)).ToList();
        var lhmDevices = lhm.Gpus.Where(g => !Ignored(g.Name))
            .Select(g => new GpuSourceDevice(g.Prefix, g.Name, g.DevicePath is { } p ? BusOf(p) : null)).ToList();
        if (Simulation.CloneGpus(options.Value) is var clones and > 0)
            (nvmlDevices, lhmDevices) = (Simulation.Clone(nvmlDevices, clones), Simulation.Clone(lhmDevices, clones));

        var inventory = GpuInventory.Merge(nvmlDevices, lhmDevices, DriverlessCards(Ignored));
        var setup = GpuSetup.Build(inventory, sensors, o);

        var signature = string.Join("|", setup.Rules.Select(r => r.Name).Concat(setup.Extras.Select(e => e.SensorId))
            .Concat(inventory.Cards.Select(c => $"{c.Group}:{c.Note}")));
        if (signature == _lastSignature) return false;
        _lastSignature = signature;
        (Setup, Cards) = (setup, inventory.Cards);
        log.LogWarning("Видеокарты: {Gpus}",
            inventory.Devices.Count == 0 ? "не найдены"
                : string.Join(", ", inventory.Devices.Select(d => $"{d.Group} (PCI {d.PciBus}){(d.Note is { } n ? " — " + n : "")}")));
        return true;
    }

    /// <summary>"\\?\PCI#VEN_10DE&amp;DEV_1DB1&amp;...#4&amp;223e60ad&amp;0&amp;0019#{guid}" → its PCI bus number, via Windows' device properties.</summary>
    private int? BusOf(string devicePath)
    {
        if (_busByPath.TryGetValue(devicePath, out var cached)) return cached;
        int? bus = null;
        try
        {
            var parts = devicePath.TrimStart('\\', '?').Split('#');
            var instanceId = parts.Length >= 3 ? $"{parts[0]}\\{parts[1]}\\{parts[2]}" : devicePath;
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_PnPEntity WHERE DeviceID = '{instanceId.Replace("\\", "\\\\").Replace("'", "")}'");
            foreach (ManagementObject device in searcher.Get())
            {
                using (device)
                {
                    var args = device.GetMethodParameters("GetDeviceProperties");
                    args["devicePropertyKeys"] = new[] { "DEVPKEY_Device_BusNumber" };
                    var result = device.InvokeMethod("GetDeviceProperties", args, null);
                    if (result?["deviceProperties"] is ManagementBaseObject[] { Length: > 0 } props && props[0]["Data"] is { } data)
                        bus = Convert.ToInt32(data);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "PCI-шина для {Path} не определена", devicePath);
        }
        return _busByPath[devicePath] = bus;
    }

    /// <summary>Video cards Windows lists with a device error (e.g. code 31: no driver loaded).</summary>
    private List<DriverlessGpu> DriverlessCards(Func<string, bool> ignored)
    {
        var cards = new List<DriverlessGpu>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_VideoController");
            foreach (ManagementObject card in searcher.Get())
            {
                using (card)
                {
                    var name = card["Name"] as string ?? "?";
                    var code = Convert.ToInt32(card["ConfigManagerErrorCode"] ?? 0);
                    var pnp = card["PNPDeviceID"] as string ?? "";
                    if (code != 0 && pnp.StartsWith("PCI", StringComparison.OrdinalIgnoreCase) && !ignored(name))
                        cards.Add(new DriverlessGpu(name, BusOf(pnp), code));
                }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Список видеокарт Windows недоступен");
        }
        return cards;
    }
}
