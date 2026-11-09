using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniSpec.App.Controls;
using OmniSpec.Core.History;
using OmniSpec.Core.Live;
using OmniSpec.Core.Settings;
using OmniSpec.Monitoring;
using OmniSpec.Monitoring.Fps;

namespace OmniSpec.App.ViewModels;

/// <summary>One value on the overlay, e.g. "62 °C".</summary>
public sealed partial class OverlayValue : ObservableObject
{
    public OverlayValue(string key, string unit, bool isPrimary = false) { Key = key; Unit = unit; IsPrimary = isPrimary; }
    public string Key { get; }
    public bool IsPrimary { get; }
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private string _unit;
    [ObservableProperty] private bool _isVisible;
    /// <summary>0 = normal, 1 = warning, 2 = critical.</summary>
    [ObservableProperty] private int _severity;
}

/// <summary>A row of the overlay such as "GPU  45 %  62 °C  1950 MHz".</summary>
public sealed partial class OverlayRow : ObservableObject
{
    public OverlayRow(string label, string brushKey) { Label = label; BrushKey = brushKey; }
    public string Label { get; }
    public string BrushKey { get; }
    public System.Windows.Media.Brush LabelBrush => Charts.Brush(BrushKey);
    public ObservableCollection<OverlayValue> Values { get; } = new();
    [ObservableProperty] private bool _isVisible;
}

/// <summary>
/// Content of the in-game overlay. Only the items the user selected are shown, and only when they have a real value
/// (nothing is shown as "n/a").
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly FpsMonitor _fps;
    private readonly MonitoringService _monitoring;
    private readonly ISettingsService _settings;
    private readonly Dictionary<string, OverlayValue> _values = new();

    public OverlayViewModel(FpsMonitor fps, MonitoringService monitoring, ISettingsService settings)
    {
        _fps = fps;
        _monitoring = monitoring;
        _settings = settings;
        Build();
        settings.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(ApplySettings);
        ApplySettings();
    }

    public ObservableCollection<OverlayRow> Rows { get; } = new();

    [ObservableProperty] private bool _showGraph;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _frameTimeSeries;
    [ObservableProperty] private bool _isHorizontal;
    [ObservableProperty] private double _scale = 1;
    [ObservableProperty] private string? _statusLine;

    private OverlaySettings S => _settings.Current.Overlay;

    private void Build()
    {
        OverlayRow Row(string label, string brush, params OverlayValue[] values)
        {
            var row = new OverlayRow(label, brush);
            foreach (var v in values) { row.Values.Add(v); _values[v.Key] = v; }
            Rows.Add(row);
            return row;
        }

        Row("FPS", "Brush.Series3",
            new OverlayValue("fps", "FPS", isPrimary: true),
            new OverlayValue("onepercent", "1% low"),
            new OverlayValue("pointone", "0.1% low"),
            new OverlayValue("frametime", "ms"));
        Row("GPU", "Brush.Series2",
            new OverlayValue("gpuload", "%"),
            new OverlayValue("gputemp", "°C"),
            new OverlayValue("gpuhotspot", "°C hot spot"),
            new OverlayValue("gpuclock", "MHz"),
            new OverlayValue("gpupower", "W"),
            new OverlayValue("gpufan", "RPM"));
        Row("CPU", "Brush.Accent",
            new OverlayValue("cpuload", "%"),
            new OverlayValue("cputemp", "°C"),
            new OverlayValue("cpuclock", "MHz"),
            new OverlayValue("cpupower", "W"));
        Row("MEM", "Brush.Series4",
            new OverlayValue("ram", "GB RAM"),
            new OverlayValue("vram", "GB VRAM"));
        Row("", "Brush.TextMuted",
            new OverlayValue("app", ""),
            new OverlayValue("time", ""));
    }

    private void ApplySettings()
    {
        IsHorizontal = S.Layout == "Horizontal";
        Scale = S.Scale;
        if (_monitoring.Latest is { } live) Update(live);
    }

    private void Set(string key, string? text, int severity = 0)
    {
        var v = _values[key];
        var visible = text is not null && S.Items.Contains(key);
        v.IsVisible = visible;
        if (!visible) return;
        v.Text = text!;
        v.Severity = S.ColorWarnings ? severity : 0;
    }

    private static int TempSeverity(double value, double limit) => value >= limit ? 2 : value >= limit - 10 ? 1 : 0;

    public void Update(LiveSnapshot live)
    {
        var alerts = _settings.Current.Alerts;
        var session = _fps.IsCapturing ? _fps.Session : null;

        // ---- Frame rate
        var current = session?.CurrentFps;
        Set("fps", current is double f ? $"{f:0}" : null);
        var recent = session?.RecentStatistics(1000);
        var enough = recent is { FrameCount: > 30 };
        Set("onepercent", enough ? $"{recent!.OnePercentLowFps:0}" : null);
        Set("pointone", enough ? $"{recent!.PointOnePercentLowFps:0}" : null);
        Set("frametime", session?.LastFrameTimeMs is double ft ? $"{ft:0.0}" : null);
        ShowGraph = session is not null && S.Items.Contains("frametimegraph");
        if (ShowGraph)
        {
            var frames = session!.Frames();
            var tail = frames.Skip(Math.Max(0, frames.Length - 600))
                .Select(fr => new HistoryPoint((long)(fr.TimeSeconds * 1000), (float)fr.FrameTimeMs)).ToArray();
            FrameTimeSeries = [new ChartSeries("Frame time", tail, Charts.Brush("Brush.Series3"))];
        }

        // ---- GPU
        var gi = Math.Clamp(_settings.Current.SelectedGpuIndex, 0, Math.Max(0, live.Gpus.Count - 1));
        var gpu = live.Gpus.Count > 0 ? live.Gpus[gi] : null;
        Set("gpuload", gpu?.Load.HasValue == true ? $"{gpu.Load.Value:0}" : null);
        Set("gputemp", gpu?.Temperature.HasValue == true ? $"{gpu.Temperature.Value:0}" : null,
            gpu?.Temperature.HasValue == true ? TempSeverity(gpu.Temperature.Value, alerts.GpuTemperatureC) : 0);
        Set("gpuhotspot", gpu?.HotSpot.HasValue == true ? $"{gpu.HotSpot.Value:0}" : null,
            gpu?.HotSpot.HasValue == true ? TempSeverity(gpu.HotSpot.Value, alerts.GpuTemperatureC + 15) : 0);
        Set("gpuclock", gpu?.CoreClock.HasValue == true ? $"{gpu.CoreClock.Value:0}" : null);
        Set("gpupower", gpu?.Power.HasValue == true ? $"{gpu.Power.Value:0}" : null);
        Set("gpufan", gpu?.FanRpm.HasValue == true ? $"{gpu.FanRpm.Value:0}" : null);

        // ---- CPU
        var c = live.Cpu;
        Set("cpuload", live.CpuUsage?.TotalLoad.HasValue == true ? $"{live.CpuUsage.TotalLoad.Value:0}" : null);
        Set("cputemp", c.PackageTemperature.HasValue ? $"{c.PackageTemperature.Value:0}" : null,
            c.PackageTemperature.HasValue ? TempSeverity(c.PackageTemperature.Value, alerts.CpuTemperatureC) : 0);
        var clock = c.AverageCoreClock.HasValue ? c.AverageCoreClock : live.CpuUsage?.EffectiveClockMHz;
        Set("cpuclock", clock is { HasValue: true } ? $"{clock.Value:0}" : null);
        Set("cpupower", c.PackagePower.HasValue ? $"{c.PackagePower.Value:0}" : null);

        // ---- Memory
        if (live.Memory is { TotalPhysical: > 0 } m)
            Set("ram", $"{m.UsedPhysical / 1073741824.0:0.0}", m.UsedPercent >= alerts.RamUsagePercent ? 2 : m.UsedPercent >= alerts.RamUsagePercent - 10 ? 1 : 0);
        else Set("ram", null);
        Set("vram", gpu?.DedicatedUsedMB.HasValue == true ? $"{gpu.DedicatedUsedMB.Value / 1024:0.0}" : null);

        // ---- Other
        Set("app", session?.ProcessName);
        Set("time", DateTime.Now.ToString("HH:mm"));

        foreach (var row in Rows) row.IsVisible = row.Values.Any(v => v.IsVisible);
    }
}

// =====================================================================================================================
// Overlay settings page
// =====================================================================================================================
public sealed partial class OverlayItemOption : ObservableObject
{
    private readonly ISettingsService _settings;

    public OverlayItemOption(string key, string label, string group, ISettingsService settings)
    {
        Key = key;
        Label = label;
        Group = group;
        _settings = settings;
    }

    public string Key { get; }
    public string Label { get; }
    public string Group { get; }

    public void Refresh() => OnPropertyChanged(nameof(IsSelected));

    public bool IsSelected
    {
        get => _settings.Current.Overlay.Items.Contains(Key);
        set
        {
            var items = _settings.Current.Overlay.Items.ToList();
            if (value && !items.Contains(Key)) items.Add(Key);
            if (!value) items.Remove(Key);
            _settings.Current.Overlay.Items = OverlaySettings.AllItems.Where(items.Contains).ToList();
            _settings.Save();
            OnPropertyChanged();
        }
    }
}

public sealed record OverlayItemGroup(string Name, IReadOnlyList<OverlayItemOption> Items);

public sealed partial class OverlayPageViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly Services.OverlayController _controller;

    public OverlayPageViewModel(ISettingsService settings, OverlayViewModel overlay, Services.OverlayController controller)
    {
        _settings = settings;
        Overlay = overlay;
        _controller = controller;
        var labels = new Dictionary<string, (string Label, string Group)>
        {
            ["fps"] = ("Current FPS", "Frame rate"), ["onepercent"] = ("1% low FPS", "Frame rate"), ["pointone"] = ("0.1% low FPS", "Frame rate"),
            ["frametime"] = ("Frame time", "Frame rate"), ["frametimegraph"] = ("Frame-time graph", "Frame rate"),
            ["gpuload"] = ("GPU usage", "GPU"), ["gputemp"] = ("GPU temperature", "GPU"), ["gpuhotspot"] = ("GPU hot spot", "GPU"),
            ["gpuclock"] = ("GPU core clock", "GPU"), ["gpupower"] = ("GPU power", "GPU"), ["gpufan"] = ("GPU fan speed", "GPU"),
            ["cpuload"] = ("CPU usage", "CPU"), ["cputemp"] = ("CPU temperature", "CPU"), ["cpuclock"] = ("CPU clock", "CPU"), ["cpupower"] = ("CPU power", "CPU"),
            ["ram"] = ("RAM in use", "Memory"), ["vram"] = ("VRAM in use", "Memory"),
            ["app"] = ("Game name", "Other"), ["time"] = ("Clock", "Other"),
        };
        Groups = OverlaySettings.AllItems
            .Select(k => new OverlayItemOption(k, labels[k].Label, labels[k].Group, settings))
            .GroupBy(o => o.Group)
            .Select(g => new OverlayItemGroup(g.Key, g.ToList()))
            .ToList();
        _hotkeyText = settings.Current.Overlay.Hotkey;
        controller.StatusChanged += (_, _) => OnPropertyChanged(nameof(DetectedText));
    }

    public override string Title => "Overlay";
    public override string Icon => "";

    public OverlayViewModel Overlay { get; }
    public IReadOnlyList<OverlayItemGroup> Groups { get; }
    public string[] Modes { get; } = OverlaySettings.Modes;
    public string[] Corners { get; } = OverlaySettings.Corners;
    public string[] Layouts { get; } = OverlaySettings.Layouts;

    private OverlaySettings S => _settings.Current.Overlay;

    private void Change(Action apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        apply();
        _settings.Save();
        OnPropertyChanged(name);
    }

    public bool Enabled { get => S.Enabled; set => Change(() => S.Enabled = value); }
    public string Mode { get => S.Mode; set => Change(() => S.Mode = value); }
    public bool AutoCapture { get => S.AutoCapture; set => Change(() => S.AutoCapture = value); }
    public string Corner { get => S.Corner; set => Change(() => S.Corner = value); }
    public string Layout { get => S.Layout; set => Change(() => S.Layout = value); }
    public double Opacity { get => S.Opacity; set => Change(() => S.Opacity = value); }
    public double Scale { get => S.Scale; set => Change(() => S.Scale = value); }
    public bool ColorWarnings { get => S.ColorWarnings; set => Change(() => S.ColorWarnings = value); }

    public string ExcludedAppsText
    {
        get => string.Join(", ", S.ExcludedApps);
        set => Change(() => S.ExcludedApps = SplitList(value));
    }

    public string AlwaysGamesText
    {
        get => string.Join(", ", S.AlwaysGames);
        set => Change(() => S.AlwaysGames = SplitList(value));
    }

    private static List<string> SplitList(string text) =>
        text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    [ObservableProperty] private string _hotkeyText;
    [ObservableProperty] private string? _hotkeyStatus;

    [RelayCommand]
    private void ApplyHotkey()
    {
        if (!HotkeyParser.TryParse(HotkeyText, out _))
        {
            HotkeyStatus = "Use a combination like Ctrl+Shift+O, Alt+F10 or Ctrl+Insert.";
            return;
        }
        S.Hotkey = HotkeyText.Trim();
        _settings.Save();
        HotkeyStatus = _controller.RegisterHotkey() ? $"Shortcut set to {S.Hotkey}." : $"{S.Hotkey} is already used by another program — choose a different shortcut.";
    }

    public bool TestOverlay
    {
        get => _controller.ForceVisible;
        set { _controller.ForceVisible = value; OnPropertyChanged(); }
    }

    public string DetectedText => _controller.CurrentGame is { } g
        ? $"Game in front: {g.ProcessName}" + (_controller.IsAutoCapturing ? " · FPS capture running" : "")
        : "No game in front right now.";

    [RelayCommand]
    private void ResetItems()
    {
        S.Items = OverlaySettings.DefaultItems.ToList();
        _settings.Save();
        foreach (var o in Groups.SelectMany(g => g.Items)) o.Refresh();
    }

    [RelayCommand]
    private void ResetExclusions()
    {
        S.ExcludedApps = OverlaySettings.DefaultExcludedApps.ToList();
        _settings.Save();
        OnPropertyChanged(nameof(ExcludedAppsText));
    }

    public override void OnActivated() => OnPropertyChanged(nameof(DetectedText));

    public override void OnLive(LiveSnapshot live)
    {
        Overlay.Update(live);
        OnPropertyChanged(nameof(DetectedText));
    }

    public override void OnDeactivated()
    {
        if (TestOverlay) TestOverlay = false;
    }
}
