using System.Windows;
using System.Windows.Interop;
using OmniSpec.App.Services;
using OmniSpec.App.ViewModels;

namespace OmniSpec.App.Windows;

/// <summary>
/// Click-through, never-focused, always-on-top overlay window. It is an ordinary desktop window drawn by Windows'
/// compositor above the game — it does not touch the game process, so it is safe with anti-cheat systems.
/// </summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow(OverlayViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) => WindowInterop.MakeClickThrough(this);
    }

    /// <summary>Shows the overlay in a corner of the given monitor (physical pixels) and re-asserts top-most z-order.</summary>
    public void ShowOnTop(GameDetector.RECT monitor, string corner, double opacity)
    {
        Opacity = opacity;
        if (!IsVisible) Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var (w, h) = WindowInterop.PixelSize(hwnd);
        var x = corner is "TopRight" or "BottomRight" ? monitor.Right - w : monitor.Left;
        var y = corner is "BottomLeft" or "BottomRight" ? monitor.Bottom - h : monitor.Top;
        WindowInterop.PlaceTopmost(hwnd, x, y);
    }
}
