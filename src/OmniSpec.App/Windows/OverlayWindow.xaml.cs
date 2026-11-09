using System.Windows;
using System.Windows.Threading;
using OmniSpec.App.ViewModels;
using OmniSpec.Core.Settings;
using OmniSpec.Monitoring.Fps;

namespace OmniSpec.App.Windows;

/// <summary>Compact, click-through, always-on-top performance overlay shown while an FPS capture runs.</summary>
public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _vm;
    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _timer;

    public OverlayWindow(OverlayViewModel vm, ISettingsService settings)
    {
        _vm = vm;
        _settings = settings;
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) => WindowInterop.MakeClickThrough(this);
        SizeChanged += (_, _) => Position();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => { _vm.Update(); Topmost = true; };
        IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };
    }

    private void Position()
    {
        var area = SystemParameters.WorkArea;
        Opacity = Math.Clamp(_settings.Current.Fps.OverlayOpacity, 0.3, 1);
        switch (_settings.Current.Fps.OverlayCorner)
        {
            case "TopRight": Left = area.Right - ActualWidth; Top = area.Top; break;
            case "BottomLeft": Left = area.Left; Top = area.Bottom - ActualHeight; break;
            case "BottomRight": Left = area.Right - ActualWidth; Top = area.Bottom - ActualHeight; break;
            default: Left = area.Left; Top = area.Top; break;
        }
    }

    /// <summary>Shows or hides the overlay to match the capture state and user setting.</summary>
    public void Sync(FpsMonitor fps)
    {
        var shouldShow = fps.IsCapturing && _settings.Current.Fps.ShowOverlay;
        if (shouldShow && !IsVisible) { Show(); Position(); }
        else if (!shouldShow && IsVisible) Hide();
        else if (shouldShow) Position();
    }
}
