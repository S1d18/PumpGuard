using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PumpGuard.Widget;

public enum ViewMode { Compact, Normal, Wide, Bar }

public enum BarEdge { Top, Bottom, Left, Right }

public sealed class WidgetSettings
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PumpGuard");
    private static readonly string FilePath = Path.Combine(Dir, "widget.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string ApiUrl { get; set; } = "http://127.0.0.1:8765";
    public string Token { get; set; } = "widget";
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Topmost { get; set; }
    public ViewMode View { get; set; } = ViewMode.Normal;
    public ViewMode LastFullView { get; set; } = ViewMode.Normal;
    public BarEdge BarEdge { get; set; } = BarEdge.Top;

    /// <summary>Register the bar as an app bar, so maximized windows do not cover it.</summary>
    public bool BarReserve { get; set; } = true;

    /// <summary>Docked panel thickness in DIPs, set by dragging its inner edge. Side panels under 180 show tiles.</summary>
    public double SideWidth { get; set; } = 300;
    public double BarHeight { get; set; } = 30;

    public static WidgetSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch { /* broken file: fall back to defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* not worth interrupting the widget */ }
    }
}

/// <summary>Per-user "run at logon" entry for the widget. The service itself starts with Windows on its own.</summary>
/// <summary>%APPDATA%\PumpGuard\widget.log — widget errors, capped at ~1 MB.</summary>
public static class WidgetLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PumpGuard", "widget.log");

    public static void Write(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\n\n");
        }
        catch { /* logging must never take the widget down */ }
    }
}

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PumpGuardWidget";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
