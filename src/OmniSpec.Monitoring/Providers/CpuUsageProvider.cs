using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OmniSpec.Core.Live;
using OmniSpec.Core.Metrics;
using OmniSpec.Monitoring.Native;
using OmniSpec.SystemInformation;

namespace OmniSpec.Monitoring.Providers;

/// <summary>
/// CPU utilization via the "Processor Information" performance counters — the same source Task Manager uses
/// (% Processor Utility, % Processor Performance). Works without elevation. If performance counters are broken
/// or disabled, total load falls back to GetSystemTimes and per-core values become unavailable.
/// </summary>
public sealed class CpuUsageProvider : ICpuUsageProvider
{
    private const string Category = "Processor Information";
    private readonly ILogger<CpuUsageProvider> _logger;
    private readonly double? _baseClockMHz;
    private PerformanceCounter? _total;
    private PerformanceCounter? _performance;
    private PerformanceCounter? _limit;
    private PerformanceCounter?[] _perCore = Array.Empty<PerformanceCounter?>();
    private bool _initialized;
    private bool _countersFailed;
    private long _retryAt;
    private ulong _lastIdle, _lastKernel, _lastUser;

    public CpuUsageProvider(ILogger<CpuUsageProvider> logger)
    {
        _logger = logger;
        _baseClockMHz = ReadNominalClock();
    }

    /// <summary>Nominal (base) clock reported by the firmware, from HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\~MHz.</summary>
    private static double? ReadNominalClock()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("~MHz") is int mhz && mhz > 0 ? mhz : null;
        }
        catch { return null; }
    }

    public IReadOnlyList<LogicalProcessorInfo> LogicalProcessors => CpuTopology.Current.LogicalProcessors;

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _total = new PerformanceCounter(Category, "% Processor Utility", "_Total", readOnly: true);
            _performance = TryCreate("% Processor Performance", "_Total");
            _limit = TryCreate("% Performance Limit", "_Total");
            _perCore = LogicalProcessors.Select(lp => TryCreate("% Processor Utility", lp.CounterInstance)).ToArray();
            // Prime counters: rate counters need two samples.
            _total.NextValue();
            _performance?.NextValue();
            _limit?.NextValue();
            foreach (var c in _perCore) c?.NextValue();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Processor performance counters unavailable; falling back to GetSystemTimes (retrying in 30 s)");
            _countersFailed = true;
            _retryAt = Environment.TickCount64 + 30_000;
            DisposeCounters();
        }
    }

    private PerformanceCounter? TryCreate(string counter, string instance)
    {
        try { return new PerformanceCounter(Category, counter, instance, readOnly: true); }
        catch { return null; }
    }

    public CpuUsageSample Sample()
    {
        // Performance counters can be briefly unavailable (e.g. while the counter registry is being rebuilt); retry periodically.
        if (_countersFailed && Environment.TickCount64 > _retryAt)
        {
            _countersFailed = false;
            _initialized = false;
        }
        EnsureInitialized();
        var now = DateTimeOffset.Now;
        var perf = MonitoringNative.PerformanceInfo();
        var processes = (int)(perf?.ProcessCount ?? 0);
        var threads = (int)(perf?.ThreadCount ?? 0);
        var handles = (int)(perf?.HandleCount ?? 0);

        if (_countersFailed || _total is null)
        {
            var total = SystemTimesLoad();
            return new CpuUsageSample(
                total is null ? Metric<double>.Unavailable(DataSources.Win32) : Metric<double>.Available(total.Value, DataSources.Win32, now),
                LogicalProcessors.Select(_ => (double?)null).ToList(),
                Metric<double>.Unavailable(DataSources.PerformanceCounters, "Performance counters unavailable"),
                Metric<double>.Unavailable(DataSources.PerformanceCounters, "Performance counters unavailable"),
                processes, threads, handles);
        }

        try
        {
            var totalLoad = Math.Clamp(_total.NextValue(), 0, 100);
            var perCore = _perCore.Select(c => c is null ? (double?)null : Math.Clamp(Safe(c), 0, 100)).ToList();

            Metric<double> clock;
            if (_performance is not null && _baseClockMHz is > 0)
            {
                // Effective frequency = base clock × % Processor Performance (how Task Manager computes "Speed").
                clock = Metric<double>.Available(_baseClockMHz.Value * Safe(_performance) / 100.0, DataSources.PerformanceCounters, now,
                    "Effective frequency = base clock × % Processor Performance");
            }
            else
            {
                clock = Metric<double>.Unavailable(DataSources.PerformanceCounters);
            }

            var limit = _limit is null
                ? Metric<double>.Unsupported(DataSources.PerformanceCounters)
                : Metric<double>.Available(Safe(_limit), DataSources.PerformanceCounters, now, "100% = not limited; lower values mean performance is capped by power policy, power budget or thermal conditions");

            return new CpuUsageSample(Metric<double>.Available(totalLoad, DataSources.PerformanceCounters, now), perCore, clock, limit, processes, threads, handles);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading processor counters failed; switching to fallback");
            _countersFailed = true;
            _retryAt = Environment.TickCount64 + 30_000;
            DisposeCounters();
            return Sample();
        }
    }

    private static double Safe(PerformanceCounter c)
    {
        try
        {
            var v = c.NextValue();
            return float.IsFinite(v) ? v : 0;
        }
        catch { return 0; }
    }

    private double? SystemTimesLoad()
    {
        if (!MonitoringNative.GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var dIdle = idle.Value - _lastIdle;
        var dKernel = kernel.Value - _lastKernel;
        var dUser = user.Value - _lastUser;
        var first = _lastKernel == 0;
        _lastIdle = idle.Value; _lastKernel = kernel.Value; _lastUser = user.Value;
        var total = dKernel + dUser; // kernel time includes idle time
        if (first || total == 0) return null;
        return Math.Clamp(100.0 * (total - dIdle) / total, 0, 100);
    }

    private void DisposeCounters()
    {
        _total?.Dispose(); _total = null;
        _performance?.Dispose(); _performance = null;
        _limit?.Dispose(); _limit = null;
        foreach (var c in _perCore) c?.Dispose();
        _perCore = Array.Empty<PerformanceCounter?>();
    }

    public void Dispose() => DisposeCounters();
}
