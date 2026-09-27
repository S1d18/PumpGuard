using System.Windows;
using System.Windows.Media;
using PumpGuard.Core;

namespace PumpGuard.Widget;

public sealed record HistoryDto(List<DateTimeOffset> Timestamps, Dictionary<string, double?[]> Series);

/// <summary>Recent values per series key (see GuardStatus.SeriesPoints), trimmed to a sliding window.</summary>
public sealed class SeriesStore
{
    private readonly Dictionary<string, List<(DateTimeOffset T, double V)>> _data = new();

    public TimeSpan Window { get; } = TimeSpan.FromMinutes(3);

    public void Seed(HistoryDto history)
    {
        foreach (var (key, values) in history.Series)
            for (var i = 0; i < values.Length && i < history.Timestamps.Count; i++)
                if (values[i] is { } v) Add(key, history.Timestamps[i], v);
    }

    public void Add(GuardStatus status)
    {
        foreach (var (key, value) in status.SeriesPoints())
            if (value is { } v) Add(key, status.Timestamp, v);
    }

    public IReadOnlyList<(DateTimeOffset T, double V)> Get(string key) =>
        _data.TryGetValue(key, out var list) ? list : [];

    private void Add(string key, DateTimeOffset t, double v)
    {
        if (!_data.TryGetValue(key, out var list)) _data[key] = list = [];
        if (list.Count > 0 && t <= list[^1].T) return; // the same status polled twice
        list.Add((t, v));
        var cutoff = t - Window;
        var old = list.FindIndex(p => p.T >= cutoff);
        if (old > 0) list.RemoveRange(0, old);
    }
}

public static class Sparkline
{
    /// <summary>
    /// Maps a series to polyline points in a width×height box. The y-range is the data's own min..max,
    /// but never narrower than <paramref name="minSpan"/>, so sensor noise does not look like big swings.
    /// </summary>
    public static (PointCollection? Points, string? Tip) Build(
        IReadOnlyList<(DateTimeOffset T, double V)> data, DateTimeOffset now, TimeSpan window,
        double width, double height, double minSpan, Func<double, string> format)
    {
        if (data.Count < 2) return (null, null);

        double min = data.Min(p => p.V), max = data.Max(p => p.V);
        var (lo, hi) = (min, max);
        if (hi - lo < minSpan)
        {
            var mid = (lo + hi) / 2;
            (lo, hi) = (mid - minSpan / 2, mid + minSpan / 2);
        }

        const double pad = 2;
        var start = now - window;
        var points = new PointCollection(data.Count);
        foreach (var (t, v) in data)
        {
            var x = Math.Clamp((t - start) / window, 0, 1) * width;
            var y = pad + (1 - (v - lo) / (hi - lo)) * (height - 2 * pad);
            points.Add(new Point(x, y));
        }
        points.Freeze();

        var minutes = window.TotalMinutes;
        return (points, $"за {minutes:0} мин: мин {format(min)}, макс {format(max)}, сейчас {format(data[^1].V)}");
    }

    /// <summary>Smallest y-range per unit, roughly the size of a meaningful change.</summary>
    public static double MinSpan(string unit) => unit switch
    {
        "°C" => 10,
        "W" => 20,
        "%" => 20,
        "GB" => 1,
        "RPM" => 500,
        _ => 1,
    };
}
