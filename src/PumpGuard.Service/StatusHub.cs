using System.Threading.Channels;
using PumpGuard.Core;

namespace PumpGuard.Service;

/// <summary>Latest status for the API plus fan-out to /api/stream subscribers.</summary>
public sealed class StatusHub
{
    private readonly object _lock = new();
    private readonly List<Channel<GuardStatus>> _subscribers = [];
    private readonly Queue<GuardStatus> _history = new();

    /// <summary>How much history is kept for /api/history and the widget sparklines.</summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(10);

    public GuardStatus? Latest { get; private set; }
    public IReadOnlyList<SensorReading> Sensors { get; private set; } = [];

    public IReadOnlyList<GuardStatus> History(TimeSpan window)
    {
        lock (_lock)
        {
            var since = DateTimeOffset.Now - window;
            return _history.Where(s => s.Timestamp >= since).ToList();
        }
    }

    public void Publish(GuardStatus status, IReadOnlyList<SensorReading> sensors)
    {
        lock (_lock)
        {
            Latest = status;
            Sensors = sensors;
            _history.Enqueue(status);
            while (_history.Count > 0 && status.Timestamp - _history.Peek().Timestamp > HistoryWindow) _history.Dequeue();
            foreach (var s in _subscribers) s.Writer.TryWrite(status);
        }
    }

    public ChannelReader<GuardStatus> Subscribe(CancellationToken ct)
    {
        var ch = Channel.CreateBounded<GuardStatus>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_lock)
        {
            _subscribers.Add(ch);
            if (Latest is not null) ch.Writer.TryWrite(Latest);
        }
        ct.Register(() => { lock (_lock) _subscribers.Remove(ch); });
        return ch.Reader;
    }
}

/// <summary>Test hooks, only honoured in DryRun.</summary>
public sealed class Simulation
{
    /// <summary>Pretends the pump reads 0 RPM until this time.</summary>
    public DateTimeOffset PumpFailureUntil { get; set; }

    /// <summary>How many fake copies of the first GPU to add (PumpGuard:SimulateExtraGpus), e.g. to preview a 2×V100 NVLink box.</summary>
    public static int CloneGpus(PumpGuardOptions o) => o.DryRun ? Math.Clamp(o.SimulateExtraGpus, 0, 3) : 0;

    /// <summary>Adds copies of the first device of one source ("/nvml/0" → "/nvml/1", ...), on later PCI buses.</summary>
    public static List<GpuSourceDevice> Clone(List<GpuSourceDevice> devices, int copies)
    {
        if (devices.Count == 0) return devices;
        var first = devices[0];
        var family = first.Prefix[..first.Prefix.LastIndexOf('/')];
        var result = devices.ToList();
        for (var k = 1; k <= copies; k++)
            result.Add(first with { Prefix = $"{family}/{devices.Count + k - 1}", PciBus = (first.PciBus ?? 0) + 33 * k });
        return result;
    }

    /// <summary>Copies the first GPU's readings for each clone, a little hotter and busier so the blocks differ.</summary>
    public static void CloneReadings(List<SensorReading> sensors, int copies)
    {
        foreach (var family in new[] { "/nvml", "/gpu-nvidia" })
        {
            var existing = sensors.Count(s => s.Id.StartsWith(family + "/", StringComparison.Ordinal) && s.Id.EndsWith("/temperature/0", StringComparison.Ordinal) || s.Id.StartsWith(family + "/", StringComparison.Ordinal) && s.Id.EndsWith("/temperature/core", StringComparison.Ordinal));
            var first = sensors.Where(s => s.Id.StartsWith(family + "/0/", StringComparison.Ordinal)).ToList();
            for (var k = 1; k <= copies; k++)
                foreach (var s in first)
                    sensors.Add(s with
                    {
                        Id = $"{family}/{existing + k - 1}/{s.Id[(family.Length + 3)..]}",
                        Value = s.Value is not { } v ? null : s.Kind switch
                        {
                            SensorKind.Temperature => v + 3 * k,
                            SensorKind.Load => Math.Min(100, v + 15 * k),
                            SensorKind.Power when !s.Id.EndsWith("/limit", StringComparison.Ordinal) => v + 25 * k,
                            _ => v,
                        },
                    });
        }
    }
}
