using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniSpec.App.Controls;
using OmniSpec.App.Services;
using OmniSpec.Core.Fps;
using OmniSpec.Core.History;
using OmniSpec.Core.Live;
using OmniSpec.Core.Metrics;
using OmniSpec.Core.Settings;
using OmniSpec.Core.Storage;
using OmniSpec.Infrastructure;
using OmniSpec.Infrastructure.Export;
using OmniSpec.Monitoring;
using OmniSpec.Monitoring.Fps;

namespace OmniSpec.App.ViewModels;

public sealed record ProcessChoice(int Pid, string Name, string WindowTitle)
{
    public string Display => string.IsNullOrWhiteSpace(WindowTitle) ? $"{Name} (PID {Pid})" : $"{Name} — {WindowTitle} (PID {Pid})";
}

// =====================================================================================================================
// FPS & Gaming
// =====================================================================================================================
public sealed partial class GamingViewModel : PageViewModel
{
    private readonly FpsMonitor _fps;
    private readonly MonitoringService _monitoring;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly DispatcherTimer _timer;

    public GamingViewModel(FpsMonitor fps, MonitoringService monitoring, ISettingsService settings, AppPaths paths)
    {
        _fps = fps;
        _monitoring = monitoring;
        _settings = settings;
        _paths = paths;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdateStats();
        _fps.SessionChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => { UpdateAvailability(); UpdateStats(); });
        _fps.CaptureStopped += (_, reason) => Application.Current.Dispatcher.BeginInvoke(() => { LastStopReason = reason; UpdateAvailability(); UpdateStats(); });
    }

    public override string Title => "FPS & Gaming";
    public override string Icon => "";

    public ObservableCollection<ProcessChoice> Candidates { get; } = new();
    public RowList Live { get; } = new();
    public RowList SessionSummary { get; } = new();
    public RowList SystemDuringSession { get; } = new();

    [ObservableProperty] private ProcessChoice? _selectedProcess;
    [ObservableProperty] private bool _showAllProcesses;
    [ObservableProperty] private FpsProviderState _providerState = FpsProviderState.NotInstalled;
    [ObservableProperty] private string _providerMessage = string.Empty;
    [ObservableProperty] private string? _executablePath;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string _fpsText = "—";
    [ObservableProperty] private string _frameTimeText = "—";
    [ObservableProperty] private string _targetText = "No capture running";
    [ObservableProperty] private string? _lastStopReason;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _frameTimeSeries;
    [ObservableProperty] private string? _statusMessage;

    public bool CanStart => !IsCapturing && SelectedProcess is not null && ProviderState is FpsProviderState.Ready or FpsProviderState.PermissionRequired;
    public bool NeedsSetup => ProviderState == FpsProviderState.NotInstalled;
    public bool NeedsPermission => ProviderState == FpsProviderState.PermissionRequired;

    public bool ShowOverlay
    {
        get => _settings.Current.Overlay.Enabled;
        set { _settings.Current.Overlay.Enabled = value; _settings.Save(); OnPropertyChanged(); }
    }

    partial void OnSelectedProcessChanged(ProcessChoice? value) => OnPropertyChanged(nameof(CanStart));
    partial void OnIsCapturingChanged(bool value) => OnPropertyChanged(nameof(CanStart));
    partial void OnShowAllProcessesChanged(bool value) => RefreshProcesses();
    partial void OnProviderStateChanged(FpsProviderState value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(NeedsSetup));
        OnPropertyChanged(nameof(NeedsPermission));
    }

    public override void OnActivated()
    {
        UpdateAvailability();
        if (Candidates.Count == 0) RefreshProcesses();
        UpdateStats();
        _timer.Start();
    }

    public override void OnDeactivated() => _timer.Stop();

    private void UpdateAvailability()
    {
        var a = _fps.CheckAvailability();
        ProviderState = a.State;
        ProviderMessage = a.Message;
        ExecutablePath = a.ExecutablePath;
        IsCapturing = _fps.IsCapturing;
    }

    [RelayCommand]
    private void RefreshProcesses()
    {
        var self = Environment.ProcessId;
        var list = new List<ProcessChoice>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.Id == 0) continue;
                    var hasWindow = p.MainWindowHandle != IntPtr.Zero;
                    if (!ShowAllProcesses && !hasWindow) continue;
                    list.Add(new ProcessChoice(p.Id, p.ProcessName, hasWindow ? p.MainWindowTitle : string.Empty));
                }
                catch { /* exited / access denied */ }
            }
        }
        var selected = SelectedProcess?.Pid;
        Candidates.Clear();
        foreach (var c in list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)) Candidates.Add(c);
        SelectedProcess = Candidates.FirstOrDefault(c => c.Pid == selected);
    }

    [RelayCommand]
    private void Start()
    {
        if (SelectedProcess is null) return;
        if (ProviderState == FpsProviderState.PermissionRequired)
        {
            // Frame capture uses ETW, which needs administrator rights: relaunch elevated and come back to this page.
            LaunchHandoff.Save(_paths, ["--page=FPS"]);
            if (Elevation.RestartElevated()) ((App)Application.Current).ShutdownForRestart();
            return;
        }
        try
        {
            _fps.GpuIndex = _settings.Current.SelectedGpuIndex;
            _fps.Start(SelectedProcess.Pid, SelectedProcess.Name);
            LastStopReason = null;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not start capture: {ex.Message}";
        }
        UpdateAvailability();
        UpdateStats();
    }

    [RelayCommand]
    private void Stop()
    {
        _fps.Stop();
        UpdateAvailability();
        UpdateStats();
    }

    [RelayCommand]
    private void BrowsePresentMon()
    {
        var file = Shell.OpenFile("Select the PresentMon console executable", "PresentMon (PresentMon*.exe)|PresentMon*.exe|Executables (*.exe)|*.exe");
        if (file is null) return;
        _settings.Current.Fps.PresentMonPath = file;
        _settings.Save();
        UpdateAvailability();
    }

    [RelayCommand] private void OpenPresentMonReleases() => Shell.OpenUrl("https://github.com/GameTechDev/PresentMon/releases");

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (Elevation.RestartElevated()) ((App)Application.Current).ShutdownForRestart();
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var session = _fps.Session;
        if (session is null || session.FrameCount == 0) { StatusMessage = "No frames captured yet."; return; }
        var path = Shell.SaveFile("Export frame data", "CSV (*.csv)|*.csv", $"OmniSpec-frames-{session.ProcessName}-{session.StartedAt:yyyyMMdd-HHmmss}.csv", _paths.ExportDirectory);
        if (path is null) return;
        try
        {
            using var w = new StreamWriter(path);
            Exporters.WriteFramesCsv(w, session.ProcessName, session.ProcessId, session.Frames(), session.Statistics());
            StatusMessage = $"Saved {Path.GetFileName(path)}";
            Shell.SelectFile(path);
        }
        catch (Exception ex) { StatusMessage = $"Export failed: {ex.Message}"; }
    }

    private void UpdateStats()
    {
        IsCapturing = _fps.IsCapturing;
        var session = _fps.Session;
        if (session is null)
        {
            TargetText = "No capture running";
            FpsText = "—";
            FrameTimeText = "—";
            return;
        }

        TargetText = IsCapturing
            ? $"Capturing {session.ProcessName} (PID {session.ProcessId}) since {session.StartedAt:HH:mm:ss}"
            : $"Last session: {session.ProcessName} · {session.StartedAt:HH:mm:ss}–{session.EndedAt:HH:mm:ss}{(session.EndReason is null ? "" : " · " + session.EndReason)}";

        var current = session.CurrentFps;
        FpsText = IsCapturing && current is double f ? $"{f:0}" : "—";
        FrameTimeText = IsCapturing && session.LastFrameTimeMs is double ft ? $"{ft:0.0} ms" : "—";

        var recent = session.RecentStatistics(600);
        const string src = DataSources.PresentMon;
        Live.Set("Current FPS", current is double c ? $"{c:0.0}" : "—", src, current is null ? MetricState.Unavailable : MetricState.Available, "Frames over the last second");
        Live.Set("Frame time (recent avg)", recent.FrameCount > 0 ? $"{recent.AverageFrameTimeMs:0.00} ms" : "—", src);
        Live.Set("CPU frame time (busy)", recent.AverageCpuBusyMs is double cb ? $"{cb:0.00} ms" : "Not reported", src, recent.AverageCpuBusyMs is null ? MetricState.Unavailable : MetricState.Available);
        Live.Set("GPU frame time", recent.AverageGpuTimeMs is double gt ? $"{gt:0.00} ms" : "Not reported", src, recent.AverageGpuTimeMs is null ? MetricState.Unavailable : MetricState.Available);
        Live.Set("Display frame time", recent.AverageDisplayedTimeMs is double dt ? $"{dt:0.00} ms" : "Not reported", src, recent.AverageDisplayedTimeMs is null ? MetricState.Unavailable : MetricState.Available);
        Live.Set("Frame pacing (σ)", recent.FrameCount > 1 ? $"{recent.FrameTimeStdDevMs:0.00} ms" : "—", DataSources.Calculated, note: "Standard deviation of frame time over the last 600 frames");

        var stats = session.Statistics();
        SessionSummary.Set("Frames", stats.FrameCount.ToString("N0"), src);
        SessionSummary.Set("Duration", Fmt.Duration(TimeSpan.FromSeconds(stats.DurationSeconds)), DataSources.Calculated, note: "Sum of frame times");
        SessionSummary.Set("Average FPS", stats.FrameCount > 0 ? $"{stats.AverageFps:0.0}" : "—", DataSources.Calculated, note: "Frames ÷ total frame time");
        SessionSummary.Set("1% low", stats.FrameCount > 0 ? $"{stats.OnePercentLowFps:0.0}" : "—", DataSources.Calculated, note: "1000 ÷ 99th percentile frame time");
        SessionSummary.Set("0.1% low", stats.FrameCount > 0 ? $"{stats.PointOnePercentLowFps:0.0}" : "—", DataSources.Calculated, note: "1000 ÷ 99.9th percentile frame time");
        SessionSummary.Set("Minimum FPS", stats.FrameCount > 0 ? $"{stats.MinimumFps:0.0}" : "—", DataSources.Calculated, note: "From the slowest single frame");
        SessionSummary.Set("Maximum FPS", stats.FrameCount > 0 ? $"{stats.MaximumFps:0.0}" : "—", DataSources.Calculated);
        SessionSummary.Set("Stutter frames", stats.StutterFrames.ToString("N0"), DataSources.Calculated, note: "Frame time > 2× median and > 8 ms above it");
        SessionSummary.Set("Dropped (not displayed)", stats.DroppedFrames.ToString("N0"), src, note: "Presented frames that never reached the display");

        var s = _fps.SystemStats;
        static string Avg(RunningStats r, string f, string unit) => r.Count == 0 ? "Unavailable" : $"avg {r.Average.ToString(f)} · max {r.Max.ToString(f)} {unit}".Trim();
        SystemDuringSession.Set("CPU utilization", Avg(s.CpuLoad, "0", "%"), DataSources.PerformanceCounters, s.CpuLoad.Count == 0 ? MetricState.Unavailable : MetricState.Available);
        SystemDuringSession.Set("CPU temperature", Avg(s.CpuTemp, "0", "°C"), DataSources.LibreHardwareMonitor, s.CpuTemp.Count == 0 ? MetricState.Unavailable : MetricState.Available);
        SystemDuringSession.Set("GPU utilization", Avg(s.GpuLoad, "0", "%"), DataSources.LibreHardwareMonitor, s.GpuLoad.Count == 0 ? MetricState.Unavailable : MetricState.Available);
        SystemDuringSession.Set("GPU temperature", Avg(s.GpuTemp, "0", "°C"), DataSources.LibreHardwareMonitor, s.GpuTemp.Count == 0 ? MetricState.Unavailable : MetricState.Available);
        SystemDuringSession.Set("GPU power", Avg(s.GpuPower, "0", "W"), DataSources.LibreHardwareMonitor, s.GpuPower.Count == 0 ? MetricState.Unavailable : MetricState.Available);
        SystemDuringSession.Set("VRAM usage", Avg(s.VramMB, "0", "MB"), DataSources.LibreHardwareMonitor, s.VramMB.Count == 0 ? MetricState.Unavailable : MetricState.Available);
        SystemDuringSession.Set("System RAM in use", Avg(s.RamUsedGB, "0.0", "GB"), DataSources.Win32, s.RamUsedGB.Count == 0 ? MetricState.Unavailable : MetricState.Available);

        // Frame-time graph: last ~10 s of frames on the capture's own timeline.
        var frames = session.Frames();
        var tail = frames.Skip(Math.Max(0, frames.Length - 2000))
            .Select(fr => new HistoryPoint((long)(fr.TimeSeconds * 1000), (float)fr.FrameTimeMs)).ToArray();
        FrameTimeSeries = [new ChartSeries("Frame time", tail, Charts.Brush("Brush.Accent"))];
    }
}

// =====================================================================================================================
// Desktop widget
// =====================================================================================================================
public sealed partial class WidgetRow : ObservableObject
{
    public WidgetRow(string key, string label) { Key = key; Label = label; }
    public string Key { get; }
    public string Label { get; }
    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private string _sub = string.Empty;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private double _bar = double.NaN;
}

public sealed partial class WidgetViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly FpsMonitor _fps;
    private IReadOnlyList<DriveState> _drives = Array.Empty<DriveState>();

    public WidgetViewModel(ISettingsService settings, FpsMonitor fps, MonitoringService monitoring)
    {
        _settings = settings;
        _fps = fps;
        IsMock = monitoring.HardwareProvider.IsMock;
        foreach (var key in WidgetSettings.AllMetrics)
        {
            Rows.Add(new WidgetRow(key, key switch
            {
                "cpu" => "CPU", "gpu" => "GPU", "ram" => "RAM", "vram" => "VRAM", "fps" => "FPS", "storage" => "DISK",
                "network" => "NET", "battery" => "BATT", "clock" => "TIME", _ => key.ToUpperInvariant(),
            }));
        }
        _isVisible = settings.Current.Widget.Visible;
        ApplyVisibility();
        settings.Changed += (_, _) => { ApplyVisibility(); OnPropertyChanged(nameof(Opacity)); OnPropertyChanged(nameof(Compact)); };
    }

    public ObservableCollection<WidgetRow> Rows { get; } = new();
    public bool IsMock { get; }

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isCollapsed;

    partial void OnIsVisibleChanged(bool value)
    {
        _settings.Current.Widget.Visible = value;
        _settings.Save();
    }

    public WidgetSettings Settings => _settings.Current.Widget;

    public double Opacity
    {
        get => Settings.Opacity;
        set { Settings.Opacity = Math.Clamp(value, 0.2, 1); _settings.Save(); OnPropertyChanged(); }
    }

    public bool AlwaysOnTop
    {
        get => Settings.AlwaysOnTop;
        set { Settings.AlwaysOnTop = value; if (value) Settings.PinToDesktop = false; _settings.Save(); OnPropertyChanged(); OnPropertyChanged(nameof(PinToDesktop)); }
    }

    public bool PinToDesktop
    {
        get => Settings.PinToDesktop;
        set { Settings.PinToDesktop = value; if (value) Settings.AlwaysOnTop = false; _settings.Save(); OnPropertyChanged(); OnPropertyChanged(nameof(AlwaysOnTop)); }
    }

    public bool Locked
    {
        get => Settings.Locked;
        set { Settings.Locked = value; _settings.Save(); OnPropertyChanged(); }
    }

    public bool Compact
    {
        get => Settings.Compact;
        set { Settings.Compact = value; _settings.Save(); OnPropertyChanged(); }
    }

    [RelayCommand] private void Hide() => IsVisible = false;
    [RelayCommand] private void ToggleCollapsed() => IsCollapsed = !IsCollapsed;

    private void ApplyVisibility()
    {
        var enabled = _settings.Current.Widget.Metrics;
        foreach (var r in Rows) r.IsVisible = enabled.Contains(r.Key) && (r.Key != "battery" || r.Value != "n/a");
    }

    public void OnDrives(IReadOnlyList<DriveState> drives) => _drives = drives;

    private WidgetRow Row(string key) => Rows.First(r => r.Key == key);

    public void OnLive(LiveSnapshot live)
    {
        if (!IsVisible) return;
        var enabled = _settings.Current.Widget.Metrics;

        var cpu = Row("cpu");
        cpu.Value = live.CpuUsage?.TotalLoad.HasValue == true ? $"{live.CpuUsage.TotalLoad.Value:0}%" : "—";
        cpu.Sub = live.Cpu.PackageTemperature.HasValue ? $"{live.Cpu.PackageTemperature.Value:0}°C" : string.Empty;
        cpu.Bar = Fmt.Value(live.CpuUsage?.TotalLoad);

        var gi = Math.Clamp(_settings.Current.SelectedGpuIndex, 0, Math.Max(0, live.Gpus.Count - 1));
        var gpu = live.Gpus.Count > 0 ? live.Gpus[gi] : null;
        var g = Row("gpu");
        g.Value = gpu?.Load.HasValue == true ? $"{gpu.Load.Value:0}%" : "—";
        g.Sub = gpu?.Temperature.HasValue == true ? $"{gpu.Temperature.Value:0}°C" : string.Empty;
        g.Bar = Fmt.Value(gpu?.Load);

        var v = Row("vram");
        v.Value = gpu?.DedicatedUsedMB.HasValue == true ? Fmt.Mb(gpu.DedicatedUsedMB.Value) : "—";
        v.Sub = gpu?.DedicatedTotalMB.HasValue == true ? $"/ {Fmt.Mb(gpu.DedicatedTotalMB.Value)}" : "";
        v.Bar = gpu is { DedicatedUsedMB.HasValue: true, DedicatedTotalMB.HasValue: true } && gpu.DedicatedTotalMB.Value > 0
            ? 100 * gpu.DedicatedUsedMB.Value / gpu.DedicatedTotalMB.Value : double.NaN;

        var ram = Row("ram");
        if (live.Memory is { TotalPhysical: > 0 } m)
        {
            ram.Value = $"{m.UsedPhysical / 1073741824.0:0.0} GB";
            ram.Sub = $"/ {m.TotalPhysical / 1073741824.0:0} GB";
            ram.Bar = m.UsedPercent;
        }

        var fps = Row("fps");
        var session = _fps.IsCapturing ? _fps.Session : null;
        fps.Value = session?.CurrentFps is double f ? $"{f:0}" : "—";
        fps.Sub = session is null ? "not capturing" : session.ProcessName;

        var disk = Row("storage");
        if (_drives.Count > 0)
        {
            var worst = _drives.OrderByDescending(d => d.Health.Level switch
            {
                DriveHealthLevel.Critical => 4, DriveHealthLevel.Warning => 3, DriveHealthLevel.Good => 2, DriveHealthLevel.Healthy => 1, _ => 0,
            }).First();
            disk.Value = DriveHealthEvaluator.Describe(worst.Health.Level);
            // Only show a life percentage when the drive actually reports wear data.
            disk.Sub = worst.Health.EstimatedRemainingLife.HasValue
                ? $"{worst.Health.EstimatedRemainingLife.Value:0}% est. life"
                : $"{_drives.Count} drive{(_drives.Count == 1 ? "" : "s")}";
        }

        var net = live.Network.FirstOrDefault(n => n.Id == _settings.Current.SelectedNetworkAdapterId && n.Status == "Up")
                  ?? live.Network.FirstOrDefault(n => n.Status == "Up" && !n.IsVirtualOrLoopback);
        var nr = Row("network");
        nr.Value = net is null ? "offline" : $"↓ {Fmt.Rate(net.DownloadBytesPerSecond)}";
        nr.Sub = net is null ? "" : $"↑ {Fmt.Rate(net.UploadBytesPerSecond)}";

        var bat = Row("battery");
        if (live.Battery is { HasBattery: true } b)
        {
            bat.Value = Fmt.Pct(b.ChargePercent);
            bat.Sub = b.IsCharging.HasValue && b.IsCharging.Value ? "charging" : b.OnAcPower.HasValue && b.OnAcPower.Value ? "plugged in" : "on battery";
            bat.Bar = Fmt.Value(b.ChargePercent);
        }
        else
        {
            bat.Value = "n/a";
        }

        var clock = Row("clock");
        clock.Value = DateTime.Now.ToString("HH:mm");
        clock.Sub = DateTime.Now.ToString("ddd d MMM");

        foreach (var r in Rows) r.IsVisible = enabled.Contains(r.Key) && !(r.Key == "battery" && r.Value == "n/a");
    }
}
