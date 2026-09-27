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

/// <summary>Test hook, only honoured in DryRun: pretends the pump reads 0 RPM until the given time.</summary>
public sealed class Simulation
{
    public DateTimeOffset PumpFailureUntil { get; set; }
}
