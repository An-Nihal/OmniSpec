using System.Globalization;

namespace OmniSpec.Core.Metrics;

/// <summary>
/// The state of a single metric. Every value shown in OmniSpec carries one of these states so the UI
/// never has to invent a value when the hardware or OS does not expose it.
/// </summary>
public enum MetricState
{
    /// <summary>The value was read successfully.</summary>
    Available,
    /// <summary>The hardware, driver or OS does not support this metric.</summary>
    Unsupported,
    /// <summary>The metric is supported in principle but no value could be read right now.</summary>
    Unavailable,
    /// <summary>Reading the metric requires elevated privileges or an optional driver.</summary>
    PermissionRequired,
    /// <summary>An error occurred while reading the metric.</summary>
    Error,
}

/// <summary>An immutable metric value with its state, data source and timestamp.</summary>
public sealed record Metric<T>
{
    public T? Value { get; init; }
    public MetricState State { get; init; }
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public string? Note { get; init; }

    public bool HasValue => State == MetricState.Available && Value is not null;

    public static Metric<T> Available(T value, string source, DateTimeOffset? timestamp = null, string? note = null) =>
        new() { Value = value, State = MetricState.Available, Source = source, Timestamp = timestamp ?? DateTimeOffset.Now, Note = note };

    public static Metric<T> Unsupported(string source, string? note = null) => WithState(MetricState.Unsupported, source, note);
    public static Metric<T> Unavailable(string source, string? note = null) => WithState(MetricState.Unavailable, source, note);
    public static Metric<T> PermissionRequired(string source, string? note = null) => WithState(MetricState.PermissionRequired, source, note);
    public static Metric<T> Error(string source, string? note = null) => WithState(MetricState.Error, source, note);

    public static Metric<T> WithState(MetricState state, string source, string? note = null) =>
        new() { State = state, Source = source, Timestamp = DateTimeOffset.Now, Note = note };

    public override string ToString() =>
        HasValue ? Convert.ToString(Value, CultureInfo.CurrentCulture) ?? string.Empty : MetricText.ForState(State);
}

public static class MetricText
{
    public static string ForState(MetricState state) => state switch
    {
        MetricState.Available => string.Empty,
        MetricState.Unsupported => "Not Supported",
        MetricState.Unavailable => "Unavailable",
        MetricState.PermissionRequired => "Permission Required",
        MetricState.Error => "Error",
        _ => "Unknown",
    };
}

public static class MetricExtensions
{
    /// <summary>Formats a numeric metric with a unit, or returns the state text when no value exists.</summary>
    public static string Format(this Metric<double> metric, string format, string unit = "")
    {
        if (!metric.HasValue) return MetricText.ForState(metric.State);
        var text = metric.Value.ToString(format, CultureInfo.CurrentCulture);
        return string.IsNullOrEmpty(unit) ? text : $"{text} {unit}";
    }

    public static double? ValueOrNull(this Metric<double> metric) => metric.HasValue ? metric.Value : null;
}

/// <summary>Well-known data source names, shown next to values so users can see where a reading came from.</summary>
public static class DataSources
{
    public const string LibreHardwareMonitor = "LibreHardwareMonitor";
    public const string Wmi = "WMI (CIMv2)";
    public const string WmiStorage = "Windows Storage Management API (MSFT_PhysicalDisk)";
    public const string ReliabilityCounters = "Windows Storage Reliability Counters";
    public const string Smart = "S.M.A.R.T. / NVMe health log (via LibreHardwareMonitor)";
    public const string PerformanceCounters = "Windows Performance Counters";
    public const string Win32 = "Win32 API";
    public const string DisplayConfig = "Windows Display Configuration API";
    public const string NetworkInterface = "Windows IP Helper (NetworkInterface)";
    public const string PowerStatus = "Win32 GetSystemPowerStatus";
    public const string BatteryWmi = "Battery WMI (root\\wmi)";
    public const string PresentMon = "Intel PresentMon";
    public const string Registry = "Windows Registry";
    public const string Calculated = "Calculated by OmniSpec";
    public const string Mock = "MOCK (development only)";
}
