using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using OmniSpec.App.ViewModels;
using OmniSpec.App.Windows;
using OmniSpec.Core.Fps;
using OmniSpec.Core.Settings;
using OmniSpec.Monitoring;
using OmniSpec.Monitoring.Fps;

namespace OmniSpec.App.Services;

/// <summary>
/// Drives the in-game overlay: detects the game in front, optionally starts FPS capture for it, decides visibility
/// (mode + hotkey), keeps the overlay on top of the game's monitor and refreshes its content twice a second.
/// The overlay is a separate click-through window — nothing is injected into games.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private readonly OverlayViewModel _vm;
    private readonly ISettingsService _settings;
    private readonly FpsMonitor _fps;
    private readonly MonitoringService _monitoring;
    private readonly ILogger<OverlayController> _logger;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<int> _failedAutoCapture = new();
    private OverlayWindow? _window;
    private GlobalHotkey? _hotkey;
    private int? _autoPid;
    private int _candidatePid;
    private int _candidateTicks;
    private bool _userHidden;
    private bool _userShown;
    private DateTime _availabilityCheckedAt = DateTime.MinValue;
    private FpsProviderState _fpsState;

    public OverlayController(OverlayViewModel vm, ISettingsService settings, FpsMonitor fps, MonitoringService monitoring, ILogger<OverlayController> logger)
    {
        _vm = vm;
        _settings = settings;
        _fps = fps;
        _monitoring = monitoring;
        _logger = logger;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Tick();
    }

    public GameWindow? CurrentGame { get; private set; }
    public bool IsAutoCapturing => _autoPid is int pid && _fps.IsCapturing && _fps.Session?.ProcessId == pid;
    public event EventHandler? StatusChanged;

    /// <summary>Shows the overlay regardless of mode (used for the "test overlay" switch on the settings page).</summary>
    public bool ForceVisible { get; set; }

    public void Start()
    {
        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += (_, _) => ToggleByHotkey();
        RegisterHotkey();
        _timer.Start();
    }

    public bool RegisterHotkey()
    {
        var ok = _hotkey?.Register(_settings.Current.Overlay.Hotkey) ?? false;
        if (!ok) _logger.LogInformation("Overlay hotkey {Hotkey} could not be registered", _settings.Current.Overlay.Hotkey);
        return ok;
    }

    private void ToggleByHotkey()
    {
        if (_settings.Current.Overlay.Mode == "Manual") _userShown = !_userShown;
        else _userHidden = !_userHidden;
        Tick();
    }

    private void Tick()
    {
        var s = _settings.Current.Overlay;
        var previousGame = CurrentGame;
        CurrentGame = s.Enabled || ForceVisible ? GameDetector.Detect(s) : null;
        if (CurrentGame?.ProcessId != previousGame?.ProcessId) StatusChanged?.Invoke(this, EventArgs.Empty);

        if (s.Enabled && s.AutoCapture) AutoCapture(CurrentGame);

        var foreground = GameDetector.ForegroundProcessId();
        var capturingInFront = _fps.IsCapturing && _fps.Session?.ProcessId == foreground;
        var visible = ForceVisible || (s.Enabled && s.Mode switch
        {
            "Always" => !_userHidden,
            "Manual" => _userShown,
            _ => !_userHidden && (CurrentGame is not null || capturingInFront),
        });

        if (!visible)
        {
            if (_window?.IsVisible == true) _window.Hide();
            return;
        }

        if (_monitoring.Latest is { } live) _vm.Update(live);
        _window ??= new OverlayWindow(_vm);
        var monitor = CurrentGame?.Monitor ?? GameDetector.PrimaryMonitor();
        _window.ShowOnTop(monitor, s.Corner, s.Opacity);
    }

    private void AutoCapture(GameWindow? game)
    {
        // Never interfere with a capture the user started manually on the FPS page.
        if (_fps.IsCapturing && _fps.Session?.ProcessId != _autoPid) return;
        if (game is null) { _candidateTicks = 0; return; }

        // Require the game to stay in front for ~1.5 s before starting, to ignore brief focus changes.
        if (game.ProcessId != _candidatePid) { _candidatePid = game.ProcessId; _candidateTicks = 0; }
        if (++_candidateTicks < 3) return;
        if (_fps.IsCapturing && _fps.Session?.ProcessId == game.ProcessId) return;
        if (_failedAutoCapture.Contains(game.ProcessId)) return;

        if (DateTime.Now - _availabilityCheckedAt > TimeSpan.FromSeconds(15))
        {
            _fpsState = _fps.CheckAvailability().State;
            _availabilityCheckedAt = DateTime.Now;
        }
        if (_fpsState is not (FpsProviderState.Ready or FpsProviderState.Running)) return;

        try
        {
            _fps.GpuIndex = _settings.Current.SelectedGpuIndex;
            _fps.Start(game.ProcessId, game.ProcessName);
            _autoPid = game.ProcessId;
            _logger.LogInformation("Overlay: automatic FPS capture started for {Game} ({Pid})", game.ProcessName, game.ProcessId);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _failedAutoCapture.Add(game.ProcessId);
            _logger.LogWarning(ex, "Overlay: automatic FPS capture failed for {Game}", game.ProcessName);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _hotkey?.Dispose();
        _window?.Close();
    }
}
