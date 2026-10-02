using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PumpGuard.Core;

namespace PumpGuard.Service;

/// <summary>
/// Runs the local HTTP API next to the safety loop, never in its way: if the port is busy or Kestrel fails,
/// the error is logged and retried while pump and temperature protection keep running.
/// </summary>
public sealed class ApiHost(IServiceProvider services, IOptions<PumpGuardOptions> options, ILogger<ApiHost> log) : BackgroundService
{
    /// <summary>Largest accepted request body; the biggest legitimate one (a fan preset) is well under 2 KB.</summary>
    public const long MaxBodyBytes = 16 * 1024;

    private static readonly Type[] Shared =
        [typeof(StatusHub), typeof(SafetyMonitor), typeof(FanController), typeof(Simulation), typeof(IOptions<PumpGuardOptions>)];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var url = options.Value.ApiUrl;
        while (!ct.IsCancellationRequested)
        {
            WebApplication? app = null;
            try
            {
                app = Build(url);
                await app.StartAsync(ct);
                log.LogInformation("HTTP API: {Url}", url);
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "HTTP API не запустился на {Url} (порт занят?). Защита продолжает работать, повтор через 30 с", url);
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (app is not null)
                {
                    try { await app.StopAsync(CancellationToken.None); } catch { /* already failed to start */ }
                    await app.DisposeAsync();
                }
            }
        }
    }

    private WebApplication Build(string url)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxBodyBytes);
        // Explicit, not inherited from appsettings: only requests addressed to this machine by loopback name are
        // served, which defeats DNS rebinding from a browser.
        builder.Services.AddHostFiltering(o => o.AllowedHosts = ["localhost", "127.0.0.1", "[::1]"]);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        foreach (var type in Shared) builder.Services.AddSingleton(type, services.GetRequiredService(type));

        var app = builder.Build();
        app.UseHostFiltering();
        app.MapPumpGuardApi();
        return app;
    }
}
