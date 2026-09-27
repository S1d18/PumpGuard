using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using PumpGuard.Core;

namespace PumpGuard.Service;

public static class Api
{
    public const string TokenHeader = "X-PumpGuard-Token";

    public sealed record PresetRequest(string Name);
    public sealed record ManualRequest(string Channel, double? Percent);

    public static void MapPumpGuardApi(this WebApplication app)
    {
        app.MapGet("/health", (StatusHub hub) =>
        {
            var age = hub.Latest is { } s ? (DateTimeOffset.Now - s.Timestamp).TotalSeconds : double.PositiveInfinity;
            return age < 5 ? Results.Ok(new { ok = true, state = hub.Latest!.State }) : Results.Json(new { ok = false }, statusCode: 503);
        });

        var api = app.MapGroup("/api");

        api.MapGet("/status", (StatusHub hub) =>
            hub.Latest is { } s ? Results.Ok(s) : Results.Json(new { error = "ещё нет данных" }, statusCode: 503));

        // Compact time series: one timestamp array plus one value array per key ("pump", "temp:CPU",
        // "extra:GPU/Мощность", "fan:Fan #1"), aligned by index. Up to 10 minutes back.
        api.MapGet("/history", (StatusHub hub, int? seconds) =>
        {
            var window = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 180, 1, StatusHub.HistoryWindow.TotalSeconds));
            var items = hub.History(window);
            var series = new Dictionary<string, double?[]>();
            for (var i = 0; i < items.Count; i++)
                foreach (var (key, value) in items[i].SeriesPoints())
                {
                    if (!series.TryGetValue(key, out var values)) series[key] = values = new double?[items.Count];
                    values[i] = value is { } v ? Math.Round(v, 1) : null;
                }
            return Results.Ok(new { timestamps = items.Select(s => s.Timestamp), series });
        });

        api.MapGet("/sensors", (StatusHub hub, SensorKind? kind) =>
            Results.Ok(kind is null ? hub.Sensors : hub.Sensors.Where(s => s.Kind == kind)));

        // Server-Sent Events: one "data: {status}" message per poll interval.
        api.MapGet("/stream", async (HttpContext ctx, StatusHub hub, IOptions<JsonOptions> json) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var ct = ctx.RequestAborted;
            try
            {
                await foreach (var s in hub.Subscribe(ct).ReadAllAsync(ct))
                {
                    await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(s, json.Value.SerializerOptions)}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
        });

        var control = api.MapGroup("").AddEndpointFilter(RequireToken);

        control.MapPost("/alarm/cancel", (SafetyMonitor monitor, IOptions<PumpGuardOptions> o, ILogger<SafetyMonitor> log) =>
        {
            var now = DateTimeOffset.Now;
            if (!monitor.CancelCountdown(now))
                return Results.Conflict(new { cancelled = false, error = "нет активного отсчёта" });
            log.LogWarning("Выключение отменено пользователем через API");
            return Results.Ok(new { cancelled = true, snoozedUntil = now.AddSeconds(o.Value.CancelSnoozeSeconds) });
        });

        api.MapGet("/fans", (FanController fans, StatusHub hub) => Results.Ok(new
        {
            enabled = fans.ControlEnabled,
            activePreset = fans.ActivePreset,
            presets = fans.Presets,
            manual = fans.ManualOverrides,
            fans = hub.Latest?.Fans ?? [],
        }));

        control.MapPost("/fans/preset", (FanController fans, PresetRequest req) =>
            fans.SetPreset(req.Name) ? Results.Ok(new { activePreset = fans.ActivePreset })
                : Results.NotFound(new { error = $"пресет «{req.Name}» не найден", available = fans.Presets.Keys }));

        control.MapPost("/fans/manual", (FanController fans, ManualRequest req) =>
            fans.SetManual(req.Channel, req.Percent) ? Results.Ok(new { manual = fans.ManualOverrides })
                : Results.NotFound(new { error = $"управляемый канал «{req.Channel}» не найден" }));

        control.MapPut("/fans/presets/{name}", (FanController fans, string name, FanPreset preset) =>
            fans.SavePreset(name, preset) is { } error ? Results.BadRequest(new { error }) : Results.Ok(fans.Presets[name]));

        control.MapDelete("/fans/presets/{name}", (FanController fans, string name) =>
            fans.DeletePreset(name) ? Results.NoContent() : Results.NotFound(new { error = "пользовательский пресет не найден" }));

        control.MapPost("/simulate/pump-failure", (Simulation sim, IOptions<PumpGuardOptions> o, int? seconds) =>
        {
            if (!o.Value.DryRun) return Results.Json(new { error = "симуляция доступна только в режиме DryRun" }, statusCode: 403);
            sim.PumpFailureUntil = DateTimeOffset.Now.AddSeconds(Math.Clamp(seconds ?? 60, 1, 600));
            return Results.Ok(new { until = sim.PumpFailureUntil });
        });
    }

    /// <summary>
    /// Mutating calls need the X-PumpGuard-Token header. Its value is checked only when ApiToken is configured,
    /// but the header itself is always required: a custom header forces a CORS preflight, so web pages cannot
    /// call these endpoints on 127.0.0.1 behind the user's back.
    /// </summary>
    private static async ValueTask<object?> RequireToken(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var expected = http.RequestServices.GetRequiredService<IOptions<PumpGuardOptions>>().Value.ApiToken;
        if (!http.Request.Headers.TryGetValue(TokenHeader, out var given) ||
            (!string.IsNullOrEmpty(expected) && given != expected))
            return Results.Json(new { error = $"нужен заголовок {TokenHeader}" }, statusCode: 401);
        return await next(ctx);
    }
}
