using OmniSpec.SystemInformation.Native;

namespace OmniSpec.SystemInformation;

public enum CoreType { Uniform, Performance, Efficiency }

/// <summary>A logical processor and the core it belongs to.</summary>
public sealed record LogicalProcessorInfo(ushort Group, int Number, int CoreIndex, CoreType CoreType)
{
    /// <summary>Instance name used by the "Processor Information" performance counter object ("group,number").</summary>
    public string CounterInstance => $"{Group},{Number}";
}

/// <summary>CPU topology from GetLogicalProcessorInformationEx — exact, no elevation needed.</summary>
public sealed class CpuTopology
{
    private static readonly Lazy<CpuTopology> _current = new(Read);

    public static CpuTopology Current => _current.Value;

    public IReadOnlyList<LogicalProcessorInfo> LogicalProcessors { get; }
    public int PhysicalCores { get; }
    public bool IsHybrid { get; }
    public int PerformanceCores { get; }
    public int EfficiencyCores { get; }
    internal IReadOnlyList<NativeMethods.CacheInfo> Caches { get; }

    private CpuTopology(IReadOnlyList<LogicalProcessorInfo> lps, int cores, bool hybrid, int p, int e, IReadOnlyList<NativeMethods.CacheInfo> caches)
    {
        LogicalProcessors = lps;
        PhysicalCores = cores;
        IsHybrid = hybrid;
        PerformanceCores = p;
        EfficiencyCores = e;
        Caches = caches;
    }

    private static CpuTopology Read()
    {
        try
        {
            var cores = NativeMethods.GetCores();
            var classes = cores.Select(c => c.EfficiencyClass).Distinct().ToList();
            var hybrid = classes.Count > 1;
            var maxClass = classes.Count > 0 ? classes.Max() : (byte)0;
            var lps = new List<LogicalProcessorInfo>();
            foreach (var core in cores)
            {
                var type = !hybrid ? CoreType.Uniform : core.EfficiencyClass == maxClass ? CoreType.Performance : CoreType.Efficiency;
                lps.AddRange(core.LogicalProcessors.Select(lp => new LogicalProcessorInfo(lp.Group, lp.Number, core.CoreIndex, type)));
            }
            lps.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Number.CompareTo(b.Number));
            var p = hybrid ? cores.Count(c => c.EfficiencyClass == maxClass) : 0;
            return new CpuTopology(lps, cores.Count, hybrid, p, hybrid ? cores.Count - p : 0, NativeMethods.GetCaches());
        }
        catch
        {
            var n = Environment.ProcessorCount;
            var lps = Enumerable.Range(0, n).Select(i => new LogicalProcessorInfo(0, i, i, CoreType.Uniform)).ToList();
            return new CpuTopology(lps, 0, false, 0, 0, Array.Empty<NativeMethods.CacheInfo>());
        }
    }

    /// <summary>Human-readable cache summary, e.g. "L1d 48 KB ×8 · L2 1.25 MB ×8 · L3 24 MB".</summary>
    public IReadOnlyList<(string Name, string Value)> CacheSummary()
    {
        static string Size(ulong bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.##} MB" : $"{bytes / 1024.0:0.##} KB";
        static string TypeName(byte level, int type) => level == 1 ? type switch { 1 => "L1 Instruction", 2 => "L1 Data", _ => "L1" } : $"L{level}";

        return Caches
            .Where(c => c.Type != 3) // skip trace caches
            .GroupBy(c => (c.Level, Type: c.Level == 1 ? c.Type : 0))
            .OrderBy(g => g.Key.Level).ThenBy(g => g.Key.Type)
            .Select(g =>
            {
                var total = (ulong)g.Sum(c => (long)c.SizeBytes);
                var per = g.First().SizeBytes;
                var count = g.Count();
                var text = count > 1 ? $"{Size(total)} total ({count} × {Size(per)})" : Size(total);
                return (TypeName(g.Key.Level, g.Key.Type), text);
            })
            .ToList();
    }
}
