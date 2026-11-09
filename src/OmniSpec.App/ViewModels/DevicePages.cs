using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniSpec.App.Controls;
using OmniSpec.Core.Hardware;
using OmniSpec.Core.History;
using OmniSpec.Core.Live;
using OmniSpec.Core.Metrics;
using OmniSpec.Core.Settings;
using OmniSpec.Core.Storage;
using OmniSpec.Monitoring;

namespace OmniSpec.App.ViewModels;

// =====================================================================================================================
// Storage
// =====================================================================================================================
public sealed partial class DriveItem : ObservableObject
{
    public DriveItem(DriveState state, bool revealSensitive, MetricHistory history)
    {
        var d = state.Drive;
        var h = state.Health;
        DeviceId = d.DeviceId;
        Title = d.Model;
        Subtitle = $"Disk {d.DeviceId} · {d.BusType} {d.MediaType} · {Fmt.Bytes(d.SizeBytes)}";
        Level = h.Level;
        LevelText = DriveHealthEvaluator.Describe(h.Level);
        TempText = h.Temperature.HasValue ? $"{h.Temperature.Value:0} °C" : string.Empty;
        var total = d.TotalVolumeBytes;
        var free = d.FreeVolumeBytes;
        UsedPercent = total is > 0 && free.HasValue ? 100.0 * (total.Value - free.Value) / total.Value : double.NaN;
        UsageText = total is > 0 && free.HasValue ? $"{Fmt.Bytes(free.Value)} free of {Fmt.Bytes(total.Value)}" : "No mounted volumes";

        Identity.Set("Model", d.Model, DataSources.WmiStorage);
        Identity.Set("Manufacturer", d.Manufacturer ?? "Not reported", DataSources.WmiStorage, d.Manufacturer is null ? MetricState.Unavailable : MetricState.Available);
        Identity.Set("Serial number", d.SerialNumber is null ? "Not reported" : revealSensitive ? d.SerialNumber : "Hidden for privacy", DataSources.WmiStorage,
            d.SerialNumber is null ? MetricState.Unavailable : MetricState.Available);
        Identity.Set("Firmware", d.FirmwareVersion ?? "Not reported", DataSources.WmiStorage, d.FirmwareVersion is null ? MetricState.Unavailable : MetricState.Available);
        Identity.Set("Interface / bus", d.BusType ?? "Unknown", DataSources.WmiStorage);
        Identity.Set("Media type", d.MediaType ?? "Unknown", DataSources.WmiStorage);
        Identity.Set("Capacity", $"{Fmt.Bytes(d.SizeBytes)} ({d.SizeBytes / 1e9:0.#} GB)", DataSources.WmiStorage);
        if (d.SpindleSpeedRpm is uint rpm) Identity.Set("Spindle speed", $"{rpm} RPM", DataSources.WmiStorage);
        Identity.Set("Partition style", d.PartitionStyle ?? "Unknown", DataSources.WmiStorage);
        Identity.Set("System / boot disk", $"{(d.IsSystemDisk ? "System" : "")}{(d.IsSystemDisk && d.IsBootDisk ? " + " : "")}{(d.IsBootDisk ? "Boot" : "")}".Trim() is { Length: > 0 } sb ? sb : "No", DataSources.WmiStorage);
        if (total is > 0 && free.HasValue)
            Identity.Set("Used space (mounted volumes)", $"{Fmt.Bytes(total.Value - free.Value)} of {Fmt.Bytes(total.Value)} ({UsedPercent:0}%)", "Windows volume information");

        Health.Set("Windows health status (official)", h.OfficialStatus, s => s);
        Health.Set("Operational status", d.OperationalStatus.Count > 0 ? string.Join(", ", d.OperationalStatus) : "Not reported", DataSources.WmiStorage,
            d.OperationalStatus.Count > 0 ? MetricState.Available : MetricState.Unavailable);
        Health.Set("Endurance used (device-reported wear)", h.DeviceReportedWear, "0", "%").Sub = "100% = rated endurance reached";
        Health.Set("Estimated remaining life", h.EstimatedRemainingLife, "0", "%").Sub = "Estimate";
        Health.Set("Temperature", h.Temperature, "0", "°C");
        Health.Set("Power-on hours", h.PowerOnHours, "N0", "h");
        Health.Set("Power cycles", h.PowerCycles, "N0");
        Health.Set("Host reads", h.HostReadsGb, "N0", "GB");
        Health.Set("Host writes", h.HostWritesGb, "N0", "GB");

        foreach (var f in h.Findings) Findings.Add(f);

        if (d.Reliability.HasValue)
        {
            var r = d.Reliability.Value!;
            void R(string label, double? v, string unit = "")
            {
                if (v is double x) Reliability.Set(label, $"{x.ToString("N0", CultureInfo.CurrentCulture)}{(unit.Length > 0 ? " " + unit : "")}", DataSources.ReliabilityCounters);
                else Reliability.Set(label, "Not reported", DataSources.ReliabilityCounters, MetricState.Unavailable);
            }
            R("Temperature", r.Temperature, "°C");
            R("Max temperature", r.TemperatureMax, "°C");
            R("Wear (endurance used)", r.Wear, "%");
            R("Power-on hours", r.PowerOnHours, "h");
            R("Start/stop cycles", r.StartStopCycleCount);
            R("Load/unload cycles", r.LoadUnloadCycleCount);
            R("Read errors (total)", r.ReadErrorsTotal);
            R("Read errors (corrected)", r.ReadErrorsCorrected);
            R("Read errors (uncorrected)", r.ReadErrorsUncorrected);
            R("Write errors (total)", r.WriteErrorsTotal);
            R("Write errors (corrected)", r.WriteErrorsCorrected);
            R("Write errors (uncorrected)", r.WriteErrorsUncorrected);
            R("Max read latency", r.ReadLatencyMaxMs, "ms");
            R("Max write latency", r.WriteLatencyMaxMs, "ms");
            R("Max flush latency", r.FlushLatencyMaxMs, "ms");
        }
        ReliabilityStatus = d.Reliability.HasValue ? string.Empty : $"{MetricText.ForState(d.Reliability.State)}: {d.Reliability.Note}";

        foreach (var s in d.SmartSensors)
            Smart.Set($"{s.Name} ({s.Kind})", s.Value is double v ? $"{v.ToString("0.##", CultureInfo.CurrentCulture)} {s.Unit}".Trim() : "Unavailable",
                DataSources.Smart, s.Value.HasValue ? MetricState.Available : MetricState.Unavailable, "Value as reported; vendor-specific attributes are not interpreted");
        SmartStatus = d.SmartSensors.Count > 0
            ? $"Matched to sensor device \"{d.SmartSourceDevice}\""
            : "No SMART/NVMe data available for this drive. Administrator mode may be required; USB bridges often block SMART.";

        foreach (var p in d.Partitions) Partitions.Add(p);

        var tempSensor = d.SmartSensors.FirstOrDefault(s => s.Kind == SensorKind.Temperature);
        TempSeries = tempSensor is not null
            ? Charts.One(history, HistoryKeys.Sensor(tempSensor.Id), "Temperature", "Brush.Series2")
            : Charts.One(history, HistoryKeys.DriveTemp(d.DeviceId), "Temperature", "Brush.Series2");
    }

    public string DeviceId { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public DriveHealthLevel Level { get; }
    public string LevelText { get; }
    public string TempText { get; }
    public double UsedPercent { get; }
    public string UsageText { get; }
    public RowList Identity { get; } = new();
    public RowList Health { get; } = new();
    public RowList Reliability { get; } = new();
    public RowList Smart { get; } = new();
    public ObservableCollection<HealthFinding> Findings { get; } = new();
    public ObservableCollection<PartitionInfo> Partitions { get; } = new();
    public string ReliabilityStatus { get; }
    public bool HasReliability => Reliability.Count > 0;
    public bool HasSmart => Smart.Count > 0;
    public bool HasTemperatureHistory => TempSeries.Any(s => s.Points.Length > 0);
    public string SmartStatus { get; }
    public IReadOnlyList<ChartSeries> TempSeries { get; }
}

public sealed partial class StorageViewModel : PageViewModel
{
    private readonly MonitoringService _monitoring;
    private readonly ISettingsService _settings;

    public StorageViewModel(MonitoringService monitoring, ISettingsService settings)
    {
        _monitoring = monitoring;
        _settings = settings;
    }

    public override string Title => "Storage";
    public override string Icon => "";

    public ObservableCollection<DriveItem> Drives { get; } = new();
    [ObservableProperty] private DriveItem? _selectedDrive;
    [ObservableProperty] private string _updatedText = "Collecting drive information…";
    [ObservableProperty] private bool _isRefreshing;

    public override void OnActivated() => OnDrives(_monitoring.Drives);

    public override void OnDrives(IReadOnlyList<DriveState> drives)
    {
        IsRefreshing = false;
        if (_monitoring.DrivesUpdatedAt is null) return;
        var selectedId = SelectedDrive?.DeviceId;
        Drives.Clear();
        foreach (var d in drives) Drives.Add(new DriveItem(d, _settings.Current.ShowSensitiveIdentifiers, _monitoring.History));
        SelectedDrive = Drives.FirstOrDefault(d => d.DeviceId == selectedId) ?? Drives.FirstOrDefault();
        UpdatedText = drives.Count == 0
            ? "No physical drives were reported by the Windows Storage Management API."
            : $"{drives.Count} drive(s) · updated {_monitoring.DrivesUpdatedAt:HH:mm:ss} · refreshes every {_settings.Current.StorageIntervalSeconds} s";
    }

    [RelayCommand]
    private void Refresh()
    {
        IsRefreshing = true;
        UpdatedText = "Refreshing…";
        _monitoring.RequestDriveRefresh();
    }
}

// =====================================================================================================================
// Sensors
// =====================================================================================================================
public sealed partial class SensorRow : ObservableObject
{
    public SensorRow(string id, string name, SensorKind kind, string unit) { Id = id; Name = name; Kind = kind; Unit = unit; }
    public string Id { get; }
    public string Name { get; }
    public SensorKind Kind { get; }
    public string KindText => Kind.ToString();
    public string Unit { get; }
    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private string _minText = "—";
    [ObservableProperty] private string _maxText = "—";
    [ObservableProperty] private string _avgText = "—";
    [ObservableProperty] private string _limitText = string.Empty;
    [ObservableProperty] private bool _isOverLimit;
    [ObservableProperty] private bool _hasValue;
}

public sealed class SensorGroup
{
    public SensorGroup(string name, string kind) { Name = name; Kind = kind; }
    public string Name { get; }
    public string Kind { get; }
    public ObservableCollection<SensorRow> Sensors { get; } = new();
}

public sealed partial class SensorsViewModel : PageViewModel
{
    private readonly MonitoringService _monitoring;
    private readonly ISettingsService _settings;
    private string _signature = string.Empty;
    private readonly Dictionary<string, SensorRow> _rows = new();

    public SensorsViewModel(MonitoringService monitoring, ISettingsService settings)
    {
        _monitoring = monitoring;
        _settings = settings;
    }

    public override string Title => "Sensors";
    public override string Icon => "";

    public ObservableCollection<SensorGroup> Groups { get; } = new();
    public ObservableCollection<ValueRow> Coverage { get; } = new();
    public string[] KindFilters { get; } = ["All", "Temperature", "Fan", "Voltage", "Power", "Load", "Clock", "Control", "Data"];

    [ObservableProperty] private string _kindFilter = "All";
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private SensorRow? _selected;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _selectedSeries;
    [ObservableProperty] private string _selectedTitle = "Select a sensor to see its history";
    [ObservableProperty] private string _limitInput = string.Empty;
    [ObservableProperty] private double _selectedLimit = double.NaN;
    [ObservableProperty] private string _providerStatus = string.Empty;
    [ObservableProperty] private int _sensorCount;

    partial void OnKindFilterChanged(string value) => _signature = string.Empty;
    partial void OnSearchChanged(string value) => _signature = string.Empty;

    partial void OnSelectedChanged(SensorRow? value)
    {
        LimitInput = value is not null && _settings.Current.SensorLimits.TryGetValue(value.Id, out var l) ? l.ToString(CultureInfo.CurrentCulture) : string.Empty;
        SelectedTitle = value is null ? "Select a sensor to see its history" : $"{value.Name} · {value.KindText}";
        UpdateSelectedChart();
    }

    private void UpdateSelectedChart()
    {
        if (Selected is null) { SelectedSeries = null; return; }
        SelectedSeries = Charts.One(_monitoring.History, HistoryKeys.Sensor(Selected.Id), Selected.Name);
        SelectedLimit = _settings.Current.SensorLimits.TryGetValue(Selected.Id, out var l) ? l : double.NaN;
    }

    [RelayCommand]
    private void SelectSensor(SensorRow row) => Selected = row;

    [RelayCommand]
    private void ApplyLimit()
    {
        if (Selected is null) return;
        // Copy-on-write so the monitoring thread never sees a dictionary being mutated.
        var limits = new Dictionary<string, double>(_settings.Current.SensorLimits);
        if (string.IsNullOrWhiteSpace(LimitInput)) limits.Remove(Selected.Id);
        else if (double.TryParse(LimitInput, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)) limits[Selected.Id] = v;
        else return;
        _settings.Current.SensorLimits = limits;
        _settings.Save();
        Selected.LimitText = limits.TryGetValue(Selected.Id, out var x) ? $"≥ {x:0.#} {Selected.Unit}" : string.Empty;
        UpdateSelectedChart();
    }

    [RelayCommand]
    private void ResetStatistics()
    {
        _monitoring.History.ResetStats();
    }

    public override void OnLive(LiveSnapshot live)
    {
        ProviderStatus = _monitoring.HardwareProvider.Status.Message;
        var devices = live.Hardware.Devices.Where(d => d.Sensors.Count > 0).ToList();
        bool Include(SensorReading s) => s.Value.HasValue &&
            (KindFilter == "All" || s.Kind.ToString() == KindFilter || (KindFilter == "Data" && s.Kind is SensorKind.Data or SensorKind.SmallData or SensorKind.Throughput)) &&
            (Search.Length == 0 || s.Name.Contains(Search, StringComparison.OrdinalIgnoreCase));

        var signature = string.Join("|", devices.Select(d => d.Id + ":" + string.Join(",", d.Sensors.Where(Include).Select(s => s.Id))));
        if (signature != _signature)
        {
            _signature = signature;
            Groups.Clear();
            _rows.Clear();
            foreach (var d in devices)
            {
                var included = d.Sensors.Where(Include).ToList();
                if (included.Count == 0) continue;
                var g = new SensorGroup(d.Name, d.Kind.ToString());
                foreach (var s in included.OrderBy(s => s.Kind).ThenBy(s => NaturalKey(s.Name), StringComparer.OrdinalIgnoreCase))
                {
                    var row = new SensorRow(s.Id, s.Name, s.Kind, s.Unit);
                    if (_settings.Current.SensorLimits.TryGetValue(s.Id, out var l)) row.LimitText = $"≥ {l:0.#} {s.Unit}";
                    g.Sensors.Add(row);
                    _rows[s.Id] = row;
                }
                Groups.Add(g);
            }
        }

        var count = 0;
        foreach (var d in devices)
        {
            foreach (var s in d.Sensors)
            {
                if (!_rows.TryGetValue(s.Id, out var row)) continue;
                count++;
                var fmt = s.Kind is SensorKind.Voltage ? "0.000" : s.Kind is SensorKind.Fan or SensorKind.Clock ? "0" : "0.#";
                string F(double? v) => v is double x ? $"{x.ToString(fmt, CultureInfo.CurrentCulture)} {s.Unit}".Trim() : "—";
                row.HasValue = s.Value.HasValue;
                row.ValueText = s.Value.HasValue ? F(s.Value) : "Unavailable";
                var stats = _monitoring.History.Stats(HistoryKeys.Sensor(s.Id));
                row.MinText = stats.Count > 0 ? F(stats.Min) : F(s.Min);
                row.MaxText = stats.Count > 0 ? F(stats.Max) : F(s.Max);
                row.AvgText = stats.Count > 0 ? F(stats.Avg) : "—";
                row.IsOverLimit = _settings.Current.SensorLimits.TryGetValue(s.Id, out var lim) && s.Value >= lim;
            }
        }
        SensorCount = count;
        UpdateSelectedChart();
    }

    /// <summary>Pads numbers so "Core #2" sorts before "Core #10".</summary>
    private static string NaturalKey(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"\d+", m => m.Value.PadLeft(6, '0'));

    /// <summary>Explains, per category, whether sensors were found and why not.</summary>
    private void UpdateCoverage(LiveSnapshot live)
    {
        var hw = _monitoring.HardwareProvider;
        var lowLevel = hw.LowLevelAccessAvailable;
        var all = live.Hardware.Devices.SelectMany(d => d.Sensors.Select(s => (d, s))).ToList();
        bool Has(DeviceKind dk, SensorKind sk) => all.Any(x => (x.d.Kind == dk) && x.s.Kind == sk && x.s.Value.HasValue);
        bool HasBoard(SensorKind sk) => all.Any(x => x.d.Kind is DeviceKind.SuperIO or DeviceKind.Motherboard or DeviceKind.EmbeddedController && x.s.Kind == sk && x.s.Value.HasValue);

        (string, bool, string)[] checks =
        [
            ("CPU temperature", Has(DeviceKind.Cpu, SensorKind.Temperature), lowLevel ? "Not exposed by this CPU" : "Requires administrator mode + PawnIO driver"),
            ("CPU power", Has(DeviceKind.Cpu, SensorKind.Power), lowLevel ? "Not exposed by this CPU" : "Requires administrator mode + PawnIO driver"),
            ("GPU temperature", Has(DeviceKind.Gpu, SensorKind.Temperature), "Not exposed by the GPU driver"),
            ("GPU hot spot", all.Any(x => x.d.Kind == DeviceKind.Gpu && x.s.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) && x.s.Value.HasValue), "Not exposed by this GPU/driver"),
            ("Motherboard / VRM / chipset temperatures", HasBoard(SensorKind.Temperature), lowLevel ? "No supported Super I/O chip detected" : "Requires administrator mode + PawnIO driver"),
            ("Fans and pumps", all.Any(x => x.s.Kind == SensorKind.Fan && x.s.Value.HasValue), lowLevel ? "No fan headers reported" : "Requires administrator mode + PawnIO driver"),
            ("Voltages", HasBoard(SensorKind.Voltage), lowLevel ? "No supported Super I/O chip detected" : "Requires administrator mode + PawnIO driver"),
            ("RAM temperatures", Has(DeviceKind.Memory, SensorKind.Temperature), lowLevel ? "Modules without thermal sensors, or SPD access unsupported" : "Requires administrator mode + PawnIO driver"),
            ("Drive temperatures", Has(DeviceKind.Storage, SensorKind.Temperature) || _monitoring.Drives.Any(d => d.Health.Temperature.HasValue), Elevated() ? "Not reported by the drives" : "Requires administrator mode"),
        ];
        Coverage.Clear();
        foreach (var (name, ok, why) in checks)
        {
            Coverage.Add(new ValueRow(name)
            {
                Value = ok ? "Available" : why,
                State = ok ? MetricState.Available : why.StartsWith("Requires", StringComparison.Ordinal) ? MetricState.PermissionRequired : MetricState.Unsupported,
                Source = hw.Name,
            });
        }
    }

    private static bool Elevated() => Services.Elevation.IsElevated;
}

// =====================================================================================================================
// Network
// =====================================================================================================================
public sealed partial class NetworkViewModel : PageViewModel
{
    private readonly MonitoringService _monitoring;
    private readonly ISettingsService _settings;
    private string _adapterSignature = string.Empty;

    public NetworkViewModel(MonitoringService monitoring, ISettingsService settings)
    {
        _monitoring = monitoring;
        _settings = settings;
    }

    public override string Title => "Network";
    public override string Icon => "";

    public ObservableCollection<NetworkAdapterSample> Adapters { get; } = new();
    public RowList Rows { get; } = new();

    [ObservableProperty] private NetworkAdapterSample? _selectedAdapter;
    [ObservableProperty] private bool _showVirtual;
    [ObservableProperty] private string _downText = "—";
    [ObservableProperty] private string _upText = "—";
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _series;
    [ObservableProperty] private string _downStats = string.Empty;
    [ObservableProperty] private string _upStats = string.Empty;

    partial void OnShowVirtualChanged(bool value) => _adapterSignature = string.Empty;

    partial void OnSelectedAdapterChanged(NetworkAdapterSample? value)
    {
        if (value is null) return;
        if (_settings.Current.SelectedNetworkAdapterId != value.Id)
        {
            _settings.Current.SelectedNetworkAdapterId = value.Id;
            _settings.Save();
        }
        Rows.Reset();
    }

    public override void OnLive(LiveSnapshot live)
    {
        var list = live.Network.Where(n => ShowVirtual || !n.IsVirtualOrLoopback).ToList();
        var sig = string.Join(",", list.Select(n => n.Id + n.Status));
        if (sig != _adapterSignature)
        {
            _adapterSignature = sig;
            var selectedId = SelectedAdapter?.Id ?? _settings.Current.SelectedNetworkAdapterId;
            Adapters.Clear();
            foreach (var n in list) Adapters.Add(n);
            SelectedAdapter = Adapters.FirstOrDefault(a => a.Id == selectedId) ?? Adapters.FirstOrDefault(a => a.Status == "Up") ?? Adapters.FirstOrDefault();
        }

        var a = SelectedAdapter is null ? null : live.Network.FirstOrDefault(n => n.Id == SelectedAdapter.Id);
        if (a is null) return;
        var reveal = _settings.Current.ShowSensitiveIdentifiers;
        const string src = DataSources.NetworkInterface;
        DownText = Fmt.Rate(a.DownloadBytesPerSecond);
        UpText = Fmt.Rate(a.UploadBytesPerSecond);
        Rows.Set("Adapter", a.Description, src);
        Rows.Set("Connection state", a.Status, src);
        Rows.Set("Interface type", a.InterfaceType, src);
        Rows.Set("Link speed", a.LinkSpeedBitsPerSecond is long sp ? Fmt.Bits(sp) : "Not reported", src, a.LinkSpeedBitsPerSecond is null ? MetricState.Unavailable : MetricState.Available);
        Rows.Set("Utilization", a.UtilizationPercent is double u ? $"{u:0.0}%" : "Unavailable", DataSources.Calculated,
            a.UtilizationPercent is null ? MetricState.Unavailable : MetricState.Available, "(download + upload) ÷ link speed");
        Rows.Set("Received since adapter start", Fmt.Bytes(a.TotalBytesReceived), src, note: "Interface counters reset when the adapter reconnects");
        Rows.Set("Sent since adapter start", Fmt.Bytes(a.TotalBytesSent), src);
        Rows.Set("Received this session", Fmt.Bytes(a.SessionBytesReceived), DataSources.Calculated, note: "Since OmniSpec started");
        Rows.Set("Sent this session", Fmt.Bytes(a.SessionBytesSent), DataSources.Calculated, note: "Since OmniSpec started");
        Rows.Set("Connection uptime", a.ObservedUpSince is { } up ? Fmt.Duration(DateTimeOffset.Now - up) : a.Status == "Up" ? "Connected before OmniSpec started" : "Not connected",
            DataSources.Calculated, a.ObservedUpSince is null ? MetricState.Unavailable : MetricState.Available,
            "Windows does not expose link-up time; OmniSpec measures it from when it observed the connection come up");
        Rows.Set("IP addresses", a.IpAddresses.Count == 0 ? "None" : reveal ? string.Join(", ", a.IpAddresses) : "Hidden for privacy", src);
        Rows.Set("MAC address", a.MacAddress is null ? "None" : reveal ? a.MacAddress : "Hidden for privacy", src);

        var h = _monitoring.History;
        Series = [Charts.Series(h, HistoryKeys.NetDown(a.Id), "Download", "Brush.Accent"), Charts.Series(h, HistoryKeys.NetUp(a.Id), "Upload", "Brush.Series2")];
        var ds = h.Stats(HistoryKeys.NetDown(a.Id));
        var us = h.Stats(HistoryKeys.NetUp(a.Id));
        DownStats = ds.Count == 0 ? "" : $"↓ peak {Fmt.Rate(ds.Max)} · avg {Fmt.Rate(ds.Avg)}";
        UpStats = us.Count == 0 ? "" : $"↑ peak {Fmt.Rate(us.Max)} · avg {Fmt.Rate(us.Avg)}";
    }
}

// =====================================================================================================================
// Battery
// =====================================================================================================================
public sealed partial class BatteryViewModel : PageViewModel
{
    public override string Title => "Battery";
    public override string Icon => "";

    public RowList Measured { get; } = new();
    public RowList Estimated { get; } = new();

    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private double _percent = double.NaN;
    [ObservableProperty] private string _percentText = "—";
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _powerPlan = "Unavailable";

    public override void OnLive(LiveSnapshot live)
    {
        var b = live.Battery;
        PowerPlan = b?.PowerPlan ?? "Unavailable";
        HasBattery = b?.HasBattery == true;
        if (b is null || !b.HasBattery) return;

        Percent = Fmt.Value(b.ChargePercent);
        PercentText = Fmt.Pct(b.ChargePercent);
        StatusText = b.Status.HasValue ? b.Status.Value! : "";
        Measured.Set("Charge", b.ChargePercent, "0", "%");
        Measured.Set("Status", b.Status, s => s);
        Measured.Set("Charging", b.IsCharging, v => v ? "Yes" : "No");
        Measured.Set("On AC power", b.OnAcPower, v => v ? "Yes" : "No");
        Measured.Set("Design capacity", b.DesignCapacityMWh, "N0", "mWh");
        Measured.Set("Full-charge capacity", b.FullChargeCapacityMWh, "N0", "mWh");
        Measured.Set("Current capacity", b.RemainingCapacityMWh, "N0", "mWh");
        Measured.Set("Cycle count", b.CycleCount, "N0");
        Measured.Set("Voltage", b.VoltageV, "0.000", "V");
        Measured.Set("Charge / discharge rate", b.ChargeRateW, "0.0", "W");
        if (b.Name is not null) Measured.Set("Battery", b.Name, DataSources.Wmi);
        if (b.Chemistry is not null) Measured.Set("Chemistry", b.Chemistry, DataSources.Wmi);

        Estimated.Set("Battery wear (estimate)", b.WearPercentEstimate, "0.0", "%");
        Estimated.Set("Estimated time remaining", b.EstimatedRuntime, t => Fmt.Duration(t));
    }
}
