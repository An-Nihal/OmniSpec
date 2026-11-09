using OmniSpec.Core.Metrics;

namespace OmniSpec.Core.Hardware;

public enum DeviceKind
{
    Cpu, Gpu, Motherboard, SuperIO, Memory, Storage, Network, Cooler, EmbeddedController, Psu, Battery, PowerMonitor, Other,
}

public enum SensorKind
{
    Voltage, Current, Power, Clock, Temperature, Load, Frequency, Fan, Flow, Control, Level, Factor, Data, SmallData,
    Throughput, TimeSpan, Timing, Energy, Noise, Conductivity, Humidity, Other,
}

/// <summary>A single sensor reading. <see cref="Value"/> is null when the sensor exists but returned no value.</summary>
public sealed record SensorReading(string Id, string Name, SensorKind Kind, double? Value, double? Min, double? Max)
{
    public string Unit => SensorUnits.For(Kind);
}

public sealed record HardwareDevice(string Id, string Name, DeviceKind Kind, string? ParentId, IReadOnlyList<SensorReading> Sensors);

public sealed record HardwareSnapshot(DateTimeOffset Timestamp, IReadOnlyList<HardwareDevice> Devices, string Source)
{
    public static HardwareSnapshot Empty(string source) => new(DateTimeOffset.Now, Array.Empty<HardwareDevice>(), source);
    public IEnumerable<HardwareDevice> OfKind(DeviceKind kind) => Devices.Where(d => d.Kind == kind);
}

public static class SensorUnits
{
    public static string For(SensorKind kind) => kind switch
    {
        SensorKind.Voltage => "V",
        SensorKind.Current => "A",
        SensorKind.Power => "W",
        SensorKind.Clock => "MHz",
        SensorKind.Temperature => "°C",
        SensorKind.Load => "%",
        SensorKind.Frequency => "Hz",
        SensorKind.Fan => "RPM",
        SensorKind.Flow => "L/h",
        SensorKind.Control => "%",
        SensorKind.Level => "%",
        SensorKind.Data => "GB",
        SensorKind.SmallData => "MB",
        SensorKind.Throughput => "B/s",
        SensorKind.TimeSpan => "s",
        SensorKind.Timing => "ns",
        SensorKind.Energy => "mWh",
        SensorKind.Noise => "dBA",
        SensorKind.Conductivity => "µS/cm",
        SensorKind.Humidity => "%",
        _ => "",
    };
}

/// <summary>Status of a provider after initialization.</summary>
public sealed record ProviderStatus(MetricState State, string Message)
{
    public static ProviderStatus NotInitialized { get; } = new(MetricState.Unavailable, "Not initialized");
    public static ProviderStatus Ok(string message = "Running") => new(MetricState.Available, message);
}

public sealed record HardwareProviderOptions
{
    public bool Cpu { get; init; } = true;
    public bool Gpu { get; init; } = true;
    public bool Motherboard { get; init; } = true;
    public bool Memory { get; init; } = true;
    public bool Storage { get; init; } = true;
    public bool Controllers { get; init; } = true;
    public bool Battery { get; init; } = true;
    public bool Psu { get; init; }
}

/// <summary>Information about privileges/drivers that affect which sensors can be read.</summary>
public sealed record PrivilegeInfo(bool IsElevated, bool? LowLevelDriverInstalled, string LowLevelDriverName);

/// <summary>
/// A provider of live hardware sensors. Implementations must never return fabricated values:
/// when a value cannot be read, the sensor's value must be null or the sensor omitted.
/// </summary>
public interface IHardwareSensorProvider : IDisposable
{
    string Name { get; }
    bool IsMock { get; }
    ProviderStatus Status { get; }
    PrivilegeInfo Privileges { get; }

    /// <summary>True when ring-0 sensors (CPU temperature/power, Super I/O) can actually be read.</summary>
    bool LowLevelAccessAvailable { get; }

    /// <summary>Opens the underlying hardware library. Called once.</summary>
    void Initialize(HardwareProviderOptions options);

    /// <summary>Refreshes sensors and returns a snapshot. Called from a background thread, never concurrently.</summary>
    HardwareSnapshot Poll();
}

/// <summary>
/// Helpers for locating well-known sensors by name. Hardware libraries name sensors differently per vendor,
/// so callers pass candidate names in priority order. Exact (case-insensitive) matches win over "contains" matches.
/// </summary>
public static class SensorQuery
{
    public static SensorReading? Find(HardwareDevice? device, SensorKind kind, params string[] candidates) =>
        device is null ? null : Find(device.Sensors, kind, candidates);

    public static SensorReading? Find(IEnumerable<SensorReading> sensors, SensorKind kind, params string[] candidates)
    {
        var ofKind = sensors.Where(s => s.Kind == kind && s.Value.HasValue && double.IsFinite(s.Value.Value)).ToList();
        if (ofKind.Count == 0) return null;
        if (candidates.Length == 0) return ofKind[0];

        foreach (var c in candidates)
        {
            var exact = ofKind.FirstOrDefault(s => string.Equals(s.Name, c, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        foreach (var c in candidates)
        {
            var partial = ofKind.FirstOrDefault(s => s.Name.Contains(c, StringComparison.OrdinalIgnoreCase));
            if (partial is not null) return partial;
        }
        return null;
    }

    public static IReadOnlyList<SensorReading> All(HardwareDevice? device, SensorKind kind, Func<SensorReading, bool>? predicate = null) =>
        device is null
            ? Array.Empty<SensorReading>()
            : device.Sensors.Where(s => s.Kind == kind && (predicate?.Invoke(s) ?? true)).ToList();

    /// <summary>Extracts a trailing "#n" index from names such as "CPU Core #3". Returns null if none.</summary>
    public static int? TrailingIndex(string name)
    {
        var hash = name.LastIndexOf('#');
        if (hash < 0) return null;
        var digits = new string(name.Skip(hash + 1).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }

    /// <summary>Converts a sensor to a metric, or returns a metric with the given fallback state.</summary>
    public static Metric<double> ToMetric(SensorReading? sensor, DateTimeOffset timestamp, string source,
        MetricState missingState = MetricState.Unavailable, string? missingNote = null) =>
        sensor?.Value is double v && double.IsFinite(v)
            ? Metric<double>.Available(v, source, timestamp, sensor.Name)
            : Metric<double>.WithState(missingState, source, missingNote);
}
