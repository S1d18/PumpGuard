using System.Text.RegularExpressions;

namespace PumpGuard.Core;

public sealed class GpuOptions
{
    /// <summary>Build rules and widget blocks for every detected GPU (GPU, GPU2, ...) instead of hand-written ids.</summary>
    public bool Auto { get; set; } = true;

    /// <summary>Hot Spot has no driver-reported limit; these apply to every GPU that reports one.</summary>
    public double HotSpotWarnC { get; set; } = 95;
    public double HotSpotShutdownC { get; set; } = 105;
    public double HotSpotCriticalC { get; set; } = 110;

    /// <summary>
    /// Cards to leave out entirely, matched by name substring, e.g. a display-only GT 710 that deliberately runs
    /// without the NVIDIA driver (installing its legacy driver would break the newer one the main GPU needs).
    /// </summary>
    public List<string> IgnoreNames { get; set; } = [];
}

/// <summary>Temperature limits the driver reports for a card (NVML), any of which may be unknown.</summary>
public sealed record GpuThresholds(double? GpuMax = null, double? Slowdown = null, double? Shutdown = null, double? MemoryMax = null);

/// <summary>A GPU as one source sees it: NVML device "/nvml/0", or an LHM hardware node "/gpu-nvidia/0".</summary>
public sealed record GpuSourceDevice(string Prefix, string Name, int? PciBus, GpuThresholds? Thresholds = null);

/// <summary>One physical card, with the source prefixes that describe it.</summary>
public sealed record GpuDevice(int Number, string Name, int? PciBus, string? NvmlPrefix, string? LhmPrefix, GpuThresholds Thresholds)
{
    /// <summary>"GPU · V100" for the first card, "GPU2 · GT 710" for the second, ...</summary>
    public string Group => $"{(Number == 1 ? "GPU" : $"GPU{Number}")} · {ShortName(Name)}";

    /// <summary>Stable sensor id prefix that survives index shuffles: /gpu/1, /gpu/2 (ordered by PCI bus).</summary>
    public string AliasPrefix => $"/gpu/{Number}";

    /// <summary>"NVIDIA Tesla V100-SXM2-16GB" → "V100", "NVIDIA GeForce GT 710" → "GT 710", "AMD Radeon RX 7900 XTX" → "RX 7900".</summary>
    public static string ShortName(string name)
    {
        var vendor = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NVIDIA", "AMD", "ATI", "Intel", "GeForce", "Tesla", "Quadro", "Radeon", "(R)", "Graphics" };
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !vendor.Contains(w)).ToList();
        var result = new List<string>();
        foreach (var w in words)
        {
            var part = w.Split('-')[0];
            result.Add(part);
            if (part.Any(char.IsDigit)) break;
        }
        return result.Count > 0 ? string.Join(' ', result) : name;
    }
}

public sealed record GpuInventory(IReadOnlyList<GpuDevice> Devices, IReadOnlyList<string> Notices)
{
    public static readonly GpuInventory Empty = new([], []);

    /// <summary>
    /// Pairs NVML devices with LHM GPU nodes. Same PCI bus = same card; without bus numbers, cards are paired
    /// by name in enumeration order. Cards are numbered by PCI bus, so GPU2 stays GPU2 across reboots.
    /// </summary>
    public static GpuInventory Merge(IReadOnlyList<GpuSourceDevice> nvml, IReadOnlyList<GpuSourceDevice> lhm, IReadOnlyList<string> notices)
    {
        var unmatchedLhm = lhm.ToList();
        var pairs = new List<(GpuSourceDevice? N, GpuSourceDevice? L)>();
        foreach (var n in nvml)
        {
            var l = unmatchedLhm.FirstOrDefault(x => x.PciBus is { } b && b == n.PciBus)
                ?? unmatchedLhm.FirstOrDefault(x => (x.PciBus is null || n.PciBus is null) && SameModel(x.Name, n.Name));
            if (l is not null) unmatchedLhm.Remove(l);
            pairs.Add((n, l));
        }
        pairs.AddRange(unmatchedLhm.Select(l => ((GpuSourceDevice?)null, (GpuSourceDevice?)l)));

        var devices = pairs
            .Select(p => (p.N, p.L, Bus: p.N?.PciBus ?? p.L?.PciBus))
            .OrderBy(p => p.Bus ?? int.MaxValue)
            .Select((p, i) => new GpuDevice(i + 1, p.N?.Name ?? p.L!.Name, p.Bus, p.N?.Prefix, p.L?.Prefix,
                p.N?.Thresholds ?? p.L?.Thresholds ?? new GpuThresholds()))
            .ToList();
        return new GpuInventory(devices, notices);
    }

    private static bool SameModel(string a, string b) => GpuDevice.ShortName(a).Equals(GpuDevice.ShortName(b), StringComparison.OrdinalIgnoreCase);
}

/// <summary>What auto-discovery adds on top of the hand-written config.</summary>
public sealed record GpuSetup(
    IReadOnlyList<TemperatureRule> Rules,
    IReadOnlyList<ExtraSensor> Extras,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Aliases)
{
    public static readonly GpuSetup Empty = new([], [], new Dictionary<string, IReadOnlyList<string>>());

    /// <summary>
    /// Builds per-card rules and widget rows, all pointing at stable alias ids (/gpu/N/hotspot, ...). Source ids
    /// are found by sensor name in the current readings, so only what a card really reports gets a rule or a row.
    /// </summary>
    public static GpuSetup Build(GpuInventory inventory, IReadOnlyList<SensorReading> sensors, GpuOptions o)
    {
        var rules = new List<TemperatureRule>();
        var extras = new List<ExtraSensor>();
        var aliases = new Dictionary<string, IReadOnlyList<string>>();

        foreach (var d in inventory.Devices)
        {
            string? Find(string? prefix, SensorKind kind, params string[] names) =>
                prefix is null ? null : sensors.FirstOrDefault(s =>
                    s.Kind == kind && s.Id.StartsWith(prefix + "/", StringComparison.Ordinal) && names.Contains(s.Name))?.Id;
            string? Nvml(string suffix) => d.NvmlPrefix is { } p && sensors.Any(s => s.Id == $"{p}/{suffix}") ? $"{p}/{suffix}" : null;

            bool Alias(string name, params string?[] sources)
            {
                var ids = sources.OfType<string>().ToList();
                if (ids.Count == 0) return false;
                aliases[$"{d.AliasPrefix}/{name}"] = ids;
                return true;
            }

            var short1 = d.Number == 1 ? "GPU" : $"GPU{d.Number}";
            var t = d.Thresholds;

            if (Alias("hotspot", Find(d.LhmPrefix, SensorKind.Temperature, "GPU Hot Spot")))
                rules.Add(Rule($"{short1} Hot Spot", "Hot Spot", d, "hotspot", o.HotSpotWarnC, o.HotSpotShutdownC, o.HotSpotCriticalC));

            if (Alias("core", Nvml("temperature/core"), Find(d.LhmPrefix, SensorKind.Temperature, "GPU Core")))
            {
                var shutdown = t.GpuMax ?? (t.Slowdown - 4) ?? 83;
                var critical = t.Slowdown is { } s ? (t.Shutdown is { } h ? Math.Min(s + 1, h - 2) : s + 1) : shutdown + 5;
                if (critical <= shutdown) critical = shutdown + 2;
                rules.Add(Rule(short1, "Ядро", d, "core", shutdown - 8, shutdown, critical));
            }

            // NVIDIA: HBM/GDDR via NVML only. LHM's "GPU Memory Junction" reads ~14 °C high on a V100 (checked
            // against GPU-Z), and a hotter-wins rule would turn that into false shutdowns.
            var memory = Nvml("temperature/memory")
                ?? (d.LhmPrefix?.StartsWith("/gpu-nvidia", StringComparison.Ordinal) == true ? null : Find(d.LhmPrefix, SensorKind.Temperature, "GPU Memory"));
            if (Alias("memory", memory))
            {
                var max = t.MemoryMax ?? 95;
                rules.Add(Rule($"{short1} Memory", "Память", d, "memory", max - 10, max, max + 5));
            }

            void Extra(string alias, string label, string unit, string? limitAlias = null) =>
                extras.Add(new ExtraSensor { Name = label, SensorId = $"{d.AliasPrefix}/{alias}", Unit = unit, Group = d.Group,
                    LimitSensorId = limitAlias is null ? null : $"{d.AliasPrefix}/{limitAlias}" });

            if (Alias("load", Nvml("load/core") ?? Find(d.LhmPrefix, SensorKind.Load, "GPU Core"))) Extra("load", "Нагрузка", "%");
            if (Alias("power", Nvml("power") ?? Find(d.LhmPrefix, SensorKind.Power, "GPU Package", "GPU Power", "GPU Core")))
                Extra("power", "Мощность", "W", Alias("power-limit", Nvml("power/limit")) ? "power-limit" : null);
            if (Alias("vram", Nvml("memory/used")))
                Extra("vram", "VRAM", "GB", Alias("vram-total", Nvml("memory/total")) ? "vram-total" : null);
            if (Alias("fan", Nvml("fan"))) Extra("fan", "Вентилятор", "%");
            else if (Alias("fan", Find(d.LhmPrefix, SensorKind.Fan, "GPU Fan", "GPU Fan 1"))) Extra("fan", "Вентилятор", "RPM");
            // NVLink only when links are up: an SXM2 card on a PCIe adapter has links that never come up.
            var nvlinkActive = Nvml("nvlink/active");
            if (sensors.FirstOrDefault(s => s.Id == nvlinkActive)?.Value > 0
                && Alias("nvlink", nvlinkActive) && Alias("nvlink-total", Nvml("nvlink/total")))
                Extra("nvlink", "NVLink", "", "nvlink-total");
        }
        return new GpuSetup(rules, extras, aliases);
    }

    private static TemperatureRule Rule(string name, string label, GpuDevice d, string alias, double warn, double shutdown, double critical) => new()
    {
        Name = name, Label = label, Group = d.Group, SensorId = $"{d.AliasPrefix}/{alias}",
        WarnC = warn, ShutdownC = shutdown, CriticalC = critical, ShutdownHoldSeconds = 5, CriticalHoldSeconds = 2,
    };

    /// <summary>Adds alias readings (/gpu/N/...): the highest valid value among the alias's sources.</summary>
    public void ApplyAliases(Dictionary<string, double?> readings)
    {
        foreach (var (alias, sources) in Aliases)
            readings[alias] = sources
                .Select(id => readings.TryGetValue(id, out var v) ? v : null)
                .Where(v => v is { } x && double.IsFinite(x))
                .Max();
    }
}

public static partial class PciBus
{
    /// <summary>"00000000:41:00.0" → 65.</summary>
    public static int? FromBusId(string? busId)
    {
        var m = BusIdRegex().Match(busId ?? "");
        return m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : null;
    }

    [GeneratedRegex(@"^[0-9A-Fa-f]+:([0-9A-Fa-f]{2}):[0-9A-Fa-f]{2}\.[0-9A-Fa-f]$")]
    private static partial Regex BusIdRegex();
}
