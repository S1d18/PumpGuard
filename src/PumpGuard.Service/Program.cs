using System.Globalization;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PumpGuard.Core;
using PumpGuard.Service;
using PumpGuard.Service.Hardware;

if (args.Contains("--list-sensors"))
    return ListSensors();
if (args.Contains("--identify-fans"))
    return IdentifyFans();
if (args.Length >= 3 && args[0] == "--test-fan")
    return TestFan(args[1], args[2..].Select(a => double.Parse(a, CultureInfo.InvariantCulture)).ToArray());

// The safety loop is the host; the HTTP API runs inside it as ApiHost, so a busy port cannot stop protection.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
});

// Exactly one safety config is loaded: JSON arrays merge by index across files, so layering them would
// silently mix rules. The machine config lives outside the install folder and survives reinstalls.
// --config <file> (for testing) replaces it.
var machineConfig = Path.Combine(FanSettingsStore.DataDir, "config.json");
var settingArgs = args.ToList();
string? configArg = null;
if (settingArgs.IndexOf("--config") is var ci and >= 0 && ci + 1 < settingArgs.Count)
{
    configArg = Path.GetFullPath(settingArgs[ci + 1]);
    settingArgs.RemoveRange(ci, 2);
}
settingArgs.Remove("--dry-run");
builder.Configuration.AddJsonFile(configArg
    ?? (File.Exists(machineConfig) ? machineConfig : Path.Combine(AppContext.BaseDirectory, "config.default.json")), optional: false);
// Then --Section:Key=value overrides, e.g. --PumpGuard:ApiUrl=http://127.0.0.1:8766
builder.Configuration.AddCommandLine(settingArgs.ToArray());
if (args.Contains("--dry-run")) builder.Configuration["PumpGuard:DryRun"] = "true";

builder.Services.AddWindowsService(o => o.ServiceName = "PumpGuard");
builder.Services.Configure<PumpGuardOptions>(builder.Configuration.GetSection("PumpGuard"));
builder.Services.Configure<FanControlOptions>(builder.Configuration.GetSection("FanControl"));

builder.Services.AddSingleton<LhmHardware>();
builder.Services.AddSingleton<ISensorSource>(sp => sp.GetRequiredService<LhmHardware>());
builder.Services.AddSingleton<IFanControl>(sp => sp.GetRequiredService<LhmHardware>());
builder.Services.AddSingleton<NvmlSensorSource>();
builder.Services.AddSingleton<ISensorSource>(sp => sp.GetRequiredService<NvmlSensorSource>());
builder.Services.AddSingleton<GpuDiscovery>();
builder.Services.AddSingleton(sp => new SafetyMonitor(sp.GetRequiredService<IOptions<PumpGuardOptions>>().Value));
builder.Services.AddSingleton(sp =>
{
    var fans = new FanController(sp.GetRequiredService<IOptions<FanControlOptions>>().Value);
    var store = sp.GetRequiredService<FanSettingsStore>();
    if (store.Load() is { } saved) fans.ImportSettings(saved);
    fans.SettingsChanged += () => store.Save(fans.ExportSettings());
    return fans;
});
builder.Services.AddSingleton<FanSettingsStore>();
builder.Services.AddSingleton<StatusHub>();
builder.Services.AddSingleton<Simulation>();
builder.Services.AddSingleton<IShutdownExecutor, WindowsShutdownExecutor>();
builder.Services.AddHostedService<MonitorWorker>();
builder.Services.AddHostedService<ApiHost>();

builder.Build().Run();
return 0;

static int ListSensors()
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        Console.WriteLine("ВНИМАНИЕ: запущено без прав администратора — датчики материнской платы (помпа, вентиляторы) не будут видны.\n");

    using var lhm = new LhmHardware(NullLogger<LhmHardware>.Instance);
    using var nvml = new NvmlSensorSource(NullLogger<NvmlSensorSource>.Instance);
    lhm.Read();
    Thread.Sleep(1100); // some sensors (CPU load, fan RPM) need a second sample
    var all = lhm.Read().Concat(nvml.Read()).ToList();

    foreach (var hw in all.GroupBy(s => s.Hardware))
    {
        Console.WriteLine($"== {hw.Key}");
        foreach (var s in hw.OrderBy(s => s.Kind).ThenBy(s => s.Id))
        {
            var value = s.Value?.ToString("0.#", CultureInfo.InvariantCulture) ?? "—";
            Console.WriteLine($"  {s.Kind,-11} {s.Id,-42} {s.Name,-28} {value} {s.Unit}");
        }
    }
    Console.WriteLine("\nСкопируйте нужные Id в config.json (Pump:SensorId, Temperatures[].SensorId, FanControl:Channels[]).");
    return 0;
}

// Holds one header at each given duty for 8 s and prints RPM twice a second: shows how the fan/pump
// and its tach reading react to specific duty values.
static int TestFan(string controlId, double[] duties)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    var fanId = controlId.Replace("/control/", "/fan/");
    using var lhm = new LhmHardware(NullLogger<LhmHardware>.Instance);
    string Line()
    {
        var r = lhm.Read();
        var rpm = r.FirstOrDefault(s => s.Id == fanId)?.Value;
        var ctl = r.FirstOrDefault(s => s.Id == controlId)?.Value;
        var cpu = r.FirstOrDefault(s => s.Id == "/amdcpu/0/temperature/1")?.Value;
        return $"rpm={rpm:0} ctl={ctl:0}% cpu={cpu:0.0}";
    }
    lhm.Read();
    Thread.Sleep(1000);
    Console.WriteLine($"BIOS: {Line()}");
    try
    {
        foreach (var duty in duties)
        {
            lhm.Apply([new FanDecision(controlId, duty)]);
            for (var i = 1; i <= 16; i++)
            {
                Thread.Sleep(500);
                Console.WriteLine($"{duty,5:0}%  t={i / 2.0,4:0.0}s  {Line()}");
            }
        }
    }
    finally
    {
        lhm.RestoreDefaults();
    }
    Thread.Sleep(2000);
    Console.WriteLine($"BIOS: {Line()}");
    return 0;
}

// Slows each controllable header to 30 % for a few seconds, one at a time, so the user can hear or see
// which physical fan or pump it drives. Every header is handed back to the BIOS afterwards.
static int IdentifyFans()
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    const int testPercent = 30, testSeconds = 12;
    using var lhm = new LhmHardware(NullLogger<LhmHardware>.Instance);
    lhm.Read();
    Thread.Sleep(1100);
    var controls = lhm.Read().Where(s => s.Kind == SensorKind.Control && s.Id.StartsWith("/lpc/")).ToList();
    if (controls.Count == 0)
    {
        Console.WriteLine("Управляемых разъёмов не найдено (нужны права администратора).");
        return 1;
    }

    string FanId(string controlId) => controlId.Replace("/control/", "/fan/");
    double? Rpm(IReadOnlyList<SensorReading> r, string id) => r.FirstOrDefault(s => s.Id == id)?.Value;

    Console.WriteLine($"Разъёмы по очереди снижаются до {testPercent} % на {testSeconds} с. Слушайте помпу и смотрите на поток в петле.\n");
    try
    {
        foreach (var c in controls)
        {
            var fanId = FanId(c.Id);
            var before = Rpm(lhm.Read(), fanId);
            Console.Beep(1000, 300);
            Console.WriteLine($">>> СЕЙЧАС: {c.Name} ({fanId}), было {before:0} об/мин");
            lhm.Apply([new FanDecision(c.Id, testPercent)]);
            for (var i = 0; i < testSeconds; i++)
            {
                Thread.Sleep(1000);
                Console.Write($"\r    {Rpm(lhm.Read(), fanId):0} об/мин   ");
            }
            var during = Rpm(lhm.Read(), fanId);
            lhm.Apply([new FanDecision(c.Id, null)]);
            Console.WriteLine($"\r    {c.Name}: {before:0} → {during:0} об/мин при {testPercent} %, возвращён BIOS\n");
            Thread.Sleep(4000);
        }
    }
    finally
    {
        lhm.RestoreDefaults();
    }
    return 0;
}
