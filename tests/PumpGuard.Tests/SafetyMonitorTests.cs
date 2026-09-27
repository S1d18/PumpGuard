using PumpGuard.Core;

namespace PumpGuard.Tests;

public class SafetyMonitorTests
{
    private const string Pump = "/lpc/pump";
    private const string Gpu = "/nvml/0/temperature/core";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PumpGuardOptions Options() => new()
    {
        SensorLostAfterSeconds = 10,
        ShutdownCountdownSeconds = 30,
        CancelSnoozeSeconds = 300,
        Pump = new PumpOptions { SensorId = Pump, MinRpm = 500, FailAfterSeconds = 5, TreatSensorLossAsFailure = true },
        Temperatures =
        [
            new TemperatureRule { Name = "GPU", SensorId = Gpu, WarnC = 75, ShutdownC = 83, ShutdownHoldSeconds = 5, CriticalC = 88, CriticalHoldSeconds = 2 },
        ],
    };

    private static Dictionary<string, double?> R(double? pump = 2000, double? gpu = 40) => new() { [Pump] = pump, [Gpu] = gpu };

    /// <summary>Feeds the same readings once per second from <paramref name="from"/> to <paramref name="to"/> inclusive.</summary>
    private static GuardEvaluation Run(SafetyMonitor m, Dictionary<string, double?> r, int from, int to, List<GuardEvaluation>? log = null)
    {
        GuardEvaluation last = null!;
        for (var s = from; s <= to; s++)
        {
            last = m.Evaluate(r, T0.AddSeconds(s));
            log?.Add(last);
        }
        return last;
    }

    [Fact]
    public void HealthyReadings_AreNormal()
    {
        var e = new SafetyMonitor(Options()).Evaluate(R(), T0);

        Assert.Equal(GuardState.Normal, e.Status.State);
        Assert.False(e.ExecuteShutdown);
        Assert.True(e.Status.Pump.Ok);
        Assert.Equal("ok", e.Status.Temperatures[0].Level);
    }

    [Fact]
    public void PumpBriefDip_IsOnlyWarning()
    {
        var e = Run(new SafetyMonitor(Options()), R(pump: 0), 0, 4);

        Assert.Equal(GuardState.Warning, e.Status.State);
        Assert.Null(e.Status.ShutdownAt);
    }

    [Fact]
    public void PumpStopped_StartsCountdown_ThenShutsDownExactlyOnce()
    {
        var m = new SafetyMonitor(Options());
        var log = new List<GuardEvaluation>();

        var alarm = Run(m, R(pump: 0), 0, 5, log);
        Assert.Equal(GuardState.Alarm, alarm.Status.State);
        Assert.Equal(T0.AddSeconds(35), alarm.Status.ShutdownAt);
        Assert.Equal(30, alarm.Status.SecondsToShutdown);
        Assert.Contains("Помпа остановилась", alarm.Status.ShutdownReason);

        Run(m, R(pump: 0), 6, 40, log);
        Assert.Single(log, e => e.ExecuteShutdown);
        Assert.True(log.Single(e => e.ExecuteShutdown).Status.Timestamp == T0.AddSeconds(35));
        Assert.Equal(GuardState.ShuttingDown, log[^1].Status.State);
    }

    [Fact]
    public void PumpRecovery_DuringCountdown_CancelsIt()
    {
        var m = new SafetyMonitor(Options());
        Run(m, R(pump: 0), 0, 10);

        var e = m.Evaluate(R(pump: 1800), T0.AddSeconds(11));

        Assert.Equal(GuardState.Normal, e.Status.State);
        Assert.Null(e.Status.ShutdownAt);
        Assert.False(Run(m, R(), 12, 60).ExecuteShutdown);
    }

    [Fact]
    public void Cancel_SnoozesAlarm_UntilSnoozeExpires()
    {
        var m = new SafetyMonitor(Options());
        Run(m, R(pump: 0), 0, 10);

        Assert.True(m.CancelCountdown(T0.AddSeconds(10)));
        Assert.False(m.CancelCountdown(T0.AddSeconds(10)));

        var snoozed = Run(m, R(pump: 0), 11, 309);
        Assert.Equal(GuardState.Warning, snoozed.Status.State);
        Assert.Equal(T0.AddSeconds(310), snoozed.Status.SnoozedUntil);

        var after = m.Evaluate(R(pump: 0), T0.AddSeconds(310));
        Assert.Equal(GuardState.Alarm, after.Status.State);
        Assert.Equal(T0.AddSeconds(340), after.Status.ShutdownAt);
    }

    [Fact]
    public void CriticalTemperature_ShutsDownImmediately_EvenWhenSnoozed()
    {
        var m = new SafetyMonitor(Options());
        Run(m, R(pump: 0), 0, 10);
        m.CancelCountdown(T0.AddSeconds(10));

        var log = new List<GuardEvaluation>();
        Run(m, R(pump: 0, gpu: 90), 11, 13, log);

        Assert.False(log[0].ExecuteShutdown);
        Assert.False(log[1].ExecuteShutdown);
        Assert.True(log[2].ExecuteShutdown);
        Assert.Equal(GuardState.ShuttingDown, log[2].Status.State);
        Assert.Contains("критическая", log[2].Status.ShutdownReason);
    }

    [Fact]
    public void SingleGlitchReading_DoesNotShutDown()
    {
        var m = new SafetyMonitor(Options());
        var log = new List<GuardEvaluation>
        {
            m.Evaluate(R(gpu: 95), T0),
            m.Evaluate(R(gpu: 40), T0.AddSeconds(1)),
            m.Evaluate(R(gpu: 95), T0.AddSeconds(2)),
            m.Evaluate(R(gpu: 40), T0.AddSeconds(3)),
        };

        Assert.DoesNotContain(log, e => e.ExecuteShutdown || e.Status.State == GuardState.Alarm);
    }

    [Theory]
    [InlineData(255.0)]
    [InlineData(-128.0)]
    [InlineData(double.NaN)]
    public void ImpossibleTemperature_IsTreatedAsMissing(double bogus)
    {
        var e = Run(new SafetyMonitor(Options()), R(gpu: bogus), 0, 5);

        Assert.Equal("unknown", e.Status.Temperatures[0].Level);
        Assert.False(e.ExecuteShutdown);
    }

    [Fact]
    public void Overheat_SustainedForHold_StartsCountdown()
    {
        var m = new SafetyMonitor(Options());

        Assert.Equal(GuardState.Warning, Run(m, R(gpu: 85), 0, 4).Status.State);
        var e = m.Evaluate(R(gpu: 85), T0.AddSeconds(5));

        Assert.Equal(GuardState.Alarm, e.Status.State);
        Assert.Equal("shutdown", e.Status.Temperatures[0].Level);
    }

    [Fact]
    public void PumpSensorLost_IsAlarmAfterTimeout()
    {
        var m = new SafetyMonitor(Options());
        var missing = new Dictionary<string, double?> { [Gpu] = 40 };

        Assert.Equal(GuardState.Normal, Run(m, missing, 0, 9).Status.State);
        Assert.Equal(GuardState.Alarm, m.Evaluate(missing, T0.AddSeconds(10)).Status.State);
    }

    [Fact]
    public void PumpSensorLost_IsWarningWhenConfiguredSo()
    {
        var o = Options();
        o.Pump.TreatSensorLossAsFailure = false;

        var e = Run(new SafetyMonitor(o), new Dictionary<string, double?> { [Gpu] = 40 }, 0, 20);

        Assert.Equal(GuardState.Warning, e.Status.State);
    }

    [Fact]
    public void MultiSensorRule_UsesHottestReading()
    {
        var o = Options();
        o.Temperatures = [new TemperatureRule { Name = "HBM", SensorIds = ["/nvml/mem", "/nvapi/mem"], WarnC = 75, ShutdownC = 85, CriticalC = 90 }];

        var e = new SafetyMonitor(o).Evaluate(new Dictionary<string, double?> { [Pump] = 2000, ["/nvml/mem"] = 70, ["/nvapi/mem"] = 86 }, T0);

        Assert.Equal(86, e.Status.Temperatures[0].Value);
        Assert.Equal("/nvapi/mem", e.Status.Temperatures[0].SensorId);
        Assert.Equal("shutdown", e.Status.Temperatures[0].Level);
    }

    [Fact]
    public void MultiSensorRule_WarnsAboutLostSensor_ButKeepsGuarding()
    {
        var o = Options();
        o.Temperatures = [new TemperatureRule { Name = "HBM", SensorIds = ["/nvml/mem", "/nvapi/mem"], WarnC = 75, ShutdownC = 85, CriticalC = 90, CriticalHoldSeconds = 0 }];
        var m = new SafetyMonitor(o);

        var e = Run(m, new() { [Pump] = 2000, ["/nvml/mem"] = 60 }, 0, 10);
        Assert.Contains(e.Status.Issues, i => i.Message.Contains("не отвечает /nvapi/mem"));
        Assert.Equal("ok", e.Status.Temperatures[0].Level);

        Assert.True(m.Evaluate(new Dictionary<string, double?> { [Pump] = 2000, ["/nvml/mem"] = 95 }, T0.AddSeconds(11)).ExecuteShutdown);
    }

    [Fact]
    public void Extra_CarriesItsLimit()
    {
        var o = Options();
        o.Extras =
        [
            new ExtraSensor { Name = "Мощность", SensorId = "/nvml/0/power", Unit = "W", Group = "GPU", LimitSensorId = "/nvml/0/power/limit" },
            new ExtraSensor { Name = "Нагрузка", SensorId = "/nvml/0/load/core", Unit = "%", Group = "GPU" },
        ];
        var readings = R();
        readings["/nvml/0/power"] = 21.4;
        readings["/nvml/0/power/limit"] = 300;
        readings["/nvml/0/load/core"] = 8;

        var extras = new SafetyMonitor(o).Evaluate(readings, T0).Status.Extras;

        Assert.Equal(new ExtraValue("Мощность", 21.4, "W", "GPU", 300), extras[0]);
        Assert.Null(extras[1].Limit);
    }

    [Fact]
    public void UnconfiguredPump_IsWarning()
    {
        var o = Options();
        o.Pump.SensorId = null;

        var e = new SafetyMonitor(o).Evaluate(R(), T0);

        Assert.Equal(GuardState.Warning, e.Status.State);
        Assert.Contains(e.Status.Issues, i => i.Message.Contains("не настроен"));
    }
}
