using OmniSpec.Core.Metrics;

namespace OmniSpec.Core.SystemInfo;

/// <summary>A labelled piece of static system information.</summary>
public sealed record InfoItem(string Label, string? Value, MetricState State, string Source, bool IsSensitive = false)
{
    public static InfoItem Of(string label, string? value, string source, bool sensitive = false) =>
        string.IsNullOrWhiteSpace(value)
            ? new InfoItem(label, null, MetricState.Unavailable, source, sensitive)
            : new InfoItem(label, value.Trim(), MetricState.Available, source, sensitive);

    public static InfoItem Missing(string label, MetricState state, string source, string? note = null) =>
        new(label, note, state, source);

    public string DisplayValue(bool revealSensitive) =>
        State != MetricState.Available
            ? (Value is null ? MetricText.ForState(State) : $"{MetricText.ForState(State)} — {Value}")
            : IsSensitive && !revealSensitive ? "Hidden for privacy" : Value ?? string.Empty;
}

/// <summary>A group of information items, optionally with sub-groups (e.g. one per memory module).</summary>
public sealed record InfoSection(string Title, IReadOnlyList<InfoItem> Items, IReadOnlyList<InfoSection>? Children = null)
{
    public static InfoSection Error(string title, string source, Exception ex) =>
        new(title, new[] { InfoItem.Missing("Status", MetricState.Error, source, ex.Message) });
}

public sealed record CpuStaticInfo(
    string Name,
    string Manufacturer,
    int? PhysicalCores,
    int LogicalProcessors,
    double? BaseClockMHz,
    IReadOnlyList<int> EfficiencyClassPerLogicalProcessor);

public sealed record GpuStaticInfo(string Name, string? DriverVersion, string? DriverDate, ulong? AdapterRamBytes, string? VideoProcessor);

public sealed record DisplayInfo(
    string MonitorName,
    string GdiDeviceName,
    int? Width,
    int? Height,
    double? RefreshRateHz,
    bool? HdrSupported,
    bool? HdrEnabled,
    int? BitsPerColor,
    string? Connection);

/// <summary>Full static system report collected at startup and on manual refresh.</summary>
public sealed record SystemInfoReport(
    DateTimeOffset CollectedAt,
    IReadOnlyList<InfoSection> Sections,
    CpuStaticInfo? Cpu,
    IReadOnlyList<GpuStaticInfo> Gpus,
    IReadOnlyList<DisplayInfo> Displays,
    string OsName,
    string ComputerName,
    ulong? InstalledMemoryBytes)
{
    public static SystemInfoReport Empty { get; } =
        new(DateTimeOffset.MinValue, Array.Empty<InfoSection>(), null, Array.Empty<GpuStaticInfo>(), Array.Empty<DisplayInfo>(), "", Environment.MachineName, null);
}

public interface ISystemInfoProvider
{
    Task<SystemInfoReport> CollectAsync(CancellationToken cancellationToken);
}
