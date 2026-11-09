using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using OmniSpec.Core.Alerts;
using OmniSpec.Core.Settings;

namespace OmniSpec.App.Services;

/// <summary>Swaps the light/dark brush dictionary and applies the accent color at runtime.</summary>
public sealed class ThemeService
{
    private readonly ISettingsService _settings;

    public ThemeService(ISettingsService settings) => _settings = settings;

    public void Apply()
    {
        var s = _settings.Current;
        var app = Application.Current;
        var dictionaries = app.Resources.MergedDictionaries;
        var themeUri = new Uri(s.Theme == AppTheme.Light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        var existing = dictionaries.FirstOrDefault(d => d.Source is not null && (d.Source.OriginalString.EndsWith("Dark.xaml") || d.Source.OriginalString.EndsWith("Light.xaml")));
        var fresh = new ResourceDictionary { Source = themeUri };
        if (existing is null) dictionaries.Insert(0, fresh);
        else dictionaries[dictionaries.IndexOf(existing)] = fresh;

        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(s.AccentColor); }
        catch { accent = Color.FromRgb(0x4F, 0x8C, 0xFF); }
        app.Resources["Brush.Accent"] = Frozen(new SolidColorBrush(accent));
        app.Resources["Brush.AccentSoft"] = Frozen(new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B)));
        app.Resources["Color.Accent"] = accent;
    }

    private static SolidColorBrush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

    public static readonly (string Name, string Hex)[] AccentPresets =
    [
        ("Azure", "#4F8CFF"), ("Violet", "#8B5CF6"), ("Emerald", "#10B981"), ("Amber", "#F59E0B"),
        ("Rose", "#F43F5E"), ("Cyan", "#06B6D4"), ("Lime", "#84CC16"), ("Slate", "#94A3B8"),
    ];
}

/// <summary>Process privilege helpers. Elevation is always user-initiated.</summary>
public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>Starts an elevated copy of OmniSpec (UAC prompt). Returns false if the user cancelled.</summary>
    public static bool RestartElevated()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        if (ElevatedLaunch.IsRegistered() && ElevatedLaunch.LaunchViaTask()) return true; // no UAC prompt needed
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--restarted") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC prompt cancelled
        }
    }
}

/// <summary>Passes launch arguments (e.g. --minimized) across the hand-off to the elevated scheduled task.</summary>
public static class LaunchHandoff
{
    private static string FileFor(OmniSpec.Infrastructure.AppPaths paths) => Path.Combine(paths.Root, "launch-args.txt");

    public static void Save(OmniSpec.Infrastructure.AppPaths paths, IEnumerable<string> args)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            File.WriteAllLines(FileFor(paths), args.Where(a => a.StartsWith("--", StringComparison.Ordinal)));
        }
        catch { /* best effort */ }
    }

    public static IReadOnlyList<string> Load(OmniSpec.Infrastructure.AppPaths paths)
    {
        var file = FileFor(paths);
        try
        {
            if (!File.Exists(file)) return [];
            var fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromSeconds(60);
            var lines = fresh ? File.ReadAllLines(file) : [];
            File.Delete(file);
            return lines;
        }
        catch { return []; }
    }
}

/// <summary>Per-user "start with Windows" via HKCU\...\Run. Only changed when the user toggles the setting.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OmniSpec";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe) key.SetValue(ValueName, $"\"{exe}\" --minimized");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>System tray icon, menu and Windows notifications (balloon/toast).</summary>
public sealed class TrayService : IDisposable
{
    private readonly ILogger<TrayService> _logger;
    private System.Windows.Forms.NotifyIcon? _icon;

    public TrayService(ILogger<TrayService> logger) => _logger = logger;

    public event EventHandler? OpenRequested;
    public event EventHandler? ToggleWidgetRequested;
    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/omnispec.ico"))?.Stream;
            _icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = stream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(stream),
                Text = "OmniSpec",
                Visible = true,
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open OmniSpec", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
            menu.Items.Add("Show / hide widget", null, (_, _) => ToggleWidgetRequested?.Invoke(this, EventArgs.Empty));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
            _icon.ContextMenuStrip = menu;
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) OpenRequested?.Invoke(this, EventArgs.Empty);
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray icon could not be created");
        }
    }

    public void SetTooltip(string text)
    {
        if (_icon is null) return;
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Notify(AlertEvent alert)
    {
        if (_icon is null) return;
        var icon = alert.Severity switch
        {
            AlertSeverity.Critical => System.Windows.Forms.ToolTipIcon.Error,
            AlertSeverity.Warning => System.Windows.Forms.ToolTipIcon.Warning,
            _ => System.Windows.Forms.ToolTipIcon.Info,
        };
        _icon.ShowBalloonTip(8000, alert.Title, alert.Message, icon);
    }

    public void Dispose()
    {
        if (_icon is null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }
}

public static class Shell
{
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    public static void SelectFile(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    public static string? SaveFile(string title, string filter, string defaultName, string? initialDir = null)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = defaultName };
        if (initialDir is not null) { Directory.CreateDirectory(initialDir); dlg.InitialDirectory = initialDir; }
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public static string? OpenFile(string title, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
