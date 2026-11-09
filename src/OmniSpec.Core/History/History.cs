namespace OmniSpec.Core.History;

/// <summary>A fixed-capacity circular buffer. Not thread-safe; callers synchronize.</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count { get; private set; }

    public void Add(T item)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = item;
            Count++;
        }
        else
        {
            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }
    }

    public T this[int index] => index < 0 || index >= Count ? throw new ArgumentOutOfRangeException(nameof(index)) : _items[(_start + index) % _items.Length];

    public T[] ToArray()
    {
        var result = new T[Count];
        for (var i = 0; i < Count; i++) result[i] = _items[(_start + i) % _items.Length];
        return result;
    }

    public void Clear() { _start = 0; Count = 0; }
}

/// <summary>Running minimum/maximum/average with no stored samples.</summary>
public sealed class RunningStats
{
    public int Count { get; private set; }
    public double Min { get; private set; } = double.NaN;
    public double Max { get; private set; } = double.NaN;
    public double Sum { get; private set; }
    public double Last { get; private set; } = double.NaN;
    public double Average => Count == 0 ? double.NaN : Sum / Count;

    public void Add(double value)
    {
        if (!double.IsFinite(value)) return;
        Count++;
        Sum += value;
        Last = value;
        Min = Count == 1 ? value : Math.Min(Min, value);
        Max = Count == 1 ? value : Math.Max(Max, value);
    }

    public void Reset()
    {
        Count = 0; Sum = 0; Min = double.NaN; Max = double.NaN; Last = double.NaN;
    }
}

public readonly record struct HistoryPoint(long UnixMs, float Value);

/// <summary>
/// Thread-safe in-memory history of numeric metrics keyed by metric id (e.g. "cpu.load").
/// Capacity is bounded so memory use stays constant regardless of uptime.
/// </summary>
public sealed class MetricHistory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RingBuffer<HistoryPoint>> _series = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunningStats> _stats = new(StringComparer.Ordinal);
    private int _capacity;

    public MetricHistory(int capacity) => _capacity = Math.Max(10, capacity);

    public int Capacity => _capacity;

    public void Resize(int capacity)
    {
        lock (_gate)
        {
            _capacity = Math.Max(10, capacity);
            foreach (var key in _series.Keys.ToList())
            {
                var old = _series[key].ToArray();
                var rb = new RingBuffer<HistoryPoint>(_capacity);
                foreach (var p in old.Skip(Math.Max(0, old.Length - _capacity))) rb.Add(p);
                _series[key] = rb;
            }
        }
    }

    public void Add(string key, DateTimeOffset timestamp, double? value)
    {
        if (value is not double v || !double.IsFinite(v)) return;
        lock (_gate)
        {
            if (!_series.TryGetValue(key, out var rb))
            {
                rb = new RingBuffer<HistoryPoint>(_capacity);
                _series[key] = rb;
                _stats[key] = new RunningStats();
            }
            rb.Add(new HistoryPoint(timestamp.ToUnixTimeMilliseconds(), (float)v));
            _stats[key].Add(v);
        }
    }

    public HistoryPoint[] Get(string key)
    {
        lock (_gate) return _series.TryGetValue(key, out var rb) ? rb.ToArray() : Array.Empty<HistoryPoint>();
    }

    /// <summary>Returns min/max/average since the app started (or since last reset).</summary>
    public (double Min, double Max, double Avg, int Count) Stats(string key)
    {
        lock (_gate)
        {
            return _stats.TryGetValue(key, out var s) && s.Count > 0 ? (s.Min, s.Max, s.Average, s.Count) : (double.NaN, double.NaN, double.NaN, 0);
        }
    }

    public void ResetStats()
    {
        lock (_gate) foreach (var s in _stats.Values) s.Reset();
    }

    public IReadOnlyList<string> Keys
    {
        get { lock (_gate) return _series.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(); }
    }
}

/// <summary>Well-known history keys.</summary>
public static class HistoryKeys
{
    public const string CpuLoad = "cpu.load";
    public const string CpuTemp = "cpu.temp";
    public const string CpuPower = "cpu.power";
    public const string CpuClock = "cpu.clock";
    public const string MemUsedPercent = "mem.used.percent";
    public const string MemUsedGb = "mem.used.gb";
    public const string MemCommitPercent = "mem.commit.percent";
    public static string GpuLoad(int i) => $"gpu{i}.load";
    public static string GpuTemp(int i) => $"gpu{i}.temp";
    public static string GpuPower(int i) => $"gpu{i}.power";
    public static string GpuVram(int i) => $"gpu{i}.vram.mb";
    public static string NetDown(string id) => $"net.{id}.down";
    public static string NetUp(string id) => $"net.{id}.up";
    public static string Sensor(string sensorId) => $"sensor:{sensorId}";
    public static string DriveTemp(string deviceId) => $"drive{deviceId}.temp";
}
