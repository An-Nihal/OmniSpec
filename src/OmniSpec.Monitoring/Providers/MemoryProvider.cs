using System.Diagnostics;
using OmniSpec.Core.Live;
using OmniSpec.Monitoring.Native;

namespace OmniSpec.Monitoring.Providers;

/// <summary>
/// Memory statistics from GlobalMemoryStatusEx and GetPerformanceInfo, plus the "Memory" performance counters for
/// the standby/modified/free page lists (the same breakdown Task Manager and Resource Monitor show).
/// </summary>
public sealed class MemoryProvider : IMemoryProvider, IDisposable
{
    private PerformanceCounter? _modified, _standbyCore, _standbyNormal, _standbyReserve, _free;
    private bool _countersTried;

    private void EnsureCounters()
    {
        if (_countersTried) return;
        _countersTried = true;
        PerformanceCounter? Create(string name)
        {
            try { return new PerformanceCounter("Memory", name, readOnly: true); }
            catch { return null; }
        }
        _modified = Create("Modified Page List Bytes");
        _standbyCore = Create("Standby Cache Core Bytes");
        _standbyNormal = Create("Standby Cache Normal Priority Bytes");
        _standbyReserve = Create("Standby Cache Reserve Bytes");
        _free = Create("Free & Zero Page List Bytes");
    }

    private static ulong? Read(PerformanceCounter? c)
    {
        if (c is null) return null;
        try { return (ulong)Math.Max(0, c.RawValue); }
        catch { return null; }
    }

    public MemorySample Sample()
    {
        EnsureCounters();
        var status = MonitoringNative.MemoryStatus();
        var perf = MonitoringNative.PerformanceInfo();
        var page = (ulong)(perf?.PageSize ?? 4096);

        var modified = Read(_modified);
        var standbyParts = new[] { Read(_standbyCore), Read(_standbyNormal), Read(_standbyReserve) };
        ulong? standby = standbyParts.All(p => p.HasValue) ? standbyParts.Aggregate(0UL, (a, b) => a + b!.Value) : null;
        var free = Read(_free);

        // "Cached" as shown by Task Manager = standby + modified lists. Fall back to the system cache working set.
        var cached = standby.HasValue && modified.HasValue ? standby.Value + modified.Value : (ulong)(perf?.SystemCache ?? 0) * page;

        return new MemorySample(
            TotalPhysical: status?.ullTotalPhys ?? 0,
            AvailablePhysical: status?.ullAvailPhys ?? 0,
            CachedBytes: cached,
            CommittedBytes: (ulong)(perf?.CommitTotal ?? 0) * page,
            CommitLimitBytes: (ulong)(perf?.CommitLimit ?? 0) * page,
            TotalVirtual: status?.ullTotalPageFile ?? 0,
            AvailableVirtual: status?.ullAvailPageFile ?? 0,
            PagedPoolBytes: (ulong)(perf?.KernelPaged ?? 0) * page,
            NonPagedPoolBytes: (ulong)(perf?.KernelNonpaged ?? 0) * page,
            MemoryLoadPercent: status?.dwMemoryLoad ?? 0,
            ModifiedBytes: modified,
            StandbyBytes: standby,
            FreeBytes: free,
            Timestamp: DateTimeOffset.Now);
    }

    public void Dispose()
    {
        _modified?.Dispose(); _standbyCore?.Dispose(); _standbyNormal?.Dispose(); _standbyReserve?.Dispose(); _free?.Dispose();
    }
}
