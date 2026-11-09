using System.ComponentModel;
using System.Windows;
using OmniSpec.App.ViewModels;
using OmniSpec.Core.Settings;

namespace OmniSpec.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ISettingsService _settings;

    public MainWindow(MainViewModel vm, ISettingsService settings)
    {
        _vm = vm;
        _settings = settings;
        InitializeComponent();
        DataContext = vm;
        RestoreSavedBounds();
        StateChanged += OnStateChanged;
        IsVisibleChanged += (_, _) => _vm.IsWindowVisible = IsVisible && WindowState != WindowState.Minimized;
    }

    /// <summary>True when the app is really exiting (tray "Exit", restart), so closing must not be cancelled.</summary>
    public bool AllowClose { get; set; }

    private void RestoreSavedBounds()
    {
        var s = _settings.Current;
        if (s.MainWindowWidth is double w && s.MainWindowHeight is double h && w > 400 && h > 300)
        {
            Width = w;
            Height = h;
        }
        if (s.MainWindowLeft is double l && s.MainWindowTop is double t)
        {
            // Only restore a position that is still on a connected screen.
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            if (l >= virtualLeft - 50 && t >= virtualTop - 50 &&
                l < virtualLeft + SystemParameters.VirtualScreenWidth - 100 && t < virtualTop + SystemParameters.VirtualScreenHeight - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l;
                Top = t;
            }
        }
        if (s.MainWindowMaximized) WindowState = WindowState.Maximized;
    }

    public void SaveBounds()
    {
        var s = _settings.Current;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        s.MainWindowLeft = bounds.Left;
        s.MainWindowTop = bounds.Top;
        s.MainWindowWidth = bounds.Width;
        s.MainWindowHeight = bounds.Height;
        s.MainWindowMaximized = WindowState == WindowState.Maximized;
        _settings.Save();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        _vm.IsWindowVisible = IsVisible && WindowState != WindowState.Minimized;
        if (WindowState == WindowState.Minimized && _settings.Current.MinimizeToTray) Hide();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveBounds();
        if (!AllowClose && _settings.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!AllowClose) ((App)Application.Current).RequestExit();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
