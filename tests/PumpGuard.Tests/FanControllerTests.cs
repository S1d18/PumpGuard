using PumpGuard.Core;

namespace PumpGuard.Tests;

public class FanControllerTests
{
    private const string Cpu = "/amdcpu/0/temperature/0";
    private const string CpuFan = "/lpc/fan/0";
    private const string CpuCtl = "/lpc/control/0";
    private const string PumpFan = "/lpc/fan/5";
    private const string PumpCtl = "/lpc/control/5";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FanControlOptions Options(bool enabled = true) => new()
    {
        Enabled = enabled,
        ActivePreset = "Balanced",
        RampDownPercentPerTick = 3,
        Channels =
        [
            new FanChannel { Name = "CPU", ControlId = CpuCtl, FanSensorId = CpuFan, SourceSensorIds = [Cpu], MinPercent = 25, FailMinRpm = 200, FailAfterSeconds = 5 },
            new FanChannel { Name = "Pump", ControlId = PumpCtl, FanSensorId = PumpFan, SourceSensorIds = [Cpu], MinPercent = 60, MaxPercent = 95, IsPump = true },
        ],
    };

    private static GuardStatus Guard(GuardState state = GuardState.Normal, string level = "ok", bool pumpOk = true) =>
        new(T0, state, new PumpStatus(PumpFan, pumpOk ? 2000 : 0, 500, pumpOk),
            [new TemperatureStatus("CPU", Cpu, 50, 75, 85, 92, level)], [], [], null, null, null, null, false);

    private static Dictionary<string, double?> R(double cpuTemp = 50, double cpuRpm = 900) =>
        new() { [Cpu] = cpuTemp, [CpuFan] = cpuRpm, [PumpFan] = 2000, [CpuCtl] = 40, [PumpCtl] = 70 };

    private static double? Target(FanControlResult r, string ctl) => r.Decisions.Single(d => d.ControlId == ctl).Percent;

    [Theory]
    [InlineData(20, 30)]
    [InlineData(50, 45)]
    [InlineData(57.5, 55)]
    [InlineData(90, 100)]
    public void Interpolate_FollowsBalancedCurve(double temp, double expected) =>
        Assert.Equal(expected, FanController.Interpolate(FanController.BuiltInPresets["Balanced"].Curve, temp), 3);

    [Fact]
    public void Curve_IsClampedToChannelMinimum()
    {
        var r = new FanController(Options()).Update(R(cpuTemp: 30), Guard(), T0);

        Assert.Equal(30, Target(r, CpuCtl));
        Assert.Equal(60, Target(r, PumpCtl));
        Assert.Equal("Curve", r.Fans[0].Mode);
    }

    [Fact]
    public void Overheat_DrivesAllChannelsToMaxImmediately()
    {
        var r = new FanController(Options()).Update(R(), Guard(GuardState.Warning, "shutdown"), T0);

        Assert.Equal(100, Target(r, CpuCtl));
        Assert.Equal(95, Target(r, PumpCtl));
        Assert.All(r.Fans, f => Assert.Equal("Max", f.Mode));
    }

    [Fact]
    public void WarningSpike_DoesNotMaxFans_ButSustainedWarningDoes()
    {
        var c = new FanController(Options());
        var warn = Guard(GuardState.Warning, "warn");

        Assert.Equal("Curve", c.Update(R(), warn, T0).Fans[0].Mode);
        Assert.Equal("Curve", c.Update(R(), Guard(), T0.AddSeconds(1)).Fans[0].Mode);

        c.Update(R(), warn, T0.AddSeconds(2));
        c.Update(R(), warn, T0.AddSeconds(4));
        Assert.Equal("Max", c.Update(R(), warn, T0.AddSeconds(5)).Fans[0].Mode);

        // Held at max for a while after the warning clears, then back to the curve.
        Assert.Equal("Max", c.Update(R(), Guard(), T0.AddSeconds(10)).Fans[0].Mode);
        Assert.Equal("Curve", c.Update(R(), Guard(), T0.AddSeconds(16)).Fans[0].Mode);
    }

    [Fact]
    public void Pump_IsNeverDrivenTo100_WhenMaxPercentIsBelow()
    {
        // Regression: at 100 % duty this pump's tach reads garbage/0, which caused a false shutdown.
        var c = new FanController(Options());
        var results = new[]
        {
            c.Update(R(), Guard(GuardState.Alarm), T0),
            c.Update(R(), Guard(pumpOk: false), T0.AddSeconds(20)),
            c.Update(R(cpuTemp: 95), Guard(), T0.AddSeconds(40)),
        };

        Assert.All(results, r => Assert.Equal(95, Target(r, PumpCtl)));
    }

    [Fact]
    public void PumpFailure_DrivesOnlyPumpToMax()
    {
        var r = new FanController(Options()).Update(R(cpuTemp: 50), Guard(pumpOk: false), T0);

        Assert.Equal(95, Target(r, PumpCtl));
        Assert.Equal(45, Target(r, CpuCtl));
    }

    [Fact]
    public void Curve_RampsDownGradually()
    {
        var c = new FanController(Options());
        c.Update(R(), Guard(GuardState.Alarm), T0);

        var next = c.Update(R(cpuTemp: 30), Guard(), T0.AddSeconds(10));

        Assert.Equal(97, Target(next, CpuCtl));
    }

    [Fact]
    public void Manual_OverridesPreset_ButNotBelowMinimum()
    {
        var c = new FanController(Options());

        Assert.True(c.SetManual("cpu", 5));
        Assert.Equal(25, Target(c.Update(R(), Guard(), T0), CpuCtl));

        c.SetManual("CPU", null);
        Assert.Equal("Curve", c.Update(R(), Guard(), T0).Fans[0].Mode);
    }

    [Fact]
    public void BiosAndFixedPresets()
    {
        var c = new FanController(Options());

        Assert.True(c.SetPreset("bios"));
        var bios = c.Update(R(), Guard(), T0);
        Assert.Null(Target(bios, CpuCtl));
        Assert.Equal(95, Target(bios, PumpCtl)); // the pump is never handed to the BIOS

        Assert.True(c.SetPreset("Full"));
        var full = c.Update(R(), Guard(), T0);
        Assert.Equal(100, Target(full, CpuCtl));
        Assert.Equal(95, Target(full, PumpCtl));

        Assert.False(c.SetPreset("nope"));
    }

    [Fact]
    public void UserPreset_CanBeSavedSelectedAndExported()
    {
        var c = new FanController(Options());
        var changes = 0;
        c.SettingsChanged += () => changes++;

        Assert.Null(c.SavePreset("Night", new FanPreset { Curve = [new(40, 30), new(80, 70)] }));
        Assert.True(c.SetPreset("Night"));
        Assert.Equal(50, Target(c.Update(R(cpuTemp: 60), Guard(), T0), CpuCtl));
        Assert.Equal(2, changes);

        var restored = new FanController(Options());
        restored.ImportSettings(c.ExportSettings());
        Assert.Equal("Night", restored.ActivePreset);
    }

    [Fact]
    public void InvalidPreset_IsRejected() =>
        Assert.NotNull(new FanController(Options()).SavePreset("Bad", new FanPreset { Curve = [new(40, 150)] }));

    [Fact]
    public void Disabled_MonitorsOnly()
    {
        var r = new FanController(Options(enabled: false)).Update(R(), Guard(), T0);

        Assert.Empty(r.Decisions);
        Assert.All(r.Fans, f => Assert.Equal("Monitor", f.Mode));
        Assert.Equal(900, r.Fans[0].Rpm);
    }

    [Fact]
    public void StalledFan_WarnsAfterTimeout()
    {
        var c = new FanController(Options());

        Assert.Empty(c.Update(R(cpuRpm: 0), Guard(), T0).Issues);
        var r = c.Update(R(cpuRpm: 0), Guard(), T0.AddSeconds(5));

        Assert.Contains(r.Issues, i => i.Source == "CPU" && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void MissingCurveTemperature_FailsSafeToMax()
    {
        var readings = R();
        readings.Remove(Cpu);

        var r = new FanController(Options()).Update(readings, Guard(), T0);

        Assert.Equal(100, Target(r, CpuCtl));
        Assert.NotEmpty(r.Issues);
    }
}
