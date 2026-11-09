using System.Diagnostics;
using OmniSpec.Core.Live;
using OmniSpec.Monitoring.Native;

namespace OmniSpec.Monitoring.Providers;

/// <summary>
/// Lightweight, read-only process monitor. CPU% is computed from process-time deltas; disk I/O from
/// GetProcessIoCounters (includes all file I/O, not only physical disk). Per-process network usage requires
/// ETW kernel tracing with administrator rights and is therefore not provided.
/// OmniSpec never terminates or modifies processes.
/// </summary>
public sealed class ProcessProvider : IProcessProvider
{
    private sealed record Prev(ulong CpuTime100ns, ulong ReadBytes, ulong WriteBytes, long Ticks);

    private readonly Dictionary<int, Prev> _previous = new();
    private readonly Dictionary<int, string?> _paths = new();
    private readonly int _processorCount = Environment.ProcessorCount;

    public IReadOnlyList<ProcessSample> Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var totalMem = MonitoringNative.MemoryStatus()?.ullTotalPhys ?? 1UL;
        var processes = Process.GetProcesses();
        var result = new List<ProcessSample>(processes.Length);
        var seen = new HashSet<int>();

        foreach (var p in processes)
        {
            using (p)
            {
                try
                {
                    var pid = p.Id;
                    if (pid == 0) continue; // System Idle Process
                    seen.Add(pid);
                    double? cpu = null, read = null, write = null;
                    var limited = false;
                    string? path = null;
                    DateTime? start = null;

                    var handle = MonitoringNative.OpenProcess(MonitoringNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (handle != IntPtr.Zero)
                    {
                        try
                        {
                            ulong cpuTime = 0, rb = 0, wb = 0;
                            var haveCpu = MonitoringNative.GetProcessTimes(handle, out var creation, out _, out var kernel, out var user);
                            if (haveCpu)
                            {
                                cpuTime = kernel.Value + user.Value;
                                if (creation.Value > 0) start = DateTime.FromFileTime((long)creation.Value);
                            }
                            var haveIo = MonitoringNative.GetProcessIoCounters(handle, out var io);
                            if (haveIo) { rb = io.ReadTransferCount; wb = io.WriteTransferCount; }

                            if (_previous.TryGetValue(pid, out var prev))
                            {
                                var seconds = Stopwatch.GetElapsedTime(prev.Ticks, now).TotalSeconds;
                                if (seconds > 0.1)
                                {
                                    if (haveCpu && cpuTime >= prev.CpuTime100ns)
                                        cpu = Math.Clamp((cpuTime - prev.CpuTime100ns) / 1e7 / seconds / _processorCount * 100.0, 0, 100);
                                    if (haveIo)
                                    {
                                        read = rb >= prev.ReadBytes ? (rb - prev.ReadBytes) / seconds : 0;
                                        write = wb >= prev.WriteBytes ? (wb - prev.WriteBytes) / seconds : 0;
                                    }
                                }
                            }
                            _previous[pid] = new Prev(cpuTime, rb, wb, now);
                            if (!_paths.TryGetValue(pid, out path))
                            {
                                path = MonitoringNative.ImagePath(handle);
                                _paths[pid] = path;
                            }
                        }
                        finally
                        {
                            MonitoringNative.CloseHandle(handle);
                        }
                    }
                    else
                    {
                        limited = true;
                    }

                    long ws = 0, priv = 0;
                    int threads = 0;
                    try { ws = p.WorkingSet64; priv = p.PrivateMemorySize64; threads = p.Threads.Count; }
                    catch { limited = true; }

                    result.Add(new ProcessSample(pid, p.ProcessName, cpu, ws, priv, 100.0 * ws / totalMem, read, write, threads, path, start, limited));
                }
                catch
                {
                    // Process exited during enumeration.
                }
            }
        }

        foreach (var stale in _previous.Keys.Where(k => !seen.Contains(k)).ToList()) { _previous.Remove(stale); _paths.Remove(stale); }
        return result;
    }
}
