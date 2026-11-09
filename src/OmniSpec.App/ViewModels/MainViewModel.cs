using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniSpec.App.Services;
using OmniSpec.Core.Alerts;
using OmniSpec.Core.Live;
using OmniSpec.Core.Settings;
using OmniSpec.Monitoring;

namespace OmniSpec.App.ViewModels;

/// <summary>
/// Shell view model. Receives background events from <see cref="MonitoringService"/>, coalesces them and forwards
/// them on the UI thread to the active page only — inactive pages and a hidden window cost nothing.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly MonitoringService _monitoring;
    private readonly ISettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private LiveSnapshot? _pendingLive;
    private int _livePending;

    public MainViewModel(MonitoringService monitoring, ISettingsService settings, IEnumerable<PageViewModel> pages, WidgetViewModel widget)
    {
        _monitoring = monitoring;
        _settings = settings;
        _dispatcher = Application.Current.Dispatcher;
        // The Battery page only appears on devices that actually have a system battery.
        Pages = pages.Where(p => p is not BatteryViewModel || HasSystemBattery()).ToList();
        Widget = widget;
        _current = Pages[0];
        IsElevated = Elevation.IsElevated;
        IsMock = monitoring.HardwareProvider.IsMock;

        monitoring.LiveUpdated += (_, live) =>
        {
            _pendingLive = live;
            if (Interlocked.Exchange(ref _livePending, 1) == 0)
                _dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyLive);
        };
        monitoring.DrivesUpdated += (_, drives) => _dispatcher.BeginInvoke(() =>
        {
            foreach (var p in Pages) p.OnDrives(drives);
            Widget.OnDrives(drives);
        });
        monitoring.SystemInfoUpdated += (_, report) => _dispatcher.BeginInvoke(() =>
        {
            foreach (var p in Pages) p.OnSystemInfo(report);
        });
        monitoring.HardwareReady += (_, _) => _dispatcher.BeginInvoke(UpdateProviderStatus);
        monitoring.AlertRaised += (_, alert) => _dispatcher.BeginInvoke(() => RecentAlerts.Insert(0, alert));
        UpdateProviderStatus();
    }

    public IReadOnlyList<PageViewModel> Pages { get; }
    public WidgetViewModel Widget { get; }
    public ObservableCollection<AlertEvent> RecentAlerts { get; } = new();

    [ObservableProperty] private PageViewModel _current;
    [ObservableProperty] private bool _isWindowVisible = true;
    [ObservableProperty] private string _providerStatus = "Starting hardware monitoring…";
    [ObservableProperty] private string _lastUpdateText = string.Empty;

    public bool IsElevated { get; }
    public bool IsMock { get; }
    public string PrivilegeText => IsElevated ? "Administrator" : "Standard user";
    public string Version => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    partial void OnCurrentChanged(PageViewModel? oldValue, PageViewModel newValue)
    {
        oldValue?.OnDeactivated();
        newValue.OnActivated();
        if (_monitoring.Latest is { } live) newValue.OnLive(live);
    }

    partial void OnIsWindowVisibleChanged(bool value)
    {
        if (value) { Current.OnActivated(); if (_monitoring.Latest is { } l) Current.OnLive(l); }
        else Current.OnDeactivated();
    }

    private void ApplyLive()
    {
        Interlocked.Exchange(ref _livePending, 0);
        var live = _pendingLive;
        if (live is null) return;
        if (IsWindowVisible)
        {
            Current.OnLive(live);
            var sensors = live.Hardware.Devices.Sum(d => d.Sensors.Count(s => s.Value.HasValue));
            ProviderStatus = $"Monitoring {sensors} sensors · {live.Gpus.Count} GPU(s) · {_monitoring.Drives.Count} drive(s)";
            LastUpdateText = $"Updated {live.Timestamp:HH:mm:ss}";
        }
        Widget.OnLive(live);
    }

    private void UpdateProviderStatus()
    {
        ProviderStatus = _monitoring.IsHardwareReady ? "Monitoring active" : "Starting hardware monitoring…";
    }

    private static bool HasSystemBattery()
    {
        try { return System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery; }
        catch { return false; }
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => Current = page;

    /// <summary>Navigates to the first page whose title starts with <paramref name="title"/> (used by the --page= argument).</summary>
    public void NavigateByTitle(string title)
    {
        var page = Pages.FirstOrDefault(p => p.Title.StartsWith(title, StringComparison.OrdinalIgnoreCase));
        if (page is not null) Current = page;
    }

    public void NavigateTo<T>() where T : PageViewModel
    {
        var page = Pages.OfType<T>().FirstOrDefault();
        if (page is not null) Current = page;
    }

    [RelayCommand]
    private void RestartAsAdministrator()
    {
        var r = MessageBox.Show(
            "OmniSpec will restart with administrator rights so it can read additional sensors " +
            "(CPU temperature/power, motherboard sensors, drive reliability counters and SMART data).\n\n" +
            "Windows will ask for confirmation. OmniSpec does not install any drivers or services.\n\nContinue?",
            "Restart as administrator", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (r != MessageBoxResult.OK) return;
        if (Elevation.RestartElevated()) ((App)Application.Current).ShutdownForRestart();
    }


    [RelayCommand]
    private void ToggleWidget() => Widget.IsVisible = !Widget.IsVisible;

    [RelayCommand]
    private void ClearAlerts() => RecentAlerts.Clear();
}
