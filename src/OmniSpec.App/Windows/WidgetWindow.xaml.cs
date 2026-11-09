using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OmniSpec.App.ViewModels;
using OmniSpec.Core.Settings;

namespace OmniSpec.App.Windows;

/// <summary>
/// Borderless floating widget. Draggable (unless locked), resizable in width, remembers its position,
/// can stay on top or be pinned to the desktop (kept at the bottom of the z-order).
/// </summary>
public partial class WidgetWindow : Window
{
    private readonly WidgetViewModel _vm;
    private readonly ISettingsService _settings;

    public WidgetWindow(WidgetViewModel vm, ISettingsService settings)
    {
        _vm = vm;
        _settings = settings;
        InitializeComponent();
        DataContext = vm;

        var w = settings.Current.Widget;
        Width = Math.Clamp(w.Width, MinWidth, MaxWidth);
        if (w.Left is double l && w.Top is double t && IsOnScreen(l, t))
        {
            Left = l;
            Top = t;
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Top + 24;
        }

        SourceInitialized += (_, _) => WindowInterop.MakeToolWindow(this);
        MouseLeftButtonDown += OnDrag;
        LocationChanged += (_, _) => SavePosition();
        SizeChanged += (_, _) => { _settings.Current.Widget.Width = ActualWidth; };
        Deactivated += (_, _) => { if (_vm.PinToDesktop) WindowInterop.SendToBottom(this); };
        Activated += (_, _) => { if (_vm.PinToDesktop) WindowInterop.SendToBottom(this); };
        vm.PropertyChanged += OnVmChanged;
        ApplyZOrder();
    }

    private static bool IsOnScreen(double l, double t) =>
        l >= SystemParameters.VirtualScreenLeft - 50 && t >= SystemParameters.VirtualScreenTop - 50 &&
        l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 &&
        t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WidgetViewModel.AlwaysOnTop) or nameof(WidgetViewModel.PinToDesktop)) ApplyZOrder();
        if (e.PropertyName is nameof(WidgetViewModel.Locked)) ResizeMode = _vm.Locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
    }

    private void ApplyZOrder()
    {
        Topmost = _vm.AlwaysOnTop && !_vm.PinToDesktop;
        if (_vm.PinToDesktop && IsLoaded) WindowInterop.SendToBottom(this);
        ResizeMode = _vm.Locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Locked || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { /* mouse released */ }
        SavePosition(persist: true);
    }

    private void SavePosition(bool persist = false)
    {
        var w = _settings.Current.Widget;
        w.Left = Left;
        w.Top = Top;
        if (persist) _settings.Save();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePosition(persist: true);
        base.OnClosing(e);
    }

    private void Opacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) _vm.Opacity = v;
    }

    private void OpenDashboard_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowMainWindow();

    private void Metrics_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowMainWindow(navigateToSettings: true);
}
