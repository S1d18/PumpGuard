using System.Text.Json;
using System.Text.Json.Serialization;
using PumpGuard.Core;

namespace PumpGuard.Service;

/// <summary>Persists the fan preset, manual overrides and user presets chosen at runtime.</summary>
public sealed class FanSettingsStore(ILogger<FanSettingsStore> log)
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PumpGuard");

    private static readonly string FilePath = Path.Combine(DataDir, "fan-state.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public FanSettings? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<FanSettings>(File.ReadAllText(FilePath), Json) : null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Не удалось прочитать {File}", FilePath);
            return null;
        }
    }

    public void Save(FanSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Не удалось сохранить {File}", FilePath);
        }
    }
}
