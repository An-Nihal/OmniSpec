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
    public bool ShowOverlay { get; set; } = true;
    /// <summary>TopLeft, TopRight, BottomLeft or BottomRight of the primary screen.</summary>
    public string OverlayCorner { get; set; } = "TopLeft";
    public double OverlayOpacity { get; set; } = 0.85;
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
