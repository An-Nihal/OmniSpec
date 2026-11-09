namespace OmniSpec.Core.Settings;

public enum AppTheme { Dark, Light }

/// <summary>User settings persisted as JSON. All properties have safe defaults so partial/old files load correctly.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // Appearance
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string AccentColor { get; set; } = "#4F8CFF";

    // Polling (milliseconds unless noted)
    public int SensorIntervalMs { get; set; } = 1000;
    public int ProcessIntervalMs { get; set; } = 2000;
    public int StorageIntervalSeconds { get; set; } = 60;
    public int BatteryDetailIntervalSeconds { get; set; } = 30;

    // History
    public int HistoryMinutes { get; set; } = 15;
    public bool HistoryLoggingEnabled { get; set; }
    public int HistoryLogIntervalSeconds { get; set; } = 5;

    // Privacy
    public bool ShowSensitiveIdentifiers { get; set; }

    // Hardware library components
    public bool EnableCpuSensors { get; set; } = true;
    public bool EnableGpuSensors { get; set; } = true;
    public bool EnableMotherboardSensors { get; set; } = true;
    public bool EnableStorageSensors { get; set; } = true;
    public bool EnableMemorySensors { get; set; } = true;
    public bool EnableControllerSensors { get; set; } = true;
    public bool EnablePsuSensors { get; set; }

    // Window behaviour
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; }
    public bool StartMinimized { get; set; }
    public bool StartWithWindows { get; set; }
    public double? MainWindowLeft { get; set; }
    public double? MainWindowTop { get; set; }
    public double? MainWindowWidth { get; set; }
    public double? MainWindowHeight { get; set; }
    public bool MainWindowMaximized { get; set; }

    public int SelectedGpuIndex { get; set; }
    public string? SelectedNetworkAdapterId { get; set; }

    public WidgetSettings Widget { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();
    public FpsSettings Fps { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();

    /// <summary>Per-sensor custom warning limits, keyed by sensor id.</summary>
    public Dictionary<string, double> SensorLimits { get; set; } = new();

    /// <summary>Clamps values loaded from disk into supported ranges.</summary>
    public AppSettings Normalize()
    {
        SensorIntervalMs = Math.Clamp(SensorIntervalMs, 250, 10_000);
        ProcessIntervalMs = Math.Clamp(ProcessIntervalMs, 1000, 30_000);
        StorageIntervalSeconds = Math.Clamp(StorageIntervalSeconds, 10, 3600);
        BatteryDetailIntervalSeconds = Math.Clamp(BatteryDetailIntervalSeconds, 5, 600);
        HistoryMinutes = Math.Clamp(HistoryMinutes, 1, 240);
        HistoryLogIntervalSeconds = Math.Clamp(HistoryLogIntervalSeconds, 1, 3600);
        if (string.IsNullOrWhiteSpace(AccentColor) || !AccentColor.StartsWith('#')) AccentColor = "#4F8CFF";
        Widget ??= new();
        Alerts ??= new();
        Fps ??= new();
        Overlay = (Overlay ?? new()).Normalize();
        SensorLimits ??= new();
        Widget.Opacity = Math.Clamp(Widget.Opacity, 0.2, 1.0);
        Widget.Metrics ??= WidgetSettings.DefaultMetrics.ToList();
        return this;
    }
}

public sealed class WidgetSettings
{
    public static readonly string[] AllMetrics = ["cpu", "gpu", "ram", "vram", "fps", "storage", "network", "battery", "clock"];
    public static readonly string[] DefaultMetrics = ["cpu", "gpu", "ram", "fps", "storage", "network"];

    public bool Visible { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 280;
    public double Opacity { get; set; } = 0.92;
    public bool AlwaysOnTop { get; set; } = true;
    public bool PinToDesktop { get; set; }
    public bool Locked { get; set; }
    public bool Compact { get; set; }
    public List<string> Metrics { get; set; } = DefaultMetrics.ToList();
}

public sealed class AlertSettings
{
    public bool Enabled { get; set; } = true;
    public double CpuTemperatureC { get; set; } = 90;
    public double GpuTemperatureC { get; set; } = 85;
    public double DriveTemperatureC { get; set; } = 65;
    public double RamUsagePercent { get; set; } = 92;
    public double LowDiskFreePercent { get; set; } = 10;
    public double BatteryWearPercent { get; set; } = 40;
    public bool DriveHealthAlerts { get; set; } = true;
    /// <summary>Seconds a condition must persist before alerting (avoids alerts on momentary spikes).</summary>
    public int SustainSeconds { get; set; } = 10;
}

public sealed class FpsSettings
{
    /// <summary>Path to the PresentMon console executable (user supplied; OmniSpec never downloads/installs it).</summary>
    public string? PresentMonPath { get; set; }
    /// <summary>Extra arguments appended to the PresentMon command line.</summary>
    public string ExtraArguments { get; set; } = string.Empty;
}

/// <summary>In-game performance overlay (transparent, click-through, always-on-top window — never injected into games).</summary>
public sealed class OverlaySettings
{
    /// <summary>Every item the overlay can show, in display order.</summary>
    public static readonly string[] AllItems =
    [
        "fps", "onepercent", "pointone", "frametime", "frametimegraph",
        "gpuload", "gputemp", "gpuhotspot", "gpuclock", "gpupower", "gpufan",
        "cpuload", "cputemp", "cpuclock", "cpupower",
        "ram", "vram",
        "app", "time",
    ];

    public static readonly string[] DefaultItems =
        ["fps", "onepercent", "frametime", "frametimegraph", "gpuload", "gputemp", "cpuload", "cputemp", "ram", "vram"];

    public static readonly string[] Modes = ["Games", "Always", "Manual"];
    public static readonly string[] Corners = ["TopLeft", "TopRight", "BottomLeft", "BottomRight"];
    public static readonly string[] Layouts = ["Vertical", "Horizontal"];

    /// <summary>Default list of programs that are never treated as games (browsers, video players, shell).</summary>
    public static readonly string[] DefaultExcludedApps =
    [
        "explorer", "ApplicationFrameHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "LockApp",
        "TextInputHost", "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "iexplore", "vlc", "mpc-hc", "mpc-hc64",
        "mpc-be64", "mpv", "PotPlayerMini64", "PotPlayer64", "wmplayer", "Microsoft.Media.Player", "Video.UI", "obs64",
        "Taskmgr", "mstsc", "POWERPNT", "OmniSpec", "Discord", "Spotify",
    ];

    public bool Enabled { get; set; } = true;
    /// <summary>Games = show automatically while a fullscreen game is in front; Always = always visible; Manual = hotkey only.</summary>
    public string Mode { get; set; } = "Games";
    /// <summary>Start FPS capture automatically for the detected game.</summary>
    public bool AutoCapture { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Shift+O";
    public string Corner { get; set; } = "TopLeft";
    public string Layout { get; set; } = "Vertical";
    public double Opacity { get; set; } = 0.9;
    public double Scale { get; set; } = 1.0;
    public bool ColorWarnings { get; set; } = true;
    public List<string> Items { get; set; } = DefaultItems.ToList();
    public List<string> ExcludedApps { get; set; } = DefaultExcludedApps.ToList();
    /// <summary>Programs always treated as games, even when not fullscreen.</summary>
    public List<string> AlwaysGames { get; set; } = new();

    public OverlaySettings Normalize()
    {
        Items ??= DefaultItems.ToList();
        Items = Items.Where(AllItems.Contains).Distinct().ToList();
        ExcludedApps ??= DefaultExcludedApps.ToList();
        AlwaysGames ??= new();
        if (!Modes.Contains(Mode)) Mode = "Games";
        if (!Corners.Contains(Corner)) Corner = "TopLeft";
        if (!Layouts.Contains(Layout)) Layout = "Vertical";
        Opacity = Math.Clamp(Opacity, 0.3, 1.0);
        Scale = Math.Clamp(Scale, 0.7, 2.0);
        if (!HotkeyParser.TryParse(Hotkey, out _)) Hotkey = "Ctrl+Shift+O";
        return this;
    }
}

/// <summary>Parses hotkey strings such as "Ctrl+Shift+O" into Win32 RegisterHotKey modifiers and a virtual-key code.</summary>
public static class HotkeyParser
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    public readonly record struct Hotkey(uint Modifiers, uint VirtualKey);

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        uint mods = 0;
        uint? vk = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": mods |= ModControl; continue;
                case "SHIFT": mods |= ModShift; continue;
                case "ALT": mods |= ModAlt; continue;
                case "WIN" or "WINDOWS": mods |= ModWin; continue;
            }
            if (vk is not null) return false; // only one non-modifier key
            vk = part switch
            {
                { Length: 1 } when char.IsLetterOrDigit(part[0]) => part[0],
                ['F', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 24 => (uint)(0x70 + f - 1),
                "INSERT" or "INS" => 0x2D,
                "HOME" => 0x24,
                "END" => 0x23,
                "PAGEUP" or "PGUP" => 0x21,
                "PAGEDOWN" or "PGDN" => 0x22,
                "SCROLLLOCK" => 0x91,
                "PAUSE" => 0x13,
                _ => null,
            };
            if (vk is null) return false;
        }
        // Require a modifier for letters/digits so normal typing is never captured.
        if (vk is null || (mods == 0 && vk.Value is >= 0x30 and <= 0x5A)) return false;
        hotkey = new Hotkey(mods, vk.Value);
        return true;
    }
}

/// <summary>Decides whether a foreground program should be treated as a game for the overlay.</summary>
public static class GameRules
{
    public static bool IsGame(string processName, bool fullscreen, IEnumerable<string> excluded, IEnumerable<string> alwaysGames)
    {
        static string Clean(string s) => s.Trim().ToLowerInvariant().Replace(".exe", "");
        var name = Clean(processName);
        if (name.Length == 0) return false;
        if (alwaysGames.Any(a => Clean(a) == name)) return true;
        if (excluded.Any(e => Clean(e) == name)) return false;
        return fullscreen;
    }
}

public interface ISettingsStore
{
    string SettingsPath { get; }
    AppSettings Load();
    void Save(AppSettings settings);
}

/// <summary>Holds the live settings instance and notifies listeners when they change.</summary>
public interface ISettingsService
{
    AppSettings Current { get; }
    string SettingsPath { get; }
    event EventHandler? Changed;
    /// <summary>Persists the current settings and raises <see cref="Changed"/>.</summary>
    void Save();
}
