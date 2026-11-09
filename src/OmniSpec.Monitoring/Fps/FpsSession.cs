using OmniSpec.Core.Fps;

namespace OmniSpec.Monitoring.Fps;

/// <summary>One frame-capture session for a single target process, with bounded memory.</summary>
public sealed class FpsSession
{
    public const int MaxFrames = 500_000; // ~2.3 h at 60 FPS; oldest frames are dropped beyond this.

    private readonly object _gate = new();
    private readonly List<FrameSample> _frames = new();
    private readonly Queue<(double End, double FrameTime)> _window = new();
    private double _timeline; // seconds, cumulative frame time
    private double _windowSum;

    public FpsSession(int processId, string processName)
    {
        ProcessId = processId;
        ProcessName = processName;
        StartedAt = DateTimeOffset.Now;
    }

    public int ProcessId { get; }
    public string ProcessName { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }
    public string? EndReason { get; private set; }

    /// <summary>Rolling window used for "current" FPS.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(1);

    public int FrameCount { get { lock (_gate) return _frames.Count; } }

    public void Add(IReadOnlyList<FrameSample> frames)
    {
        lock (_gate)
        {
            foreach (var f in frames)
            {
                if (f.FrameTimeMs <= 0 || !double.IsFinite(f.FrameTimeMs)) continue;
                _timeline += f.FrameTimeMs / 1000.0;
                var stamped = double.IsFinite(f.TimeSeconds) ? f : f with { TimeSeconds = _timeline };
                _frames.Add(stamped);
                _window.Enqueue((_timeline, f.FrameTimeMs));
                _windowSum += f.FrameTimeMs;
            }
            while (_window.Count > 0 && _timeline - _window.Peek().End > Window.TotalSeconds)
            {
                _windowSum -= _window.Dequeue().FrameTime;
            }
            if (_frames.Count > MaxFrames) _frames.RemoveRange(0, _frames.Count - MaxFrames);
        }
    }

    /// <summary>Current FPS over the rolling window, or null when there is not enough data yet.</summary>
    public double? CurrentFps
    {
        get
        {
            lock (_gate) return _window.Count < 2 || _windowSum <= 0 ? null : 1000.0 * _window.Count / _windowSum;
        }
    }

    public double? LastFrameTimeMs
    {
        get { lock (_gate) return _frames.Count == 0 ? null : _frames[^1].FrameTimeMs; }
    }

    public FrameStatistics Statistics()
    {
        FrameSample[] copy;
        lock (_gate) copy = _frames.ToArray();
        return FrameStatisticsCalculator.Compute(copy);
    }

    /// <summary>Statistics over the most recent <paramref name="frames"/> frames.</summary>
    public FrameStatistics RecentStatistics(int frames)
    {
        FrameSample[] copy;
        lock (_gate) copy = _frames.Skip(Math.Max(0, _frames.Count - frames)).ToArray();
        return FrameStatisticsCalculator.Compute(copy);
    }

    public FrameSample[] Frames()
    {
        lock (_gate) return _frames.ToArray();
    }

    public double[] RecentFrameTimes(int count)
    {
        lock (_gate) return _frames.Skip(Math.Max(0, _frames.Count - count)).Select(f => f.FrameTimeMs).ToArray();
    }

    public void End(string reason)
    {
        EndedAt ??= DateTimeOffset.Now;
        EndReason ??= reason;
    }
}
