using OmniSpec.Core.Hardware;
using OmniSpec.Core.Metrics;

namespace OmniSpec.Core.Storage;

public sealed record PartitionInfo(
    int PartitionNumber,
    string? DriveLetter,
    string? Label,
    string? FileSystem,
    ulong SizeBytes,
    ulong? FreeBytes,
    string? Type);

/// <summary>Windows Storage Reliability Counters (MSFT_StorageReliabilityCounter). Every field is optional.</summary>
public sealed record ReliabilityCounters(
    double? Temperature,
    double? TemperatureMax,
    double? Wear,
    double? PowerOnHours,
    double? StartStopCycleCount,
    double? LoadUnloadCycleCount,
    double? ReadErrorsTotal,
    double? ReadErrorsCorrected,
    double? ReadErrorsUncorrected,
    double? WriteErrorsTotal,
    double? WriteErrorsCorrected,
    double? WriteErrorsUncorrected,
    double? ReadLatencyMaxMs,
    double? WriteLatencyMaxMs,
    double? FlushLatencyMaxMs);

/// <summary>Everything OmniSpec knows about one physical drive. Device-level SMART data comes from <see cref="SmartSensors"/>.</summary>
public sealed record PhysicalDriveInfo
{
    public required string DeviceId { get; init; }            // Windows disk number
    public required string Model { get; init; }
    public string? Manufacturer { get; init; }
    public string? SerialNumber { get; init; }                 // sensitive, hidden by default
    public string? FirmwareVersion { get; init; }
    public string? BusType { get; init; }
    public string? MediaType { get; init; }
    public ulong SizeBytes { get; init; }
    public string? WindowsHealthStatus { get; init; }         // Healthy | Warning | Unhealthy | Unknown
    public IReadOnlyList<string> OperationalStatus { get; init; } = Array.Empty<string>();
    public uint? SpindleSpeedRpm { get; init; }
    public bool IsBootDisk { get; init; }
    public bool IsSystemDisk { get; init; }
    public string? PartitionStyle { get; init; }
    public IReadOnlyList<PartitionInfo> Partitions { get; init; } = Array.Empty<PartitionInfo>();
    public Metric<ReliabilityCounters> Reliability { get; init; } = Metric<ReliabilityCounters>.Unavailable(DataSources.ReliabilityCounters);
    public IReadOnlyList<SensorReading> SmartSensors { get; init; } = Array.Empty<SensorReading>();
    public string? SmartSourceDevice { get; init; }
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.Now;

    public ulong? TotalVolumeBytes => Partitions.Any(p => p.FreeBytes.HasValue) ? (ulong)Partitions.Where(p => p.FreeBytes.HasValue).Sum(p => (decimal)p.SizeBytes) : null;
    public ulong? FreeVolumeBytes => Partitions.Any(p => p.FreeBytes.HasValue) ? (ulong)Partitions.Where(p => p.FreeBytes.HasValue).Sum(p => (decimal)p.FreeBytes!.Value) : null;

    public bool IsSolidState =>
        string.Equals(MediaType, "SSD", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(BusType, "NVMe", StringComparison.OrdinalIgnoreCase);
}

public enum DriveHealthLevel { Healthy, Good, Warning, Critical, Unknown }

public enum FindingSeverity { Info, Good, Warning, Critical }

/// <summary>A single fact contributing to the health verdict, always with its source.</summary>
public sealed record HealthFinding(FindingSeverity Severity, string Message, string Source);

public sealed record DriveHealthAssessment(
    DriveHealthLevel Level,
    IReadOnlyList<HealthFinding> Findings,
    Metric<string> OfficialStatus,
    Metric<double> DeviceReportedWear,
    Metric<double> EstimatedRemainingLife,
    Metric<double> Temperature,
    Metric<double> PowerOnHours,
    Metric<double> PowerCycles,
    Metric<double> HostReadsGb,
    Metric<double> HostWritesGb);

public interface IStorageInfoProvider
{
    /// <summary>Collects physical drive information. <paramref name="hardware"/> is used to attach SMART sensors.</summary>
    Task<IReadOnlyList<PhysicalDriveInfo>> GetDrivesAsync(HardwareSnapshot? hardware, CancellationToken cancellationToken);
}
