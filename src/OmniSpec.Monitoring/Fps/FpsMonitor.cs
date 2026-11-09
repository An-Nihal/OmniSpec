using Microsoft.Extensions.Logging;
using OmniSpec.Core.Fps;
using OmniSpec.Core.History;
using OmniSpec.Core.Live;

namespace OmniSpec.Monitoring.Fps;

/// <summary>System metrics aggregated while a frame capture is running.</summary>
public sealed class SessionSystemStats
{
    public RunningStats CpuLoad { get; } = new();
    public RunningStats CpuTemp { get; } = new();
    public RunningStats GpuLoad { get; } = new();
    public RunningStats GpuTemp { get; } = new();
    public RunningStats GpuPower { get; } = new();
    public RunningStats VramMB { get; } = new();
    public RunningStats RamUsedGB { get; } = new();
}

/// <summary>
/// Coordinates FPS capture: owns the active <see cref="FpsSession"/> and records system metrics during the session.
/// FPS is always per-application; there is no global desktop FPS.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    private readonly IFrameRateProvider _provider;
    private readonly MonitoringService _monitoring;
    private readonly ILogger<FpsMonitor> _logger;

    public FpsMonitor(IFrameRateProvider provider, MonitoringService monitoring, ILogger<FpsMonitor> logger)
    {
        _provider = provider;
        _monitoring = monitoring;
        _logger = logger;
        _provider.FramesReceived += OnFrames;
        _provider.Stopped += OnStopped;
        _monitoring.LiveUpdated += OnLive;
    }

    public IFrameRateProvider Provider => _provider;
    public FpsSession? Session { get; private set; }
    public SessionSystemStats SystemStats { get; private set; } = new();
    public bool IsCapturing => _provider.IsRunning;

    /// <summary>Index of the GPU whose metrics are recorded during the session.</summary>
    public int GpuIndex { get; set; }

    public event EventHandler? SessionChanged;
    public event EventHandler? FramesUpdated;
    public event EventHandler<string>? CaptureStopped;

    public FpsProviderAvailability CheckAvailability() => _provider.CheckAvailability();

    public void Start(int processId, string processName)
    {
        Session?.End("Replaced by new session");
        Session = new FpsSession(processId, processName);
        SystemStats = new SessionSystemStats();
        SessionChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            _provider.Start(processId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start frame capture");
            Session.End(ex.Message);
            CaptureStopped?.Invoke(this, ex.Message);
            throw;
        }
    }

    public void Stop()
    {
        _provider.Stop();
        Session?.End("Stopped by user");
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFrames(object? sender, IReadOnlyList<FrameSample> frames)
    {
        Session?.Add(frames);
        FramesUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void OnStopped(object? sender, string reason)
    {
        Session?.End(reason);
        CaptureStopped?.Invoke(this, reason);
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnLive(object? sender, LiveSnapshot live)
    {
        if (!_provider.IsRunning) return;
        var s = SystemStats;
        if (live.CpuUsage?.TotalLoad.HasValue == true) s.CpuLoad.Add(live.CpuUsage.TotalLoad.Value);
        if (live.Cpu.PackageTemperature.HasValue) s.CpuTemp.Add(live.Cpu.PackageTemperature.Value);
        var gpu = live.Gpus.Count == 0 ? null : live.Gpus[Math.Clamp(GpuIndex, 0, live.Gpus.Count - 1)];
        if (gpu is not null)
        {
            if (gpu.Load.HasValue) s.GpuLoad.Add(gpu.Load.Value);
            if (gpu.Temperature.HasValue) s.GpuTemp.Add(gpu.Temperature.Value);
            if (gpu.Power.HasValue) s.GpuPower.Add(gpu.Power.Value);
            if (gpu.DedicatedUsedMB.HasValue) s.VramMB.Add(gpu.DedicatedUsedMB.Value);
        }
        if (live.Memory is { TotalPhysical: > 0 } m) s.RamUsedGB.Add(m.UsedPhysical / 1024d / 1024 / 1024);
    }

    public void Dispose()
    {
        _monitoring.LiveUpdated -= OnLive;
        _provider.FramesReceived -= OnFrames;
        _provider.Stopped -= OnStopped;
        _provider.Dispose();
    }
}
