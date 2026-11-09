using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniSpec.App.Services;
using OmniSpec.Core.Live;
using OmniSpec.Core.Metrics;
using OmniSpec.Core.Settings;
using OmniSpec.Core.SystemInfo;
using OmniSpec.Infrastructure;
using OmniSpec.Infrastructure.Export;
using OmniSpec.Monitoring;

namespace OmniSpec.App.ViewModels;

// =====================================================================================================================
// System information
// =====================================================================================================================
public sealed record InfoRowView(string Label, string Value, MetricState State, string Source)
{
    public string ToolTipText => $"Source: {Source}";
}

public sealed record InfoSectionView(string Title, IReadOnlyList<InfoRowView> Items, IReadOnlyList<InfoSectionView> Children);

public sealed partial class SystemInfoViewModel : PageViewModel
{
    private readonly MonitoringService _monitoring;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;

    public SystemInfoViewModel(MonitoringService monitoring, ISettingsService settings, AppPaths paths)
    {
        _monitoring = monitoring;
        _settings = settings;
        _paths = paths;
    }

    public override string Title => "System Information";
    public override string Icon => "";

    public ObservableCollection<InfoSectionView> Sections { get; } = new();
    [ObservableProperty] private InfoSectionView? _selectedSection;
    [ObservableProperty] private string _collectedText = "Collecting…";
    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private string? _statusMessage;

    public bool RevealSensitive
    {
        get => _settings.Current.ShowSensitiveIdentifiers;
        set
        {
            _settings.Current.ShowSensitiveIdentifiers = value;
            _settings.Save();
            OnPropertyChanged();
            Rebuild(_monitoring.SystemInfo);
        }
    }

    public override void OnActivated()
    {
        OnPropertyChanged(nameof(RevealSensitive));
        if (Sections.Count == 0) OnSystemInfo(_monitoring.SystemInfo);
    }

    public override void OnSystemInfo(SystemInfoReport report)
    {
        if (report.CollectedAt == DateTimeOffset.MinValue) return;
        IsBusy = false;
        CollectedText = $"Collected {report.CollectedAt:g}";
        Rebuild(report);
    }

    private void Rebuild(SystemInfoReport report)
    {
        var title = SelectedSection?.Title;
        var reveal = RevealSensitive;
        InfoSectionView Map(InfoSection s) => new(
            s.Title,
            s.Items.Where(i => i.State == MetricState.Available).Select(i => new InfoRowView(i.Label, i.DisplayValue(reveal), i.State, i.Source)).ToList(),
            (s.Children ?? []).Select(Map).Where(c => c.Items.Count > 0).ToList());
        Sections.Clear();
        foreach (var s in report.Sections.Select(Map).Where(s => s.Items.Count > 0 || s.Children.Count > 0)) Sections.Add(s);
        SelectedSection = Sections.FirstOrDefault(s => s.Title == title) ?? Sections.FirstOrDefault();
    }

    [RelayCommand]
    private async Task Refresh()
    {
        IsBusy = true;
        CollectedText = "Collecting…";
        await _monitoring.RefreshSystemInfoAsync();
        IsBusy = false;
    }

    [RelayCommand]
    private void ExportJson() => Export("json");

    [RelayCommand]
    private void ExportText() => Export("txt");

    private void Export(string kind)
    {
        var name = $"OmniSpec-report-{DateTime.Now:yyyyMMdd-HHmmss}.{kind}";
        var path = Shell.SaveFile("Export system report", kind == "json" ? "JSON report (*.json)|*.json" : "Text report (*.txt)|*.txt", name, _paths.ExportDirectory);
        if (path is null) return;
        try
        {
            var report = new SystemReport(
                typeof(SystemInfoViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                DateTimeOffset.Now,
                _monitoring.SystemInfo,
                _monitoring.Drives.Select(d => (d.Drive, d.Health)).ToList(),
                _monitoring.Latest,
                RevealSensitive);
            if (kind == "json")
            {
                using var fs = File.Create(path);
                Exporters.WriteJsonReport(fs, report);
            }
            else
            {
                using var w = new StreamWriter(path);
                Exporters.WriteTextReport(w, report);
            }
            StatusMessage = $"Saved {Path.GetFileName(path)}";
            Shell.SelectFile(path);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }
}

// =====================================================================================================================
// Processes
// =====================================================================================================================
public sealed partial class ProcessRow : ObservableObject
{
    public ProcessRow(int pid, string name) { Pid = pid; Name = name; }
    public int Pid { get; }
    public string Name { get; }
    [ObservableProperty] private double _cpu = -1;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private long _memory;
    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private long _privateBytes;
    [ObservableProperty] private string _privateText = "—";
    [ObservableProperty] private double _diskRead = -1;
    [ObservableProperty] private string _diskReadText = "—";
    [ObservableProperty] private double _diskWrite = -1;
    [ObservableProperty] private string _diskWriteText = "—";
    [ObservableProperty] private int _threads;
    [ObservableProperty] private string? _path;
    [ObservableProperty] private DateTime? _startTime;
    [ObservableProperty] private bool _accessLimited;
}

public sealed partial class ProcessesViewModel : PageViewModel
{
    private readonly MonitoringService _monitoring;
    private IDisposable? _subscription;
    private readonly Dictionary<int, ProcessRow> _rows = new();

    public ProcessesViewModel(MonitoringService monitoring)
    {
        _monitoring = monitoring;
        View = CollectionViewSource.GetDefaultView(Items);
        View.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Cpu), ListSortDirection.Descending));
        if (View is ICollectionViewLiveShaping live && live.CanChangeLiveSorting)
        {
            foreach (var p in new[] { nameof(ProcessRow.Cpu), nameof(ProcessRow.Memory), nameof(ProcessRow.MemoryPercent), nameof(ProcessRow.PrivateBytes), nameof(ProcessRow.DiskRead), nameof(ProcessRow.DiskWrite), nameof(ProcessRow.Threads) })
                live.LiveSortingProperties.Add(p);
            live.IsLiveSorting = true;
        }
        View.Filter = o => o is ProcessRow r && (Search.Length == 0 || r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || r.Pid.ToString().Contains(Search));
        _monitoring.ProcessesUpdated += (_, list) =>
        {
            if (_subscription is null) return;
            Application.Current.Dispatcher.BeginInvoke(() => Apply(list));
        };
    }

    public override string Title => "Processes";
    public override string Icon => "";

    public ObservableCollection<ProcessRow> Items { get; } = new();
    public ICollectionView View { get; }
    public ObservableCollection<ValueRow> TopCpu { get; } = new();
    public ObservableCollection<ValueRow> TopMemory { get; } = new();

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private ProcessRow? _selected;
    [ObservableProperty] private string _summary = "Collecting process information…";

    partial void OnSearchChanged(string value) => View.Refresh();

    public override void OnActivated() => _subscription ??= _monitoring.SubscribeProcesses();

    public override void OnDeactivated()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private void Apply(IReadOnlyList<ProcessSample> list)
    {
        var seen = new HashSet<int>();
        foreach (var p in list)
        {
            seen.Add(p.Pid);
            if (!_rows.TryGetValue(p.Pid, out var row))
            {
                row = new ProcessRow(p.Pid, p.Name);
                _rows[p.Pid] = row;
                Items.Add(row);
            }
            row.Cpu = p.CpuPercent ?? -1;
            row.CpuText = p.CpuPercent is double c ? $"{c:0.0}%" : "—";
            row.Memory = p.WorkingSetBytes;
            row.MemoryText = Fmt.Bytes(p.WorkingSetBytes);
            row.MemoryPercent = Math.Round(p.MemoryPercent, 1);
            row.PrivateBytes = p.PrivateBytes;
            row.PrivateText = Fmt.Bytes(p.PrivateBytes);
            row.DiskRead = p.DiskReadBytesPerSec ?? -1;
            row.DiskReadText = p.DiskReadBytesPerSec is double r ? Fmt.Rate(r) : "—";
            row.DiskWrite = p.DiskWriteBytesPerSec ?? -1;
            row.DiskWriteText = p.DiskWriteBytesPerSec is double w ? Fmt.Rate(w) : "—";
            row.Threads = p.ThreadCount;
            row.Path = p.Path;
            row.StartTime = p.StartTime;
            row.AccessLimited = p.AccessLimited;
        }
        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Items.Remove(_rows[gone]);
            _rows.Remove(gone);
        }

        TopCpu.Clear();
        foreach (var p in list.Where(p => p.CpuPercent is > 0).OrderByDescending(p => p.CpuPercent).Take(5))
            TopCpu.Add(new ValueRow(p.Name) { Value = $"{p.CpuPercent:0.0}%", State = MetricState.Available, Source = "Process times" });
        TopMemory.Clear();
        foreach (var p in list.OrderByDescending(p => p.WorkingSetBytes).Take(5))
            TopMemory.Add(new ValueRow(p.Name) { Value = Fmt.Bytes(p.WorkingSetBytes), State = MetricState.Available, Source = "Working set" });
        Summary = $"{list.Count} running processes · updated {DateTime.Now:HH:mm:ss}";
    }

    [RelayCommand]
    private void OpenLocation()
    {
        if (Selected?.Path is { } p && File.Exists(p)) Shell.SelectFile(p);
    }
}

// =====================================================================================================================
// Settings
// =====================================================================================================================
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;
    private readonly AppPaths _paths;
    private readonly HistoryCsvLogger _historyLogger;

    public SettingsViewModel(ISettingsService settings, ThemeService theme, AppPaths paths, HistoryCsvLogger historyLogger)
    {
        _settings = settings;
        _theme = theme;
        _paths = paths;
        _historyLogger = historyLogger;
        foreach (var m in WidgetSettings.AllMetrics)
            WidgetMetrics.Add(new WidgetMetricOption(m, settings));
    }

    public override string Title => "Settings";
    public override string Icon => "";

    private AppSettings S => _settings.Current;

    private void Change(Action apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        apply();
        _settings.Save();
        OnPropertyChanged(name);
    }

    // Appearance
    public bool IsDark { get => S.Theme == AppTheme.Dark; set { if (!value) return; Change(() => S.Theme = AppTheme.Dark); OnPropertyChanged(nameof(IsLight)); _theme.Apply(); } }
    public bool IsLight { get => S.Theme == AppTheme.Light; set { if (!value) return; Change(() => S.Theme = AppTheme.Light); OnPropertyChanged(nameof(IsDark)); _theme.Apply(); } }
    public IReadOnlyList<AccentOption> Accents { get; } = ThemeService.AccentPresets.Select(a => new AccentOption(a.Name, a.Hex)).ToList();
    public string AccentColor { get => S.AccentColor; set { Change(() => S.AccentColor = value); _theme.Apply(); } }

    [RelayCommand]
    private void SetAccent(string hex) => AccentColor = hex;

    // Polling
    public double SensorIntervalSeconds { get => S.SensorIntervalMs / 1000.0; set => Change(() => S.SensorIntervalMs = (int)(value * 1000)); }
    public double ProcessIntervalSeconds { get => S.ProcessIntervalMs / 1000.0; set => Change(() => S.ProcessIntervalMs = (int)(value * 1000)); }
    public int StorageIntervalSeconds { get => S.StorageIntervalSeconds; set => Change(() => S.StorageIntervalSeconds = value); }
    public int HistoryMinutes { get => S.HistoryMinutes; set => Change(() => S.HistoryMinutes = value); }
    public bool HistoryLoggingEnabled { get => S.HistoryLoggingEnabled; set => Change(() => S.HistoryLoggingEnabled = value); }
    public int HistoryLogIntervalSeconds { get => S.HistoryLogIntervalSeconds; set => Change(() => S.HistoryLogIntervalSeconds = value); }

    // Privacy
    public bool ShowSensitiveIdentifiers { get => S.ShowSensitiveIdentifiers; set => Change(() => S.ShowSensitiveIdentifiers = value); }

    // Hardware components (apply on restart)
    public bool EnableCpuSensors { get => S.EnableCpuSensors; set => Change(() => S.EnableCpuSensors = value); }
    public bool EnableGpuSensors { get => S.EnableGpuSensors; set => Change(() => S.EnableGpuSensors = value); }
    public bool EnableMotherboardSensors { get => S.EnableMotherboardSensors; set => Change(() => S.EnableMotherboardSensors = value); }
    public bool EnableStorageSensors { get => S.EnableStorageSensors; set => Change(() => S.EnableStorageSensors = value); }
    public bool EnableMemorySensors { get => S.EnableMemorySensors; set => Change(() => S.EnableMemorySensors = value); }
    public bool EnableControllerSensors { get => S.EnableControllerSensors; set => Change(() => S.EnableControllerSensors = value); }
    public bool EnablePsuSensors { get => S.EnablePsuSensors; set => Change(() => S.EnablePsuSensors = value); }

    // Alerts
    public bool AlertsEnabled { get => S.Alerts.Enabled; set => Change(() => S.Alerts.Enabled = value); }
    public double CpuTemperatureLimit { get => S.Alerts.CpuTemperatureC; set => Change(() => S.Alerts.CpuTemperatureC = value); }
    public double GpuTemperatureLimit { get => S.Alerts.GpuTemperatureC; set => Change(() => S.Alerts.GpuTemperatureC = value); }
    public double DriveTemperatureLimit { get => S.Alerts.DriveTemperatureC; set => Change(() => S.Alerts.DriveTemperatureC = value); }
    public double RamUsageLimit { get => S.Alerts.RamUsagePercent; set => Change(() => S.Alerts.RamUsagePercent = value); }
    public double LowDiskFreeLimit { get => S.Alerts.LowDiskFreePercent; set => Change(() => S.Alerts.LowDiskFreePercent = value); }
    public double BatteryWearLimit { get => S.Alerts.BatteryWearPercent; set => Change(() => S.Alerts.BatteryWearPercent = value); }
    public bool DriveHealthAlerts { get => S.Alerts.DriveHealthAlerts; set => Change(() => S.Alerts.DriveHealthAlerts = value); }
    public int AlertSustainSeconds { get => S.Alerts.SustainSeconds; set => Change(() => S.Alerts.SustainSeconds = Math.Clamp(value, 0, 600)); }

    // Window
    public bool MinimizeToTray { get => S.MinimizeToTray; set => Change(() => S.MinimizeToTray = value); }
    public bool CloseToTray { get => S.CloseToTray; set => Change(() => S.CloseToTray = value); }
    public bool StartMinimized { get => S.StartMinimized; set => Change(() => S.StartMinimized = value); }
    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled();
        set
        {
            try { StartupRegistration.Set(value); S.StartWithWindows = value; _settings.Save(); }
            catch (Exception ex) { MessageBox.Show($"Could not update the startup entry: {ex.Message}", "OmniSpec"); }
            OnPropertyChanged();
        }
    }

    // Widget
    public ObservableCollection<WidgetMetricOption> WidgetMetrics { get; } = new();

    // FPS
    public string PresentMonPath { get => S.Fps.PresentMonPath ?? string.Empty; set => Change(() => S.Fps.PresentMonPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim()); }
    public string PresentMonArguments { get => S.Fps.ExtraArguments; set => Change(() => S.Fps.ExtraArguments = value ?? string.Empty); }


    [RelayCommand]
    private void BrowsePresentMon()
    {
        var file = Shell.OpenFile("Select the PresentMon console executable", "PresentMon (PresentMon*.exe)|PresentMon*.exe|Executables (*.exe)|*.exe");
        if (file is not null) PresentMonPath = file;
    }

    // Automatic administrator mode (scheduled task)
    private bool? _autoAdmin;

    public bool AlwaysRunAsAdministrator
    {
        get => _autoAdmin ??= ElevatedLaunch.IsRegistered();
        set
        {
            if (value == AlwaysRunAsAdministrator) return;
            if (value) EnableAutoAdmin();
            else
            {
                if (!ElevatedLaunch.Unregister())
                    MessageBox.Show("The scheduled task could not be removed. You can delete \"OmniSpec\" from Task Scheduler manually.", "OmniSpec");
            }
            _autoAdmin = ElevatedLaunch.IsRegistered();
            OnPropertyChanged();
        }
    }

    public string AutoAdminHint => ElevatedLaunch.IsInProtectedLocation(out _)
        ? "OmniSpec starts with administrator rights so every sensor can be read. Windows confirms this once; later starts are silent."
        : "Available when OmniSpec is installed with the OmniSpec installer.";

    private void EnableAutoAdmin()
    {
        if (!ElevatedLaunch.IsInProtectedLocation(out var reason))
        {
            MessageBox.Show(reason, "Automatic administrator mode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Elevation.IsElevated)
        {
            if (!ElevatedLaunch.Register(out var error))
                MessageBox.Show($"Could not enable automatic administrator mode:\n\n{error}", "OmniSpec", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var answer = MessageBox.Show(
            "OmniSpec will create a Windows scheduled task that starts it with administrator rights, so you are not asked again on every start.\n\n" +
            "Windows will ask for permission once now, and OmniSpec will restart. Continue?",
            "Automatic administrator mode", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK || Environment.ProcessPath is not { } exe) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"--restarted {ElevatedLaunch.RegisterArg}") { UseShellExecute = true, Verb = "runas" });
            ((App)Application.Current).ShutdownForRestart();
        }
        catch (System.ComponentModel.Win32Exception) { /* UAC cancelled */ }
    }

    // Admin / about
    public bool IsElevated => Elevation.IsElevated;
    public string PrivilegeModeText => IsElevated ? "Administrator (elevated)" : "Standard user (not elevated)";
    public string DataFolder => _paths.Root;
    public string SettingsFile => _settings.SettingsPath;
    public bool IsPortable => _paths.IsPortable;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    [RelayCommand] private void OpenDataFolder() => Shell.OpenFolder(_paths.Root);
    [RelayCommand] private void OpenLogs() => Shell.OpenFolder(_paths.LogDirectory);
    [RelayCommand] private void OpenHistoryFolder() => Shell.OpenFolder(_historyLogger.Directory);
    [RelayCommand] private void OpenRepository() => Shell.OpenUrl("https://github.com/An-Nihal/OmniSpec");
    [RelayCommand] private void OpenPresentMonReleases() => Shell.OpenUrl("https://github.com/GameTechDev/PresentMon/releases");
    [RelayCommand] private void OpenPawnIo() => Shell.OpenUrl("https://pawnio.eu/");

    [RelayCommand]
    private void OpenThirdPartyNotices()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        if (File.Exists(file)) Shell.SelectFile(file);
        else Shell.OpenUrl("https://github.com/An-Nihal/OmniSpec/blob/main/THIRD-PARTY-NOTICES.md");
    }
}

public sealed record AccentOption(string Name, string Hex);

public sealed partial class WidgetMetricOption : ObservableObject
{
    private readonly ISettingsService _settings;

    public WidgetMetricOption(string key, ISettingsService settings)
    {
        Key = key;
        _settings = settings;
    }

    public string Key { get; }
    public string Label => Key switch
    {
        "cpu" => "CPU load & temperature", "gpu" => "GPU load & temperature", "ram" => "RAM usage", "vram" => "VRAM usage",
        "fps" => "FPS (while capturing)", "storage" => "Storage health", "network" => "Network throughput", "battery" => "Battery", "clock" => "Clock",
        _ => Key,
    };

    public bool IsEnabled
    {
        get => _settings.Current.Widget.Metrics.Contains(Key);
        set
        {
            var list = _settings.Current.Widget.Metrics;
            if (value && !list.Contains(Key)) list.Add(Key);
            if (!value) list.Remove(Key);
            // Keep canonical order.
            _settings.Current.Widget.Metrics = WidgetSettings.AllMetrics.Where(list.Contains).ToList();
            _settings.Save();
            OnPropertyChanged();
        }
    }
}
