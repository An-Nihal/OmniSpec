using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSpec.App.Services;
using OmniSpec.App.ViewModels;
using OmniSpec.App.Windows;
using OmniSpec.Core.Fps;
using OmniSpec.Core.Hardware;
using OmniSpec.Core.Live;
using OmniSpec.Core.Settings;
using OmniSpec.Core.Storage;
using OmniSpec.Core.SystemInfo;
using OmniSpec.Hardware;
using OmniSpec.Infrastructure;
using OmniSpec.Infrastructure.Export;
using OmniSpec.Infrastructure.Logging;
using OmniSpec.Infrastructure.Settings;
using OmniSpec.Monitoring;
using OmniSpec.Monitoring.Fps;
using OmniSpec.Monitoring.Providers;
using OmniSpec.SystemInformation;

namespace OmniSpec.App;

public partial class App : Application
{
    private const string MutexName = @"Local\OmniSpec.SingleInstance";
    private const string ActivateEventName = @"Local\OmniSpec.Activate";

    private ServiceProvider? _services;
    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private FileLoggerProvider? _fileLogger;
    private ILogger<App>? _logger;
    private MainWindow? _main;
    private WidgetWindow? _widget;
    private OverlayWindow? _overlay;
    private TrayService? _tray;
    private bool _exiting;
    private bool _activationListenerStarted;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToHashSet();
        var paths = new AppPaths();

        // Installer / settings hooks for the optional "always run as administrator" scheduled task.
        if (args.Contains(ElevatedLaunch.RegisterArg))
        {
            var ok = ElevatedLaunch.Register(out var error);
            if (args.Contains("--exit")) { Shutdown(ok ? 0 : 1); return; }
            if (!ok) MessageBox.Show($"Automatic administrator mode could not be enabled:\n\n{error}", "OmniSpec", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (args.Contains(ElevatedLaunch.UnregisterArg) && args.Contains("--exit"))
        {
            Shutdown(ElevatedLaunch.Unregister() ? 0 : 1);
            return;
        }

        var fromTask = args.Contains(ElevatedLaunch.FromTaskArg);
        if (!AcquireSingleInstance(waitForPrevious: args.Contains("--restarted") || fromTask))
        {
            Shutdown();
            return;
        }

        // If the user enabled automatic administrator mode, hand this launch over to the elevated task (no UAC prompt).
        if (!fromTask && !Elevation.IsElevated && !args.Contains("--no-elevate") && ElevatedLaunch.IsRegistered())
        {
            LaunchHandoff.Save(paths, e.Args);
            ReleaseSingleInstance();
            if (ElevatedLaunch.LaunchViaTask())
            {
                Shutdown();
                return;
            }
            // The task could not start: continue without elevation.
            AcquireSingleInstance(waitForPrevious: true);
        }
        var handoff = fromTask || args.Contains("--restarted") ? LaunchHandoff.Load(paths) : [];
        args.UnionWith(handoff.Select(a => a.ToLowerInvariant()));

        try { paths.EnsureCreated(); } catch { /* read-only location: continue with defaults */ }

        _fileLogger = new FileLoggerProvider(paths.LogDirectory);
        var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(_fileLogger);
            b.AddDebug();
        });
        _logger = loggerFactory.CreateLogger<App>();
        _logger.LogInformation("OmniSpec {Version} starting (elevated: {Elevated}, portable: {Portable})",
            typeof(App).Assembly.GetName().Version, Elevation.IsElevated, paths.IsPortable);
        HookExceptionHandlers();

        var useMock = false;
#if DEBUG
        useMock = args.Contains("--mock");
#endif
        _services = ConfigureServices(paths, loggerFactory, useMock);

        _services.GetRequiredService<ThemeService>().Apply();
        var settings = _services.GetRequiredService<ISettingsService>();
        var monitoring = _services.GetRequiredService<MonitoringService>();
        var historyLogger = _services.GetRequiredService<HistoryCsvLogger>();
        var fps = _services.GetRequiredService<FpsMonitor>();

        monitoring.LiveUpdated += (_, live) => historyLogger.OnLive(live);
        monitoring.Start();

        _tray = _services.GetRequiredService<TrayService>();
        _tray.Initialize();
        _tray.OpenRequested += (_, _) => ShowMainWindow();
        _tray.ToggleWidgetRequested += (_, _) => { var w = _services.GetRequiredService<WidgetViewModel>(); w.IsVisible = !w.IsVisible; };
        _tray.ExitRequested += (_, _) => RequestExit();
        monitoring.AlertRaised += (_, alert) => Dispatcher.BeginInvoke(() => _tray?.Notify(alert));
        monitoring.LiveUpdated += (_, live) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => UpdateTrayTooltip(live));

        _main = _services.GetRequiredService<MainWindow>();
        MainWindow = _main;
        if (e.Args.Concat(handoff).FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase)) is { } pageArg)
            _services.GetRequiredService<MainViewModel>().NavigateByTitle(pageArg["--page=".Length..]);
        var startHidden = args.Contains("--minimized") || settings.Current.StartMinimized;
#if DEBUG
        // Development aid: render the window off-screen to a PNG without taking focus, then exit.
        if (e.Args.FirstOrDefault(a => a.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase)) is { } shotArg)
        {
            CaptureScreenshotAndExit(shotArg["--screenshot=".Length..]);
            return;
        }
#endif
        if (!startHidden) _main.Show();
        else _main.Hide();

        var widgetVm = _services.GetRequiredService<WidgetViewModel>();
        widgetVm.PropertyChanged += (_, pe) => { if (pe.PropertyName == nameof(WidgetViewModel.IsVisible)) SyncWidget(); };
        SyncWidget();

        fps.SessionChanged += (_, _) => Dispatcher.BeginInvoke(SyncOverlay);
        fps.CaptureStopped += (_, _) => Dispatcher.BeginInvoke(SyncOverlay);
        settings.Changed += (_, _) => SyncOverlay();

        await Task.CompletedTask;
    }

    private ServiceProvider ConfigureServices(AppPaths paths, ILoggerFactory loggerFactory, bool useMock)
    {
        var s = new ServiceCollection();
        s.AddSingleton(loggerFactory);
        s.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        s.AddSingleton(paths);

        s.AddSingleton<ISettingsStore>(sp => new JsonSettingsStore(paths.SettingsFile, sp.GetRequiredService<ILogger<JsonSettingsStore>>()));
        s.AddSingleton<ISettingsService, SettingsService>();

#if DEBUG
        if (useMock) s.AddSingleton<IHardwareSensorProvider, OmniSpec.Hardware.Mock.MockHardwareProvider>();
        else s.AddSingleton<IHardwareSensorProvider, LibreHardwareMonitorProvider>();
#else
        s.AddSingleton<IHardwareSensorProvider, LibreHardwareMonitorProvider>();
#endif
        s.AddSingleton<ICpuUsageProvider, CpuUsageProvider>();
        s.AddSingleton<IMemoryProvider, MemoryProvider>();
        s.AddSingleton<INetworkProvider, NetworkProvider>();
        s.AddSingleton<IBatteryProvider>(sp => new BatteryProvider(() => TimeSpan.FromSeconds(sp.GetRequiredService<ISettingsService>().Current.BatteryDetailIntervalSeconds)));
        s.AddSingleton<IProcessProvider, ProcessProvider>();
        s.AddSingleton<ISystemInfoProvider, SystemInfoCollector>();
        s.AddSingleton<IStorageInfoProvider, StorageInfoProvider>();
        s.AddSingleton<MonitoringService>();
        s.AddSingleton<IFrameRateProvider>(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            return new PresentMonFrameProvider(sp.GetRequiredService<ILogger<PresentMonFrameProvider>>(),
                () => settings.Current.Fps.PresentMonPath, () => settings.Current.Fps.ExtraArguments);
        });
        s.AddSingleton<FpsMonitor>();
        s.AddSingleton(sp => new HistoryCsvLogger(paths.HistoryDirectory, sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<ILogger<HistoryCsvLogger>>()));

        s.AddSingleton<ThemeService>();
        s.AddSingleton<TrayService>();

        // Pages in navigation order.
        s.AddSingleton<PageViewModel, OverviewViewModel>();
        s.AddSingleton<PageViewModel, SystemInfoViewModel>();
        s.AddSingleton<PageViewModel, CpuViewModel>();
        s.AddSingleton<PageViewModel, GpuViewModel>();
        s.AddSingleton<PageViewModel, MemoryViewModel>();
        s.AddSingleton<PageViewModel, StorageViewModel>();
        s.AddSingleton<PageViewModel, GamingViewModel>();
        s.AddSingleton<PageViewModel, SensorsViewModel>();
        s.AddSingleton<PageViewModel, NetworkViewModel>();
        s.AddSingleton<PageViewModel, BatteryViewModel>();
        s.AddSingleton<PageViewModel, ProcessesViewModel>();
        s.AddSingleton<PageViewModel, SettingsViewModel>();
        s.AddSingleton<WidgetViewModel>();
        s.AddSingleton<OverlayViewModel>();
        s.AddSingleton<MainViewModel>();
        s.AddSingleton<MainWindow>();
        return s.BuildServiceProvider();
    }

    // ------------------------------------------------------------------ single instance
    private bool AcquireSingleInstance(bool waitForPrevious)
    {
        _mutex = new Mutex(false, MutexName);
        bool acquired;
        try { acquired = _mutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { acquired = true; }

        _activateEvent ??= new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        if (!acquired)
        {
            _activateEvent.Set(); // ask the running instance to show itself
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        if (_activationListenerStarted) return true;
        _activationListenerStarted = true;
        var activate = _activateEvent;
        var thread = new Thread(() =>
        {
            try
            {
                while (activate.WaitOne())
                {
                    if (_exiting) break;
                    Dispatcher.BeginInvoke(() => ShowMainWindow());
                }
            }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "OmniSpec activation listener" };
        thread.Start();
        return true;
    }

    // ------------------------------------------------------------------ windows
    public void ShowMainWindow(bool navigateToSettings = false)
    {
        if (_main is null || _exiting) return;
        _main.ShowFromTray();
        if (navigateToSettings) _services?.GetRequiredService<MainViewModel>().NavigateTo<SettingsViewModel>();
    }

    private void SyncWidget()
    {
        if (_services is null || _exiting) return;
        var vm = _services.GetRequiredService<WidgetViewModel>();
        if (vm.IsVisible)
        {
            _widget ??= new WidgetWindow(vm, _services.GetRequiredService<ISettingsService>());
            _widget.Show();
            if (_services.GetRequiredService<MonitoringService>().Latest is { } live) vm.OnLive(live);
        }
        else
        {
            _widget?.Hide();
        }
    }

    private void SyncOverlay()
    {
        if (_services is null || _exiting) return;
        _overlay ??= new OverlayWindow(_services.GetRequiredService<OverlayViewModel>(), _services.GetRequiredService<ISettingsService>());
        _overlay.Sync(_services.GetRequiredService<FpsMonitor>());
    }

    private DateTime _lastTooltip;

    private void UpdateTrayTooltip(LiveSnapshot live)
    {
        if (_tray is null || DateTime.Now - _lastTooltip < TimeSpan.FromSeconds(3)) return;
        _lastTooltip = DateTime.Now;
        var cpu = live.CpuUsage?.TotalLoad.HasValue == true ? $"CPU {live.CpuUsage.TotalLoad.Value:0}%" : "CPU —";
        var gpu = live.Gpus.FirstOrDefault() is { Load.HasValue: true } g ? $" · GPU {g.Load.Value:0}%" : "";
        var ram = live.Memory is { TotalPhysical: > 0 } m ? $" · RAM {m.UsedPercent:0}%" : "";
        _tray.SetTooltip($"OmniSpec — {cpu}{gpu}{ram}");
    }

#if DEBUG
    private void CaptureScreenshotAndExit(string path)
    {
        var main = _main!;
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = -30000;
        main.Top = -30000;
        main.ShowActivated = false;
        main.ShowInTaskbar = false;
        main.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var visual = (System.Windows.Media.Visual)main.Content;
                var element = (FrameworkElement)main.Content;
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var bg = new System.Windows.Media.DrawingVisual();
                using (var dc = bg.RenderOpen())
                    dc.DrawRectangle((System.Windows.Media.Brush)FindResource("Brush.Background"), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                rtb.Render(bg);
                rtb.Render(visual);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = System.IO.File.Create(path);
                encoder.Save(fs);
            }
            catch (Exception ex) { _logger?.LogError(ex, "Screenshot failed"); }
            RequestExit();
        };
        timer.Start();
    }
#endif

    // ------------------------------------------------------------------ shutdown
    /// <summary>Exits for an elevated restart: release the single-instance lock first so the new process can start.</summary>
    public void ShutdownForRestart()
    {
        ReleaseSingleInstance();
        RequestExit();
    }

    private void ReleaseSingleInstance()
    {
        try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
        _mutex?.Dispose();
        _mutex = null;
    }

    public void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
        _logger?.LogInformation("OmniSpec shutting down");
        try
        {
            if (_main is not null)
            {
                _main.AllowClose = true;
                _main.SaveBounds();
                _main.Close();
            }
            _widget?.Close();
            _overlay?.Close();
            _tray?.Dispose();
            if (_services is not null)
            {
                _services.GetRequiredService<FpsMonitor>().Dispose();
                var monitoring = _services.GetRequiredService<MonitoringService>();
                // Dispose off the UI thread so provider shutdown can never deadlock the dispatcher.
                Task.Run(async () => await monitoring.DisposeAsync()).Wait(TimeSpan.FromSeconds(6));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during shutdown");
        }
        finally
        {
            _activateEvent?.Set();
            _activateEvent?.Dispose();
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            _fileLogger?.Dispose();
            Shutdown();
        }
    }

    private void HookExceptionHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            _logger?.LogError(e.Exception, "Unhandled UI exception");
            e.Handled = true; // keep monitoring alive; the error is logged
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger?.LogWarning(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }
}
