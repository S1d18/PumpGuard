using System.Management;
using PumpGuard.Core;

namespace PumpGuard.Service.Hardware;

/// <summary>A PCI display adapter as Windows itself sees it: known even when no driver could be loaded.</summary>
public sealed record PciDisplay(string Name, string InstanceId, int ErrorCode, int? Bus, IReadOnlyList<GpuInfoLine> Info);

/// <summary>
/// Reads display adapters and their PCI device properties through Windows (CIM), which needs no GPU driver:
/// bus number, PCIe link generation/width and power state. For a card without a driver that is all there is;
/// its temperature and fan are read by the GPU itself and cannot be had without one.
/// </summary>
public static class PciDevices
{
    private static readonly string[] Keys =
    [
        "DEVPKEY_Device_BusNumber", "DEVPKEY_PciDevice_CurrentLinkSpeed", "DEVPKEY_PciDevice_CurrentLinkWidth",
        "DEVPKEY_PciDevice_MaxLinkSpeed", "DEVPKEY_PciDevice_MaxLinkWidth", "DEVPKEY_Device_PowerData",
    ];

    public static List<PciDisplay> DisplayAdapters(ILogger log)
    {
        var result = new List<PciDisplay>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_VideoController");
            foreach (ManagementObject card in searcher.Get())
            {
                using (card)
                {
                    var id = card["PNPDeviceID"] as string ?? "";
                    if (!id.StartsWith("PCI", StringComparison.OrdinalIgnoreCase)) continue;
                    var props = Properties(id, log);
                    result.Add(new PciDisplay(card["Name"] as string ?? "?", id, Convert.ToInt32(card["ConfigManagerErrorCode"] ?? 0),
                        Int(props, "DEVPKEY_Device_BusNumber"), Describe(props)));
                }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Список видеокарт Windows недоступен");
        }
        return result;
    }

    /// <summary>PCI bus of a device given its instance id or an LHM/D3D device interface path.</summary>
    public static int? BusOf(string deviceIdOrPath, ILogger log)
    {
        var parts = deviceIdOrPath.TrimStart('\\', '?').Split('#');
        var instanceId = parts.Length >= 3 ? $"{parts[0]}\\{parts[1]}\\{parts[2]}" : deviceIdOrPath;
        return Int(Properties(instanceId, log), "DEVPKEY_Device_BusNumber");
    }

    private static Dictionary<string, object> Properties(string instanceId, ILogger log)
    {
        var props = new Dictionary<string, object>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_PnPEntity WHERE DeviceID = '{instanceId.Replace("\\", "\\\\").Replace("'", "")}'");
            foreach (ManagementObject device in searcher.Get())
            {
                using (device)
                {
                    var args = device.GetMethodParameters("GetDeviceProperties");
                    args["devicePropertyKeys"] = Keys;
                    if (device.InvokeMethod("GetDeviceProperties", args, null)?["deviceProperties"] is ManagementBaseObject[] list)
                        foreach (var p in list)
                            if (p["KeyName"] is string key && p["Data"] is { } data) props[key] = data;
                }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Свойства устройства {Id} недоступны", instanceId);
        }
        return props;
    }

    /// <summary>"PCIe 3.0 ×16" (plus "из 3.0 ×16" when running below the card's maximum) and the power state.</summary>
    private static List<GpuInfoLine> Describe(Dictionary<string, object> p)
    {
        var lines = new List<GpuInfoLine>();
        if (Int(p, "DEVPKEY_PciDevice_CurrentLinkSpeed") is { } speed && Int(p, "DEVPKEY_PciDevice_CurrentLinkWidth") is { } width)
        {
            var text = $"{Gen(speed)} ×{width}";
            if (Int(p, "DEVPKEY_PciDevice_MaxLinkSpeed") is { } maxSpeed && Int(p, "DEVPKEY_PciDevice_MaxLinkWidth") is { } maxWidth
                && (maxSpeed > speed || maxWidth > width))
                text += $" из {Gen(maxSpeed)} ×{maxWidth}";
            lines.Add(new GpuInfoLine("PCIe", text));
        }
        // CM_POWER_DATA: PD_Size (4 bytes), then PD_MostRecentPowerState (DEVICE_POWER_STATE: 1 = D0 … 4 = D3).
        if (p.GetValueOrDefault("DEVPKEY_Device_PowerData") is byte[] { Length: >= 8 } power)
        {
            var state = BitConverter.ToInt32(power, 4);
            lines.Add(new GpuInfoLine("Питание", state switch
            {
                1 => "D0 — работает",
                2 or 3 => $"D{state - 1} — экономия",
                4 => "D3 — спит",
                _ => "неизвестно",
            }));
        }
        return lines;
    }

    private static string Gen(int speed) => speed switch { 1 => "1.1", 2 => "2.0", 3 => "3.0", 4 => "4.0", 5 => "5.0", 6 => "6.0", _ => $"Gen{speed}" };

    private static int? Int(Dictionary<string, object> p, string key) =>
        p.TryGetValue(key, out var v) && v is not byte[] ? Convert.ToInt32(v) : null;
}
