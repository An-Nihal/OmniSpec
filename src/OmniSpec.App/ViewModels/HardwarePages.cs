using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OmniSpec.App.Controls;
using OmniSpec.Core.History;
using OmniSpec.Core.Live;
using OmniSpec.Core.Metrics;
using OmniSpec.Core.Settings;
using OmniSpec.Core.Storage;
using OmniSpec.Core.SystemInfo;
using OmniSpec.Monitoring;
using OmniSpec.SystemInformation;

namespace OmniSpec.App.ViewModels;

// =====================================================================================================================
// Overview
// =====================================================================================================================
public sealed partial class OverviewViewModel : PageViewModel
{
    private readonly MonitoringService _monitoring;
    private readonly ISettingsService _settings;

    public OverviewViewModel(MonitoringService monitoring, ISettingsService settings)
    {
        _monitoring = monitoring;
        _settings = settings;
    }

    public override string Title => "Overview";
    public override string Icon => "";

    [ObservableProperty] private string _machineTitle = Environment.MachineName;
    [ObservableProperty] private string _machineSubtitle = string.Empty;
    [ObservableProperty] private string _uptimeText = string.Empty;

    [ObservableProperty] private string _cpuName = "Processor";
    [ObservableProperty] private double _cpuLoad = double.NaN;
    [ObservableProperty] private string _cpuLoadText = "—";
    [ObservableProperty] private string _cpuTempText = "—";
    [ObservableProperty] private string _cpuClockText = "—";

    [ObservableProperty] private string _gpuName = "Graphics";
    [ObservableProperty] private double _gpuLoad = double.NaN;
    [ObservableProperty] private string _gpuLoadText = "—";
    [ObservableProperty] private string _gpuTempText = "—";
    [ObservableProperty] private string _vramText = "—";

    [ObservableProperty] private double _ramPercent = double.NaN;
    [ObservableProperty] private string _ramPercentText = "—";
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _ramAvailableText = "—";

    [ObservableProperty] private string _downText = "—";
    [ObservableProperty] private string _upText = "—";
    [ObservableProperty] private string _networkName = "No active adapter";
    [ObservableProperty] private string _powerTitle = "Power";
    [ObservableProperty] private string _powerText = "—";
    [ObservableProperty] private string _powerSub = string.Empty;

    [ObservableProperty] private IReadOnlyList<ChartSeries>? _loadSeries;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _tempSeries;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _networkSeries;

    public ObservableCollection<DriveSummary> Drives { get; } = new();

    public sealed record DriveSummary(string Title, string Subtitle, DriveHealthLevel Level, string LevelText, string TempText, double UsedPercent, string UsageText);

    public override void OnActivated()
    {
        OnSystemInfo(_monitoring.SystemInfo);
        OnDrives(_monitoring.Drives);
    }

    public override void OnSystemInfo(SystemInfoReport report)
    {
        if (report.CollectedAt == DateTimeOffset.MinValue) return;
        var os = report.Sections.FirstOrDefault(s => s.Title == "Operating System");
        var board = report.Sections.FirstOrDefault(s => s.Title == "Motherboard & Firmware");
        string? Item(InfoSection? s, string label) => s?.Items.FirstOrDefault(i => i.Label == label && i.State == MetricState.Available)?.Value;
        var maker = Item(os, "Computer manufacturer");
        var model = Item(os, "Computer model");
        var boardModel = Item(board, "Model");
        var boardMaker = ShortVendor(Item(board, "Manufacturer"));
        // Desktops built from retail boards report generic system models (e.g. "MS-7E09", "System Product Name"):
        // the motherboard name is more meaningful there.
        var genericModel = model is null || model.Length < 4 || model.Contains("System Product", StringComparison.OrdinalIgnoreCase)
                           || model.Contains("To be filled", StringComparison.OrdinalIgnoreCase) || (boardModel?.Contains(model, StringComparison.OrdinalIgnoreCase) ?? false);
        MachineTitle = boardModel is not null && genericModel ? $"{boardMaker} {boardModel}".Trim() : $"{ShortVendor(maker)} {model}".Trim();
        if (string.IsNullOrWhiteSpace(MachineTitle)) MachineTitle = "This PC";
        var ver = Item(os, "Version");
        MachineSubtitle = $"{report.OsName}{(ver is null ? "" : " · " + ver)}";
        if (report.Cpu is { } cpu) CpuName = cpu.Name;
    }

    private static string ShortVendor(string? vendor) => vendor switch
    {
        null => string.Empty,
        _ when vendor.StartsWith("Micro-Star", StringComparison.OrdinalIgnoreCase) => "MSI",
        _ when vendor.StartsWith("ASUSTeK", StringComparison.OrdinalIgnoreCase) => "ASUS",
        _ when vendor.StartsWith("Gigabyte", StringComparison.OrdinalIgnoreCase) => "Gigabyte",
        _ when vendor.StartsWith("ASRock", StringComparison.OrdinalIgnoreCase) => "ASRock",
        _ when vendor.StartsWith("Hewlett", StringComparison.OrdinalIgnoreCase) => "HP",
        _ when vendor.StartsWith("Dell", StringComparison.OrdinalIgnoreCase) => "Dell",
        _ when vendor.StartsWith("LENOVO", StringComparison.OrdinalIgnoreCase) => "Lenovo",
        _ => vendor,
    };

    public override void OnDrives(IReadOnlyList<DriveState> drives)
    {
        Drives.Clear();
        foreach (var d in drives)
        {
            var total = d.Drive.TotalVolumeBytes;
            var free = d.Drive.FreeVolumeBytes;
            var used = total is > 0 && free.HasValue ? 100.0 * (total.Value - free.Value) / total.Value : double.NaN;
            var letters = string.Join(" ", d.Drive.Partitions.Where(p => p.DriveLetter is not null).Select(p => p.DriveLetter));
            Drives.Add(new DriveSummary(
                d.Drive.Model,
                $"{d.Drive.BusType} {d.Drive.MediaType} · {Fmt.Bytes(d.Drive.SizeBytes)}{(letters.Length > 0 ? " · " + letters : "")}",
                d.Health.Level,
                DriveHealthEvaluator.Describe(d.Health.Level),
                d.Health.Temperature.HasValue ? $"{d.Health.Temperature.Value:0} °C" : "",
                used,
                total is > 0 && free.HasValue ? $"{Fmt.Bytes(total.Value - free.Value)} used of {Fmt.Bytes(total.Value)}" : "No mounted volumes"));
        }
    }

    public override void OnLive(LiveSnapshot live)
    {
        UptimeText = "Up " + Fmt.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64));
        var load = live.CpuUsage?.TotalLoad;
        CpuLoad = Fmt.Value(load);
        CpuLoadText = Fmt.Pct(load);
        CpuTempText = live.Cpu.PackageTemperature.HasValue ? $"{live.Cpu.PackageTemperature.Value:0} °C" : string.Empty;
        var clock = live.Cpu.AverageCoreClock.HasValue ? live.Cpu.AverageCoreClock : live.CpuUsage?.EffectiveClockMHz;
        CpuClockText = clock is { HasValue: true } ? $"{clock.Value / 1000:0.00} GHz" : "—";

        var gpuIndex = Math.Clamp(_settings.Current.SelectedGpuIndex, 0, Math.Max(0, live.Gpus.Count - 1));
        var gpu = live.Gpus.Count > 0 ? live.Gpus[gpuIndex] : null;
        GpuName = gpu?.Name ?? "Graphics";
        GpuLoad = Fmt.Value(gpu?.Load);
        GpuLoadText = Fmt.Pct(gpu?.Load);
        GpuTempText = gpu?.Temperature.HasValue == true ? $"{gpu.Temperature.Value:0} °C" : string.Empty;
        VramText = gpu is { DedicatedUsedMB.HasValue: true, DedicatedTotalMB.HasValue: true }
            ? $"{Fmt.Mb(gpu.DedicatedUsedMB.Value)} / {Fmt.Mb(gpu.DedicatedTotalMB.Value)}" : "—";

        if (live.Memory is { TotalPhysical: > 0 } m)
        {
            RamPercent = m.UsedPercent;
            RamPercentText = $"{m.UsedPercent:0}%";
            RamText = $"{m.UsedPhysical / 1073741824.0:0.0} / {m.TotalPhysical / 1073741824.0:0.0} GB";
            RamAvailableText = $"{Fmt.Bytes(m.AvailablePhysical)} available · pressure {m.Pressure.ToLowerInvariant()}";
        }

        var net = live.Network.FirstOrDefault(n => n.Id == _settings.Current.SelectedNetworkAdapterId && n.Status == "Up")
                  ?? live.Network.FirstOrDefault(n => n.Status == "Up" && !n.IsVirtualOrLoopback);
        if (net is not null)
        {
            NetworkName = net.Name;
            DownText = Fmt.Rate(net.DownloadBytesPerSecond);
            UpText = Fmt.Rate(net.UploadBytesPerSecond);
            NetworkSeries = [Charts.Series(_monitoring.History, HistoryKeys.NetDown(net.Id), "Download", "Brush.Accent"),
                             Charts.Series(_monitoring.History, HistoryKeys.NetUp(net.Id), "Upload", "Brush.Series2")];
        }

        if (live.Battery is { HasBattery: true } b)
        {
            PowerTitle = "Battery";
            PowerText = b.ChargePercent.HasValue ? $"{b.ChargePercent.Value:0}%" : "—";
            PowerSub = b.Status.HasValue ? b.Status.Value! : "";
        }
        else
        {
            PowerTitle = "Power";
            PowerText = "AC power";
            PowerSub = live.Battery?.PowerPlan is { } plan ? $"Plan: {plan}" : "No battery detected";
        }

        var h = _monitoring.History;
        var series = new List<ChartSeries> { Charts.Series(h, HistoryKeys.CpuLoad, "CPU", "Brush.Accent") };
        if (gpu is not null) series.Add(Charts.Series(h, HistoryKeys.GpuLoad(gpuIndex), "GPU", "Brush.Series2"));
        LoadSeries = series;
        var temps = new List<ChartSeries>();
        if (h.Get(HistoryKeys.CpuTemp).Length > 0) temps.Add(Charts.Series(h, HistoryKeys.CpuTemp, "CPU", "Brush.Accent"));
        if (gpu is not null) temps.Add(Charts.Series(h, HistoryKeys.GpuTemp(gpuIndex), "GPU", "Brush.Series2"));
        TempSeries = temps;
    }
}

// =====================================================================================================================
// CPU
// =====================================================================================================================
public sealed partial class CoreItem : ObservableObject
{
    public CoreItem(int index, string label, string type) { Index = index; Label = label; CoreType = type; }
    public int Index { get; }
    public string Label { get; }
    public string CoreType { get; }
    [ObservableProperty] private double _load = double.NaN;
    [ObservableProperty] private string _loadText = "—";
    [ObservableProperty] private string _detail = string.Empty;
}

public sealed partial class CpuViewModel : PageViewModel
{
    private readonly MonitoringService _monitoring;

    public CpuViewModel(MonitoringService monitoring) => _monitoring = monitoring;

    public override string Title => "CPU";
    public override string Icon => "";

    public RowList Tiles { get; } = new();
    public ObservableCollection<CoreItem> Cores { get; } = new();

    [ObservableProperty] private string _name = "Processor";
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _loadSeries;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _tempSeries;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _powerSeries;
    [ObservableProperty] private string _loadStats = string.Empty;
    [ObservableProperty] private string _tempStats = string.Empty;
    [ObservableProperty] private string _powerStats = string.Empty;
    [ObservableProperty] private bool _hasTemperature;
    [ObservableProperty] private bool _hasPower;
    [ObservableProperty] private double _windowSeconds = 300;

    public override void OnActivated() => OnSystemInfo(_monitoring.SystemInfo);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SetWindow(string seconds)
    {
        if (double.TryParse(seconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)) WindowSeconds = s;
        if (_monitoring.Latest is { } l) OnLive(l);
    }

    public override void OnSystemInfo(SystemInfoReport report)
    {
        var topo = CpuTopology.Current;
        if (report.Cpu is { } cpu)
        {
            Name = cpu.Name;
            Subtitle = $"{cpu.PhysicalCores} cores · {cpu.LogicalProcessors} threads" +
                       (topo.IsHybrid ? $" · {topo.PerformanceCores}P + {topo.EfficiencyCores}E" : "") +
                       (cpu.BaseClockMHz is > 0 ? $" · base {cpu.BaseClockMHz / 1000:0.0#} GHz" : "");
        }
        if (Cores.Count == 0)
        {
            foreach (var (lp, i) in topo.LogicalProcessors.Select((lp, i) => (lp, i)))
            {
                var type = lp.CoreType switch { CoreType.Performance => "P-core", CoreType.Efficiency => "E-core", _ => "" };
                Cores.Add(new CoreItem(i, $"CPU {i}", type));
            }
        }
    }

    public override void OnLive(LiveSnapshot live)
    {
        var u = live.CpuUsage;
        var c = live.Cpu;
        if (u is not null)
        {
            Tiles.Set("Utilization", u.TotalLoad, "0", "%");
            Tiles.Set("Effective clock", u.EffectiveClockMHz.HasValue ? $"{u.EffectiveClockMHz.Value / 1000:0.00} GHz" : MetricText.ForState(u.EffectiveClockMHz.State),
                u.EffectiveClockMHz.Source, u.EffectiveClockMHz.State, u.EffectiveClockMHz.Note);
        }
        Tiles.Set("Package temperature", c.PackageTemperature, "0", "°C");
        Tiles.Set("Average core clock", c.AverageCoreClock.HasValue ? $"{c.AverageCoreClock.Value / 1000:0.00} GHz" : MetricText.ForState(c.AverageCoreClock.State),
            c.AverageCoreClock.Source, c.AverageCoreClock.State, c.AverageCoreClock.Note);
        Tiles.Set("Package power", c.PackagePower, "0.0", "W");
        Tiles.Set("Core voltage", c.CoreVoltage, "0.000", "V");
        Tiles.Set("Throttling", c.ThermalThrottling, s => s);
        Tiles.Set("CPU fan", c.FanRpm, "0", "RPM");
        if (u is not null)
            Tiles.Set("Processes / threads", $"{u.ProcessCount} / {u.ThreadCount}", DataSources.Win32, note: $"{u.HandleCount:N0} handles");

        HasTemperature = c.PackageTemperature.HasValue || _monitoring.History.Get(HistoryKeys.CpuTemp).Length > 0;
        HasPower = c.PackagePower.HasValue || _monitoring.History.Get(HistoryKeys.CpuPower).Length > 0;

        // Per-logical-processor load, plus per-core clocks/temps from the sensor library when available.
        var topo = CpuTopology.Current.LogicalProcessors;
        for (var i = 0; i < Cores.Count; i++)
        {
            var load = u is not null && i < u.PerLogicalLoad.Count ? u.PerLogicalLoad[i] : null;
            var item = Cores[i];
            item.Load = load ?? double.NaN;
            item.LoadText = load is double l ? $"{l:0}%" : "—";
            var coreNumber = i < topo.Count ? topo[i].CoreIndex + 1 : i + 1;
            var clock = c.CoreClocks.FirstOrDefault(s => SensorMapping.CoreNumber(s.Name) == coreNumber)?.Value;
            var temp = c.CoreTemperatures.FirstOrDefault(s => SensorMapping.CoreNumber(s.Name) == coreNumber)?.Value;
            item.Detail = string.Join(" · ", new[]
            {
                clock is double ck ? $"{ck / 1000:0.00} GHz" : null,
                temp is double t ? $"{t:0} °C" : null,
            }.Where(x => x is not null));
        }

        var h = _monitoring.History;
        LoadSeries = Charts.One(h, HistoryKeys.CpuLoad, "Load");
        TempSeries = Charts.One(h, HistoryKeys.CpuTemp, "Temperature", "Brush.Series2");
        PowerSeries = Charts.One(h, HistoryKeys.CpuPower, "Power", "Brush.Series4");
        LoadStats = Fmt.Stats(h.Stats(HistoryKeys.CpuLoad), "0", "%");
        TempStats = Fmt.Stats(h.Stats(HistoryKeys.CpuTemp), "0", "°C");
        PowerStats = Fmt.Stats(h.Stats(HistoryKeys.CpuPower), "0.0", "W");
    }
}

// =====================================================================================================================
// GPU
// =====================================================================================================================
public sealed partial class GpuViewModel : PageViewModel
{
    private readonly MonitoringService _monitoring;
    private readonly ISettingsService _settings;
    private int _knownCount = -1;

    public GpuViewModel(MonitoringService monitoring, ISettingsService settings)
    {
        _monitoring = monitoring;
        _settings = settings;
        _selectedIndex = settings.Current.SelectedGpuIndex;
    }

    public override string Title => "GPU";
    public override string Icon => "";

    public ObservableCollection<string> GpuNames { get; } = new();
    public RowList Tiles { get; } = new();
    public RowList Engines { get; } = new();
    public RowList Details { get; } = new();

    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private bool _hasGpu;
    [ObservableProperty] private string _name = "Graphics";
    [ObservableProperty] private double _vramPercent = double.NaN;
    [ObservableProperty] private string _vramText = "—";
    [ObservableProperty] private string _sharedText = "—";
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _loadSeries;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _tempSeries;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _powerSeries;
    [ObservableProperty] private string _loadStats = string.Empty;
    [ObservableProperty] private string _tempStats = string.Empty;
    [ObservableProperty] private string _powerStats = string.Empty;

    partial void OnSelectedIndexChanged(int value)
    {
        if (value < 0) return;
        _settings.Current.SelectedGpuIndex = value;
        _settings.Save();
        Tiles.Reset(); Engines.Reset(); Details.Reset();
        if (_monitoring.Latest is { } l) OnLive(l);
    }

    public override void OnLive(LiveSnapshot live)
    {
        if (live.Gpus.Count != _knownCount)
        {
            _knownCount = live.Gpus.Count;
            GpuNames.Clear();
            foreach (var g in live.Gpus) GpuNames.Add(g.IsLikelyIntegrated ? $"{g.Name} (integrated)" : g.Name);
            if (SelectedIndex >= GpuNames.Count || SelectedIndex < 0) SelectedIndex = 0;
            else OnPropertyChanged(nameof(SelectedIndex));
        }
        HasGpu = live.Gpus.Count > 0;
        if (!HasGpu) return;
        var i = Math.Clamp(SelectedIndex, 0, live.Gpus.Count - 1);
        var g2 = live.Gpus[i];
        Name = g2.Name;

        Tiles.Set("Utilization", g2.Load, "0", "%");
        Tiles.Set("Temperature", g2.Temperature, "0", "°C");
        Tiles.Set("Hot spot", g2.HotSpot, "0", "°C");
        Tiles.Set(g2.Vendor == "AMD" ? "Memory temperature" : "Memory junction", g2.MemoryJunction, "0", "°C");
        Tiles.Set("Core clock", g2.CoreClock, "0", "MHz");
        Tiles.Set("Memory clock", g2.MemoryClock, "0", "MHz");
        Tiles.Set("Power", g2.Power, "0.0", "W");
        Tiles.Set("Core voltage", g2.Voltage, "0.000", "V");
        Tiles.Set("Fan speed", g2.FanRpm, "0", "RPM");
        Tiles.Set("Fan duty", g2.FanPercent, "0", "%");

        if (g2.DedicatedUsedMB.HasValue && g2.DedicatedTotalMB.HasValue && g2.DedicatedTotalMB.Value > 0)
        {
            VramPercent = 100 * g2.DedicatedUsedMB.Value / g2.DedicatedTotalMB.Value;
            VramText = $"{Fmt.Mb(g2.DedicatedUsedMB.Value)} of {Fmt.Mb(g2.DedicatedTotalMB.Value)} dedicated";
        }
        else
        {
            VramPercent = double.NaN;
            VramText = g2.DedicatedUsedMB.HasValue ? $"{Fmt.Mb(g2.DedicatedUsedMB.Value)} dedicated in use" : "Dedicated memory: not reported";
        }
        SharedText = g2.SharedUsedMB.HasValue
            ? $"Shared: {Fmt.Mb(g2.SharedUsedMB.Value)}{(g2.SharedTotalMB.HasValue ? $" of {Fmt.Mb(g2.SharedTotalMB.Value)}" : "")}"
            : "Shared memory: not reported";

        Engines.Set("Memory controller", g2.MemoryControllerLoad, "0", "%");
        Engines.Set("Video engine", g2.VideoEngineLoad, "0", "%");
        Engines.Set("Video decode", g2.VideoDecodeLoad, "0", "%");
        Engines.Set("Video encode", g2.VideoEncodeLoad, "0", "%");
        Engines.Set("Bus interface", g2.BusLoad, "0", "%");

        Details.Set("Vendor", g2.Vendor, DataSources.Calculated, note: "Derived from the adapter name");
        Details.Set("Type", g2.IsLikelyIntegrated ? "Integrated" : "Dedicated", DataSources.Calculated, note: "Derived from the adapter name");
        var info = _monitoring.SystemInfo.Gpus.FirstOrDefault(s => s.Name.Equals(g2.Name, StringComparison.OrdinalIgnoreCase))
                   ?? _monitoring.SystemInfo.Gpus.FirstOrDefault(s => s.Name.Contains(g2.Name, StringComparison.OrdinalIgnoreCase) || g2.Name.Contains(s.Name, StringComparison.OrdinalIgnoreCase));
        Details.Set("Driver version", info?.DriverVersion ?? "Unavailable", DataSources.Wmi, info?.DriverVersion is null ? MetricState.Unavailable : MetricState.Available);
        Details.Set("Driver date", info?.DriverDate ?? "Unavailable", DataSources.Wmi, info?.DriverDate is null ? MetricState.Unavailable : MetricState.Available);
        Details.Set("Video processor", info?.VideoProcessor ?? "Unavailable", DataSources.Wmi, info?.VideoProcessor is null ? MetricState.Unavailable : MetricState.Available);
        Details.Set("Architecture", "Not Supported", DataSources.Wmi, MetricState.Unsupported, "GPU architecture is not exposed through standard Windows APIs");
        Details.Set("PCIe link", "Not Supported", DataSources.Wmi, MetricState.Unsupported, "PCIe generation/width is not exposed through standard Windows APIs");

        var h = _monitoring.History;
        LoadSeries = Charts.One(h, HistoryKeys.GpuLoad(i), "Load");
        TempSeries = Charts.One(h, HistoryKeys.GpuTemp(i), "Temperature", "Brush.Series2");
        PowerSeries = Charts.One(h, HistoryKeys.GpuPower(i), "Power", "Brush.Series4");
        LoadStats = Fmt.Stats(h.Stats(HistoryKeys.GpuLoad(i)), "0", "%");
        TempStats = Fmt.Stats(h.Stats(HistoryKeys.GpuTemp(i)), "0", "°C");
        PowerStats = Fmt.Stats(h.Stats(HistoryKeys.GpuPower(i)), "0.0", "W");
    }
}

// =====================================================================================================================
// Memory
// =====================================================================================================================
public sealed partial class MemoryViewModel : PageViewModel
{
    private readonly MonitoringService _monitoring;
    private IDisposable? _processSubscription;

    public MemoryViewModel(MonitoringService monitoring)
    {
        _monitoring = monitoring;
        _monitoring.ProcessesUpdated += (_, list) =>
        {
            if (_processSubscription is null) return;
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => UpdateTopProcesses(list));
        };
    }

    public override string Title => "Memory";
    public override string Icon => "";

    public RowList Tiles { get; } = new();
    public ObservableCollection<InfoSection> Modules { get; } = new();
    public RowList Summary { get; } = new();
    public ObservableCollection<ValueRow> TopProcesses { get; } = new();

    [ObservableProperty] private double _usedPercent = double.NaN;
    [ObservableProperty] private string _usedText = "—";
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _usageSeries;
    [ObservableProperty] private string _usageStats = string.Empty;

    public override void OnActivated()
    {
        _processSubscription ??= _monitoring.SubscribeProcesses();
        OnSystemInfo(_monitoring.SystemInfo);
    }

    public override void OnDeactivated()
    {
        _processSubscription?.Dispose();
        _processSubscription = null;
    }

    public override void OnSystemInfo(SystemInfoReport report)
    {
        var mem = report.Sections.FirstOrDefault(s => s.Title == "Memory");
        if (mem is null) return;
        Modules.Clear();
        foreach (var c in mem.Children ?? []) Modules.Add(c with { Items = c.Items.Where(i => i.State == MetricState.Available).ToList() });
        Summary.Reset();
        foreach (var item in mem.Items)
            Summary.Set(item.Label, item.DisplayValue(false), item.Source, item.State);
    }

    public override void OnLive(LiveSnapshot live)
    {
        if (live.Memory is not { TotalPhysical: > 0 } m) return;
        const string src = "Win32 GlobalMemoryStatusEx / GetPerformanceInfo";
        UsedPercent = m.UsedPercent;
        UsedText = $"{Fmt.Bytes(m.UsedPhysical)} of {Fmt.Bytes(m.TotalPhysical)} in use ({m.UsedPercent:0}%)";
        Tiles.Set("In use", Fmt.Bytes(m.UsedPhysical), src);
        Tiles.Set("Available", Fmt.Bytes(m.AvailablePhysical), src);
        Tiles.Set("Cached", Fmt.Bytes(m.CachedBytes), DataSources.PerformanceCounters, note: "Standby + modified page lists");
        Tiles.Set("Free", m.FreeBytes is ulong f ? Fmt.Bytes(f) : "Unavailable", DataSources.PerformanceCounters, m.FreeBytes is null ? MetricState.Unavailable : MetricState.Available, "Free and zeroed pages");
        Tiles.Set("Committed", $"{Fmt.Bytes(m.CommittedBytes)} / {Fmt.Bytes(m.CommitLimitBytes)}", src, note: $"{m.CommitPercent:0}% of commit limit");
        Tiles.Set("Virtual memory available", $"{Fmt.Bytes(m.AvailableVirtual)} of {Fmt.Bytes(m.TotalVirtual)}", src, note: "Physical memory plus page files");
        Tiles.Set("Paged pool", Fmt.Bytes(m.PagedPoolBytes), src);
        Tiles.Set("Non-paged pool", Fmt.Bytes(m.NonPagedPoolBytes), src);
        Tiles.Set("Memory pressure", m.Pressure, DataSources.Calculated, note: "Max of physical usage and commit charge: <70% Low, 70–85% Moderate, 85–95% High, ≥95% Critical");
        if (m.ModifiedBytes is ulong mod) Tiles.Set("Modified", Fmt.Bytes(mod), DataSources.PerformanceCounters, note: "Pages waiting to be written to disk");
        if (m.StandbyBytes is ulong sb) Tiles.Set("Standby", Fmt.Bytes(sb), DataSources.PerformanceCounters, note: "Cached data that can be reused or freed");

        UsageSeries = Charts.One(_monitoring.History, HistoryKeys.MemUsedPercent, "Used %");
        UsageStats = Fmt.Stats(_monitoring.History.Stats(HistoryKeys.MemUsedPercent), "0", "%");
    }

    private void UpdateTopProcesses(IReadOnlyList<ProcessSample> list)
    {
        TopProcesses.Clear();
        foreach (var group in list.GroupBy(p => p.Name).Select(g => (Name: g.Key, Ws: g.Sum(p => p.WorkingSetBytes), Count: g.Count()))
                     .OrderByDescending(x => x.Ws).Take(8))
        {
            TopProcesses.Add(new ValueRow(group.Count > 1 ? $"{group.Name} ({group.Count})" : group.Name)
            {
                Value = Fmt.Bytes(group.Ws),
                Source = "Working set",
                State = MetricState.Available,
            });
        }
    }
}
