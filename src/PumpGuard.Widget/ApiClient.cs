using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PumpGuard.Core;

namespace PumpGuard.Widget;

public sealed record FansInfo(
    bool Enabled, string ActivePreset,
    Dictionary<string, FanPreset> Presets, Dictionary<string, double> Manual, List<FanStatus> Fans);

public sealed class ApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _http;

    public ApiClient(WidgetSettings settings)
    {
        _http = new HttpClient { BaseAddress = new Uri(settings.ApiUrl), Timeout = TimeSpan.FromSeconds(2) };
        _http.DefaultRequestHeaders.Add("X-PumpGuard-Token", string.IsNullOrEmpty(settings.Token) ? "widget" : settings.Token);
    }

    public Uri BaseAddress => _http.BaseAddress!;

    public Task<GuardStatus?> GetStatusAsync() => _http.GetFromJsonAsync<GuardStatus>("/api/status", Json);

    public Task<HistoryDto?> GetHistoryAsync(int seconds) => _http.GetFromJsonAsync<HistoryDto>($"/api/history?seconds={seconds}", Json);

    public Task<FansInfo?> GetFansAsync() => _http.GetFromJsonAsync<FansInfo>("/api/fans", Json);

    public Task<bool> CancelShutdownAsync() => PostAsync("/api/alarm/cancel", new { });

    public Task<bool> SetPresetAsync(string name) => PostAsync("/api/fans/preset", new { name });

    public Task<bool> SetManualAsync(string channel, double? percent) => PostAsync("/api/fans/manual", new { channel, percent });

    private async Task<bool> PostAsync(string path, object body)
    {
        using var resp = await _http.PostAsJsonAsync(path, body, Json);
        return resp.IsSuccessStatusCode;
    }
}
