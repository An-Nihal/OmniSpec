using System.Globalization;

namespace OmniSpec.Core.Fps;

/// <summary>A single presented frame as reported by PresentMon. Optional fields are null when the column is absent or "NA".</summary>
public sealed record FrameSample(
    double TimeSeconds,
    double FrameTimeMs,
    double? CpuBusyMs,
    double? CpuWaitMs,
    double? GpuTimeMs,
    double? GpuBusyMs,
    double? DisplayedTimeMs,
    double? DisplayLatencyMs,
    bool? Dropped,
    string? Application,
    int? ProcessId,
    string? PresentMode);

public enum FpsProviderState
{
    Ready,
    NotInstalled,
    PermissionRequired,
    Running,
    Error,
}

public sealed record FpsProviderAvailability(FpsProviderState State, string Message, string? ExecutablePath);

public sealed record FrameStatistics(
    int FrameCount,
    double DurationSeconds,
    double AverageFps,
    double MinimumFps,
    double MaximumFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double AverageFrameTimeMs,
    double FrameTimeStdDevMs,
    double? AverageCpuBusyMs,
    double? AverageGpuTimeMs,
    double? AverageDisplayedTimeMs,
    int DroppedFrames,
    int StutterFrames)
{
    public static FrameStatistics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, 0, 0);
}

/// <summary>
/// Frame statistics. Definitions (documented in docs/FPS.md):
/// <list type="bullet">
/// <item>Average FPS = frames / total frame time (not the mean of per-frame FPS values).</item>
/// <item>1% low FPS = 1000 / 99th-percentile frame time. 0.1% low = 1000 / 99.9th-percentile frame time.</item>
/// <item>Frame pacing = standard deviation of frame time.</item>
/// <item>Stutter frame = frame time &gt; 2× the median frame time and &gt; 8 ms over the median.</item>
/// <item>Dropped = presented frame that was never displayed (PresentMon "Dropped" or DisplayedTime NA).</item>
/// </list>
/// </summary>
public static class FrameStatisticsCalculator
{
    public static FrameStatistics Compute(IReadOnlyList<FrameSample> frames)
    {
        var valid = frames.Where(f => f.FrameTimeMs > 0 && double.IsFinite(f.FrameTimeMs)).ToList();
        if (valid.Count == 0) return FrameStatistics.Empty;

        var times = valid.Select(f => f.FrameTimeMs).ToArray();
        var sorted = (double[])times.Clone();
        Array.Sort(sorted);

        var total = times.Sum();
        var mean = total / times.Length;
        var variance = times.Sum(t => (t - mean) * (t - mean)) / times.Length;
        var median = Percentile(sorted, 50);
        var p99 = Percentile(sorted, 99);
        var p999 = Percentile(sorted, 99.9);
        var stutter = times.Count(t => t > median * 2 && t - median > 8);

        static double? Avg(IEnumerable<double?> xs)
        {
            var list = xs.Where(x => x.HasValue && double.IsFinite(x.Value)).Select(x => x!.Value).ToList();
            return list.Count == 0 ? null : list.Average();
        }

        return new FrameStatistics(
            valid.Count,
            total / 1000.0,
            1000.0 * valid.Count / total,
            1000.0 / sorted[^1],
            1000.0 / sorted[0],
            1000.0 / p99,
            1000.0 / p999,
            mean,
            Math.Sqrt(variance),
            Avg(valid.Select(f => f.CpuBusyMs)),
            Avg(valid.Select(f => f.GpuTimeMs ?? f.GpuBusyMs)),
            Avg(valid.Select(f => f.DisplayedTimeMs)),
            valid.Count(f => f.Dropped == true),
            stutter);
    }

    /// <summary>Linear-interpolated percentile over a sorted array.</summary>
    public static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        var rank = percentile / 100.0 * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}

/// <summary>
/// Parses PresentMon CSV output. Column lookup is header-driven so both PresentMon 1.x ("MsBetweenPresents")
/// and 2.x ("FrameTime", "CPUBusy", "GPUTime", "DisplayedTime") formats are supported.
/// </summary>
public sealed class PresentMonCsvParser
{
    private Dictionary<string, int>? _columns;

    public bool HasHeader => _columns is not null;

    public IReadOnlyCollection<string> Columns => _columns?.Keys ?? (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>Parses one line. Returns null for the header line, blank lines, or unparsable lines.</summary>
    public FrameSample? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var fields = SplitCsv(line);

        if (_columns is null || IsHeader(fields))
        {
            if (IsHeader(fields))
            {
                _columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < fields.Count; i++) _columns.TryAdd(fields[i].Trim(), i);
            }
            return null;
        }

        var frameTime = Num(fields, "FrameTime", "MsBetweenPresents", "msBetweenPresents");
        if (frameTime is null) return null;

        // Only the PresentMon 1.x "TimeInSeconds" column has an unambiguous unit. Consumers build their timeline
        // from cumulative frame times instead, which is exact for FPS purposes.
        var time = Num(fields, "TimeInSeconds") ?? double.NaN;

        var displayed = Num(fields, "DisplayedTime", "MsBetweenDisplayChange");
        bool? dropped = null;
        if (_columns.ContainsKey("Dropped")) dropped = Num(fields, "Dropped") is double d ? d != 0 : null;
        else if (_columns.ContainsKey("DisplayedTime")) dropped = Text(fields, "DisplayedTime") is { } dt && dt.Equals("NA", StringComparison.OrdinalIgnoreCase);

        return new FrameSample(
            time,
            frameTime.Value,
            Num(fields, "CPUBusy", "MsCPUBusy"),
            Num(fields, "CPUWait", "MsCPUWait"),
            Num(fields, "GPUTime", "MsGPUTime", "MsGPUActive"),
            Num(fields, "GPUBusy", "MsGPUBusy"),
            displayed,
            Num(fields, "DisplayLatency", "MsUntilDisplayed"),
            dropped,
            Text(fields, "Application"),
            Num(fields, "ProcessID") is double pid ? (int)pid : null,
            Text(fields, "PresentMode"));
    }

    private static bool IsHeader(List<string> fields) =>
        fields.Any(f => f.Equals("Application", StringComparison.OrdinalIgnoreCase)) &&
        fields.Any(f => f.Equals("ProcessID", StringComparison.OrdinalIgnoreCase));

    private string? Text(List<string> fields, params string[] names)
    {
        foreach (var n in names)
            if (_columns!.TryGetValue(n, out var i) && i < fields.Count) return fields[i];
        return null;
    }

    private double? Num(List<string> fields, params string[] names)
    {
        var t = Text(fields, names);
        if (t is null || t.Equals("NA", StringComparison.OrdinalIgnoreCase)) return null;
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    internal static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"') quoted = !quoted;
            else if (ch == ',' && !quoted) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }
}

/// <summary>
/// Abstraction over a frame-timing source. The PresentMon implementation is the production provider;
/// it is never replaced with synthetic numbers.
/// </summary>
public interface IFrameRateProvider : IDisposable
{
    string Name { get; }
    FpsProviderAvailability CheckAvailability();
    bool IsRunning { get; }
    int? TargetProcessId { get; }
    event EventHandler<IReadOnlyList<FrameSample>>? FramesReceived;
    event EventHandler<string>? Stopped;
    void Start(int processId);
    void Stop();
}
