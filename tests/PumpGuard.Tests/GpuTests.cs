using PumpGuard.Core;

namespace PumpGuard.Tests;

public class GpuTests
{
    private static readonly GpuThresholds V100 = new(GpuMax: 83, Slowdown: 87, Shutdown: 90, MemoryMax: 85);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SensorReading S(string id, string name, SensorKind kind, double? value = 40) =>
        new(id, name, "gpu", kind, value, "");

    /// <summary>What NVML + LHM report for one NVLink V100 at NVML index n / LHM index m.</summary>
    private static IEnumerable<SensorReading> V100Sensors(int n, int m) =>
    [
        S($"/nvml/{n}/temperature/core", "GPU Core", SensorKind.Temperature),
        S($"/nvml/{n}/temperature/memory", "GPU Memory", SensorKind.Temperature),
        S($"/nvml/{n}/load/core", "GPU Core", SensorKind.Load),
        S($"/nvml/{n}/power", "GPU Power", SensorKind.Power),
        S($"/nvml/{n}/power/limit", "GPU Power Limit", SensorKind.Power, 300),
        S($"/nvml/{n}/memory/used", "GPU Memory Used", SensorKind.Data, 3),
        S($"/nvml/{n}/memory/total", "GPU Memory Total", SensorKind.Data, 16),
        S($"/nvml/{n}/nvlink/active", "NVLink Active", SensorKind.Other, 6),
        S($"/nvml/{n}/nvlink/total", "NVLink Total", SensorKind.Other, 6),
        S($"/gpu-nvidia/{m}/temperature/0", "GPU Core", SensorKind.Temperature),
        S($"/gpu-nvidia/{m}/temperature/2", "GPU Hot Spot", SensorKind.Temperature),
        S($"/gpu-nvidia/{m}/temperature/3", "GPU Memory Junction", SensorKind.Temperature),
    ];

    [Theory]
    [InlineData("Tesla V100-SXM2-16GB", "V100")]
    [InlineData("NVIDIA Tesla V100-SXM2-16GB", "V100")]
    [InlineData("NVIDIA GeForce GT 710", "GT 710")]
    [InlineData("NVIDIA GeForce RTX 4090", "RTX 4090")]
    [InlineData("AMD Radeon RX 7900 XTX", "RX 7900")]
    public void ShortName_DropsVendorWords(string name, string expected) =>
        Assert.Equal(expected, GpuDevice.ShortName(name));

    [Theory]
    [InlineData("00000000:41:00.0", 65)]
    [InlineData("0000:0A:00.0", 10)]
    [InlineData("garbage", null)]
    public void PciBus_ParsesNvmlBusId(string busId, int? expected) => Assert.Equal(expected, PciBus.FromBusId(busId));

    [Fact]
    public void Merge_PairsSourcesByPciBus_EvenWhenEnumerationOrderDiffers()
    {
        // Two identical NVLink V100s: LHM lists them in the opposite order to NVML.
        var inv = GpuInventory.Merge(
            [new("/nvml/0", "Tesla V100-SXM2-16GB", 65, V100), new("/nvml/1", "Tesla V100-SXM2-16GB", 98, V100)],
            [new("/gpu-nvidia/0", "NVIDIA Tesla V100-SXM2-16GB", 98), new("/gpu-nvidia/1", "NVIDIA Tesla V100-SXM2-16GB", 65)],
            []);

        Assert.Collection(inv.Devices,
            d => Assert.Equal(("/nvml/0", "/gpu-nvidia/1", 1, "GPU · V100"), (d.NvmlPrefix, d.LhmPrefix, d.Number, d.Group)),
            d => Assert.Equal(("/nvml/1", "/gpu-nvidia/0", 2, "GPU2 · V100"), (d.NvmlPrefix, d.LhmPrefix, d.Number, d.Group)));
    }

    [Fact]
    public void Merge_NumbersCardsByBus_AndKeepsLhmOnlyCards()
    {
        var inv = GpuInventory.Merge(
            [new("/nvml/0", "Tesla V100-SXM2-16GB", 65, V100)],
            [new("/gpu-nvidia/0", "NVIDIA Tesla V100-SXM2-16GB", 65), new("/gpu-amd/0", "AMD Radeon RX 6400", 10)],
            ["GT 710: без драйвера"]);

        Assert.Equal(["GPU · RX 6400", "GPU2 · V100"], inv.Devices.Select(d => d.Group));
        Assert.Single(inv.Notices);
    }

    [Fact]
    public void Build_V100_GetsDriverDerivedLimits_AndHotSpot()
    {
        var inv = GpuInventory.Merge([new("/nvml/0", "Tesla V100-SXM2-16GB", 65, V100)], [new("/gpu-nvidia/0", "NVIDIA Tesla V100-SXM2-16GB", 65)], []);

        var setup = GpuSetup.Build(inv, V100Sensors(0, 0).ToList(), new GpuOptions());

        var rules = setup.Rules.ToDictionary(r => r.Label!);
        Assert.Equal((95, 105, 110), (rules["Hot Spot"].WarnC, rules["Hot Spot"].ShutdownC, rules["Hot Spot"].CriticalC));
        Assert.Equal((75, 83, 88), (rules["Ядро"].WarnC, rules["Ядро"].ShutdownC, rules["Ядро"].CriticalC));
        Assert.Equal((75, 85, 90), (rules["Память"].WarnC, rules["Память"].ShutdownC, rules["Память"].CriticalC));
        Assert.All(setup.Rules, r => Assert.Equal("GPU · V100", r.Group));
        Assert.Equal(["/nvml/0/temperature/core", "/gpu-nvidia/0/temperature/0"], setup.Aliases["/gpu/1/core"]);
        // NVIDIA memory temperature comes from NVML only, never LHM's junction sensor.
        Assert.Equal(["/nvml/0/temperature/memory"], setup.Aliases["/gpu/1/memory"]);
        Assert.Equal(["Нагрузка", "Мощность", "VRAM", "NVLink"], setup.Extras.Select(e => e.Name));
        Assert.Equal("/gpu/1/power-limit", setup.Extras.Single(e => e.Name == "Мощность").LimitSensorId);
    }

    [Fact]
    public void TwoNvLinkV100s_GetSeparateBlocks_AndAShutdownRuleEach()
    {
        var inv = GpuInventory.Merge(
            [new("/nvml/0", "Tesla V100-SXM2-16GB", 65, V100), new("/nvml/1", "Tesla V100-SXM2-16GB", 98, V100)],
            [new("/gpu-nvidia/0", "NVIDIA Tesla V100-SXM2-16GB", 98), new("/gpu-nvidia/1", "NVIDIA Tesla V100-SXM2-16GB", 65)],
            []);
        var sensors = V100Sensors(0, 1).Concat(V100Sensors(1, 0)).ToList();

        var setup = GpuSetup.Build(inv, sensors, new GpuOptions());

        Assert.Equal(["GPU Hot Spot", "GPU", "GPU Memory", "GPU2 Hot Spot", "GPU2", "GPU2 Memory"], setup.Rules.Select(r => r.Name));
        Assert.Equal(["/gpu-nvidia/1/temperature/2"], setup.Aliases["/gpu/1/hotspot"]);
        Assert.Equal(["/gpu-nvidia/0/temperature/2"], setup.Aliases["/gpu/2/hotspot"]);
    }

    [Fact]
    public void SecondGpuOverheating_ShutsDown_WithItsOwnName()
    {
        var inv = GpuInventory.Merge(
            [new("/nvml/0", "Tesla V100-SXM2-16GB", 65, V100), new("/nvml/1", "Tesla V100-SXM2-16GB", 98, V100)], [], []);
        var sensors = V100Sensors(0, 0).Concat(V100Sensors(1, 1)).Where(s => s.Id.StartsWith("/nvml")).ToList();
        var setup = GpuSetup.Build(inv, sensors, new GpuOptions());
        var monitor = new SafetyMonitor(new PumpGuardOptions { Pump = new PumpOptions { SensorId = "/pump" } });
        monitor.SetGpus(setup, []);

        GuardEvaluation last = null!;
        for (var s = 0; s <= 2; s++)
        {
            var readings = sensors.ToDictionary(x => x.Id, x => x.Value);
            readings["/pump"] = 2000;
            readings["/nvml/1/temperature/core"] = 89;
            setup.ApplyAliases(readings);
            last = monitor.Evaluate(readings, T0.AddSeconds(s));
        }

        Assert.True(last.ExecuteShutdown);
        Assert.StartsWith("GPU2:", last.Status.ShutdownReason);
        Assert.Contains(last.Status.Temperatures, t => t.Group == "GPU2 · V100" && t.Level == "critical");
    }

    [Fact]
    public void NvLink_IsShownOnlyWhileLinksAreUp()
    {
        var inv = GpuInventory.Merge([new("/nvml/0", "Tesla V100-SXM2-16GB", 65, V100)], [], []);
        var sensors = V100Sensors(0, 0).Select(s => s.Id.EndsWith("nvlink/active") ? s with { Value = 0 } : s).ToList();

        Assert.DoesNotContain(GpuSetup.Build(inv, sensors, new GpuOptions()).Extras, e => e.Name == "NVLink");
    }

    [Fact]
    public void TccCard_WithoutHotSpot_GetsNoHotSpotRule()
    {
        // A Tesla in TCC mode is invisible to NVAPI, so LHM reports nothing for it: no rule, no permanent "no data".
        var inv = GpuInventory.Merge([new("/nvml/0", "Tesla V100-SXM2-16GB", 65, V100)], [], []);
        var setup = GpuSetup.Build(inv, V100Sensors(0, 0).Where(s => s.Id.StartsWith("/nvml")).ToList(), new GpuOptions());

        Assert.Equal(["Ядро", "Память"], setup.Rules.Select(r => r.Label));
        Assert.False(setup.Aliases.ContainsKey("/gpu/1/hotspot"));
    }

    [Fact]
    public void ApplyAliases_TakesHottestValidSource()
    {
        var setup = new GpuSetup([], [], new Dictionary<string, IReadOnlyList<string>> { ["/gpu/1/core"] = ["/a", "/b", "/c"] });
        var readings = new Dictionary<string, double?> { ["/a"] = 40, ["/b"] = 44, ["/c"] = double.NaN };

        setup.ApplyAliases(readings);

        Assert.Equal(44, readings["/gpu/1/core"]);
    }
}
