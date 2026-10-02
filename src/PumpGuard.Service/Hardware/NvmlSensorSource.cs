using System.Runtime.InteropServices;
using System.Text;
using PumpGuard.Core;

// Native libraries (nvml.dll) load from System32 only: the service runs as SYSTEM, and a default search on a
// machine without the NVIDIA driver would walk PATH, where a user-writable folder could supply a planted DLL.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace PumpGuard.Service.Hardware;

/// <summary>
/// NVIDIA GPUs through NVML (nvml.dll ships with the driver). Works for Tesla/datacenter cards,
/// including HBM temperature. Sensor ids: /nvml/{index}/temperature/core, /nvml/{index}/load/core, ...
/// NVML indices follow PCI order but are not stable across hardware changes; rules use /gpu/N aliases.
/// </summary>
public sealed class NvmlSensorSource : ISensorSource, IDisposable
{
    private const uint FieldMemoryTemp = 82; // NVML_FI_DEV_MEMORY_TEMP
    private const uint MaxNvLinks = 18;      // NVML_NVLINK_MAX_LINKS

    private sealed record Device(IntPtr Handle, string Name, int? Bus, GpuThresholds Thresholds, uint[] NvLinks);

    private readonly ILogger<NvmlSensorSource> _log;
    private readonly List<Device> _devices = [];
    private bool _initialized;
    private DateTimeOffset _nextInitAttempt;

    public NvmlSensorSource(ILogger<NvmlSensorSource> log) => _log = log;

    /// <summary>The cards NVML sees, for GPU discovery. Empty until the first successful read.</summary>
    public IReadOnlyList<GpuSourceDevice> Devices =>
        _devices.Select((d, i) => new GpuSourceDevice($"/nvml/{i}", d.Name, d.Bus, d.Thresholds)).ToList();

    public IReadOnlyList<SensorReading> Read()
    {
        if (!EnsureInit()) return [];
        var result = new List<SensorReading>();
        for (var i = 0; i < _devices.Count; i++)
        {
            var d = _devices[i];
            var h = d.Handle;
            void Add(string id, string sensor, SensorKind kind, double? v, string unit) =>
                result.Add(new SensorReading($"/nvml/{i}/{id}", sensor, d.Name, kind, v, unit));

            Add("temperature/core", "GPU Core", SensorKind.Temperature,
                Native.nvmlDeviceGetTemperature(h, 0, out var temp) == 0 ? temp : null, "°C");
            if (MemoryTemp(h) is { } memTemp)
                Add("temperature/memory", "GPU Memory", SensorKind.Temperature, memTemp, "°C");

            var util = Native.nvmlDeviceGetUtilizationRates(h, out var u) == 0;
            Add("load/core", "GPU Core", SensorKind.Load, util ? u.Gpu : null, "%");
            Add("load/memory-controller", "GPU Memory Controller", SensorKind.Load, util ? u.Memory : null, "%");

            if (Native.nvmlDeviceGetPowerUsage(h, out var mw) == 0)
                Add("power", "GPU Power", SensorKind.Power, mw / 1000.0, "W");
            // The limit the driver actually enforces (lowered by `nvidia-smi -pl`, not just the board maximum).
            if (Native.nvmlDeviceGetEnforcedPowerLimit(h, out var limitMw) == 0)
                Add("power/limit", "GPU Power Limit", SensorKind.Power, limitMw / 1000.0, "W");

            if (Native.nvmlDeviceGetFanSpeed(h, out var fan) == 0)
                Add("fan", "GPU Fan", SensorKind.Control, fan, "%");

            if (Native.nvmlDeviceGetMemoryInfo(h, out var mem) == 0)
            {
                Add("memory/used", "GPU Memory Used", SensorKind.Data, mem.Used / 1073741824.0, "GB");
                Add("memory/total", "GPU Memory Total", SensorKind.Data, mem.Total / 1073741824.0, "GB");
            }

            Add("clock/core", "GPU Core", SensorKind.Clock,
                Native.nvmlDeviceGetClockInfo(h, 0, out var clk) == 0 ? clk : null, "MHz");

            // NVLink: only cards that have links report them (SXM2/SXM3 Tesla, bridged cards).
            if (d.NvLinks.Length > 0)
            {
                var active = d.NvLinks.Count(link => Native.nvmlDeviceGetNvLinkState(h, link, out var on) == 0 && on == 1);
                Add("nvlink/active", "NVLink Active", SensorKind.Other, active, "");
                Add("nvlink/total", "NVLink Total", SensorKind.Other, d.NvLinks.Length, "");
            }
        }
        return result;
    }

    public void Dispose()
    {
        if (_initialized) Native.nvmlShutdown();
    }

    private static double? MemoryTemp(IntPtr h)
    {
        var fv = new[] { new Native.FieldValue { FieldId = FieldMemoryTemp } };
        if (Native.nvmlDeviceGetFieldValues(h, 1, fv) != 0 || fv[0].NvmlReturn != 0) return null;
        var raw = fv[0].Value;
        return fv[0].ValueType switch
        {
            0 => BitConverter.Int64BitsToDouble(raw),
            1 => (uint)raw,
            5 => (int)raw,
            _ => raw,
        };
    }

    private static double? Threshold(IntPtr h, int type) =>
        Native.nvmlDeviceGetTemperatureThreshold(h, type, out var t) == 0 && t > 0 ? t : null;

    private bool EnsureInit()
    {
        if (_initialized) return true;
        if (DateTimeOffset.UtcNow < _nextInitAttempt) return false;
        try
        {
            var rc = Native.nvmlInit_v2();
            if (rc != 0) throw new InvalidOperationException($"nvmlInit_v2 = {rc}");
            Native.nvmlDeviceGetCount_v2(out var count);
            for (uint i = 0; i < count; i++)
            {
                if (Native.nvmlDeviceGetHandleByIndex_v2(i, out var h) != 0) continue;
                var buf = new byte[96];
                var name = Native.nvmlDeviceGetName(h, buf, (uint)buf.Length) == 0
                    ? Encoding.ASCII.GetString(buf).TrimEnd('\0') : $"GPU {i}";
                int? bus = Native.nvmlDeviceGetPciInfo_v3(h, out var pci) == 0 ? (int)pci.Bus : null;
                var thresholds = new GpuThresholds(
                    GpuMax: Threshold(h, 3), Slowdown: Threshold(h, 1), Shutdown: Threshold(h, 0), MemoryMax: Threshold(h, 2));
                var links = Enumerable.Range(0, (int)MaxNvLinks).Select(l => (uint)l)
                    .Where(l => Native.nvmlDeviceGetNvLinkState(h, l, out _) == 0).ToArray();
                _devices.Add(new Device(h, name, bus, thresholds, links));
            }
            _initialized = true;
            _log.LogInformation("NVML: {Gpus}", string.Join(", ", _devices.Select(d => $"{d.Name} (bus {d.Bus}, NVLink {d.NvLinks.Length})")));
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            _nextInitAttempt = DateTimeOffset.UtcNow.AddSeconds(60);
            _log.LogWarning("NVML недоступен: {Error}", ex.Message);
            return false;
        }
    }

    private static class Native
    {
        private const string Dll = "nvml.dll";

        [StructLayout(LayoutKind.Sequential)]
        public struct Utilization { public uint Gpu; public uint Memory; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Memory { public ulong Total; public ulong Free; public ulong Used; }

        [StructLayout(LayoutKind.Sequential)]
        public struct FieldValue
        {
            public uint FieldId;
            public uint ScopeId;
            public long Timestamp;
            public long LatencyUsec;
            public int ValueType;
            public int NvmlReturn;
            public long Value;
        }

        /// <summary>nvmlPciInfo_t (v3): 16-byte legacy bus id, five uints, 32-byte bus id.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PciInfo
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] BusIdLegacy;
            public uint Domain;
            public uint Bus;
            public uint Device;
            public uint PciDeviceId;
            public uint PciSubSystemId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] BusId;
        }

        [DllImport(Dll)] public static extern int nvmlInit_v2();
        [DllImport(Dll)] public static extern int nvmlShutdown();
        [DllImport(Dll)] public static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport(Dll)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport(Dll)] public static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
        [DllImport(Dll)] public static extern int nvmlDeviceGetPciInfo_v3(IntPtr device, out PciInfo pci);
        [DllImport(Dll)] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport(Dll)] public static extern int nvmlDeviceGetTemperatureThreshold(IntPtr device, int type, out uint temp);
        [DllImport(Dll)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization util);
        [DllImport(Dll)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport(Dll)] public static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
        [DllImport(Dll)] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport(Dll)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
        [DllImport(Dll)] public static extern int nvmlDeviceGetClockInfo(IntPtr device, int clockType, out uint mhz);
        [DllImport(Dll)] public static extern int nvmlDeviceGetNvLinkState(IntPtr device, uint link, out int isActive);
        [DllImport(Dll)] public static extern int nvmlDeviceGetFieldValues(IntPtr device, int count, [In, Out] FieldValue[] values);
    }
}
