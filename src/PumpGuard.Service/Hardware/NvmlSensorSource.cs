using System.Runtime.InteropServices;
using System.Text;
using PumpGuard.Core;

namespace PumpGuard.Service.Hardware;

/// <summary>
/// NVIDIA GPUs through NVML (nvml.dll ships with the driver). Works for Tesla/datacenter cards,
/// including HBM temperature. Sensor ids: /nvml/{index}/temperature/core, /nvml/{index}/load/core, ...
/// </summary>
public sealed class NvmlSensorSource : ISensorSource, IDisposable
{
    private const uint FieldMemoryTemp = 82; // NVML_FI_DEV_MEMORY_TEMP
    private readonly ILogger<NvmlSensorSource> _log;
    private readonly List<(IntPtr Handle, string Name)> _devices = [];
    private bool _initialized;
    private DateTimeOffset _nextInitAttempt;

    public NvmlSensorSource(ILogger<NvmlSensorSource> log) => _log = log;

    public IReadOnlyList<SensorReading> Read()
    {
        if (!EnsureInit()) return [];
        var result = new List<SensorReading>();
        for (var i = 0; i < _devices.Count; i++)
        {
            var (h, name) = _devices[i];
            void Add(string id, string sensor, SensorKind kind, double? v, string unit) =>
                result.Add(new SensorReading($"/nvml/{i}/{id}", sensor, name, kind, v, unit));

            Add("temperature/core", "GPU Core", SensorKind.Temperature,
                Native.nvmlDeviceGetTemperature(h, 0, out var temp) == 0 ? temp : null, "°C");
            Add("temperature/memory", "GPU Memory", SensorKind.Temperature, MemoryTemp(h), "°C");

            var util = Native.nvmlDeviceGetUtilizationRates(h, out var u) == 0;
            Add("load/core", "GPU Core", SensorKind.Load, util ? u.Gpu : null, "%");
            Add("load/memory-controller", "GPU Memory Controller", SensorKind.Load, util ? u.Memory : null, "%");

            Add("power", "GPU Power", SensorKind.Power,
                Native.nvmlDeviceGetPowerUsage(h, out var mw) == 0 ? mw / 1000.0 : null, "W");
            // The limit the driver actually enforces (lowered by `nvidia-smi -pl`, not just the board maximum).
            Add("power/limit", "GPU Power Limit", SensorKind.Power,
                Native.nvmlDeviceGetEnforcedPowerLimit(h, out var limitMw) == 0 ? limitMw / 1000.0 : null, "W");

            if (Native.nvmlDeviceGetFanSpeed(h, out var fan) == 0)
                Add("fan", "GPU Fan", SensorKind.Control, fan, "%");

            if (Native.nvmlDeviceGetMemoryInfo(h, out var mem) == 0)
            {
                Add("memory/used", "GPU Memory Used", SensorKind.Data, mem.Used / 1073741824.0, "GB");
                Add("memory/total", "GPU Memory Total", SensorKind.Data, mem.Total / 1073741824.0, "GB");
            }

            Add("clock/core", "GPU Core", SensorKind.Clock,
                Native.nvmlDeviceGetClockInfo(h, 0, out var clk) == 0 ? clk : null, "MHz");
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
                _devices.Add((h, name));
            }
            _initialized = true;
            _log.LogInformation("NVML: {Gpus}", string.Join(", ", _devices.Select(d => d.Name)));
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

        [DllImport(Dll)] public static extern int nvmlInit_v2();
        [DllImport(Dll)] public static extern int nvmlShutdown();
        [DllImport(Dll)] public static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport(Dll)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport(Dll)] public static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
        [DllImport(Dll)] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport(Dll)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization util);
        [DllImport(Dll)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport(Dll)] public static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
        [DllImport(Dll)] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport(Dll)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
        [DllImport(Dll)] public static extern int nvmlDeviceGetClockInfo(IntPtr device, int clockType, out uint mhz);
        [DllImport(Dll)] public static extern int nvmlDeviceGetFieldValues(IntPtr device, int count, [In, Out] FieldValue[] values);
    }
}
