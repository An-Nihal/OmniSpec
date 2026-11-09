using OmniSpec.Core.Hardware;
using OmniSpec.Core.Metrics;

namespace OmniSpec.Core.Live;

/// <summary>CPU utilization sample from Windows performance counters (no elevation needed).</summary>
public sealed record CpuUsageSample(
    Metric<double> TotalLoad,
    IReadOnlyList<double?> PerLogicalLoad,
    Metric<double> EffectiveClockMHz,
    Metric<double> PerformanceLimitPercent,
    int ProcessCount,
    int ThreadCount,
    int HandleCount);

public interface ICpuUsageProvider : IDisposable
{
    CpuUsageSample Sample();
}

public sealed record MemorySample(
    ulong TotalPhysical,
    ulong AvailablePhysical,
    ulong CachedBytes,
    ulong CommittedBytes,
    ulong CommitLimitBytes,
    ulong TotalVirtual,
    ulong AvailableVirtual,
    ulong PagedPoolBytes,
    ulong NonPagedPoolBytes,
    uint MemoryLoadPercent,
    ulong? ModifiedBytes,
    ulong? StandbyBytes,
    ulong? FreeBytes,
    DateTimeOffset Timestamp)
{
    public ulong UsedPhysical => TotalPhysical - AvailablePhysical;
    public double UsedPercent => TotalPhysical == 0 ? 0 : 100.0 * UsedPhysical / TotalPhysical;
    public double CommitPercent => CommitLimitBytes == 0 ? 0 : 100.0 * CommittedBytes / CommitLimitBytes;

    /// <summary>
    /// A simple, documented pressure heuristic combining physical availability and commit charge.
    /// Low: &lt;70%, Moderate: 70–85%, High: 85–95%, Critical: ≥95% (max of the two ratios).
    /// </summary>
    public string Pressure
    {
        get
        {
            var p = Math.Max(UsedPercent, CommitPercent);
            return p >= 95 ? "Critical" : p >= 85 ? "High" : p >= 70 ? "Moderate" : "Low";
        }
    }
}

public interface IMemoryProvider
{
    MemorySample Sample();
}

public sealed record NetworkAdapterSample(
    string Id,
    string Name,
    string Description,
    string InterfaceType,
    string Status,
    long? LinkSpeedBitsPerSecond,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    long TotalBytesReceived,
    long TotalBytesSent,
    long SessionBytesReceived,
    long SessionBytesSent,
    string? MacAddress,
    IReadOnlyList<string> IpAddresses,
    DateTimeOffset? ObservedUpSince,
    bool IsVirtualOrLoopback)
{
    public double? UtilizationPercent =>
        LinkSpeedBitsPerSecond is > 0
            ? Math.Min(100, 100.0 * (DownloadBytesPerSecond + UploadBytesPerSecond) * 8 / LinkSpeedBitsPerSecond.Value)
            : null;
}

public interface INetworkProvider
{
    IReadOnlyList<NetworkAdapterSample> Sample();
}

public sealed record BatterySample(
    bool HasBattery,
    Metric<double> ChargePercent,
    Metric<string> Status,
    Metric<bool> IsCharging,
    Metric<bool> OnAcPower,
    Metric<double> DesignCapacityMWh,
    Metric<double> FullChargeCapacityMWh,
    Metric<double> RemainingCapacityMWh,
    Metric<double> WearPercentEstimate,
    Metric<double> CycleCount,
    Metric<double> VoltageV,
    Metric<double> ChargeRateW,
    Metric<TimeSpan> EstimatedRuntime,
    string? Name,
    string? Chemistry,
    string? PowerPlan)
{
    public static BatterySample None(string? powerPlan) => new(false,
        Metric<double>.Unsupported(DataSources.PowerStatus, "No battery detected"),
        Metric<string>.Unsupported(DataSources.PowerStatus), Metric<bool>.Unsupported(DataSources.PowerStatus),
        Metric<bool>.Unsupported(DataSources.PowerStatus), Metric<double>.Unsupported(DataSources.BatteryWmi),
        Metric<double>.Unsupported(DataSources.BatteryWmi), Metric<double>.Unsupported(DataSources.BatteryWmi),
        Metric<double>.Unsupported(DataSources.Calculated), Metric<double>.Unsupported(DataSources.BatteryWmi),
        Metric<double>.Unsupported(DataSources.BatteryWmi), Metric<double>.Unsupported(DataSources.BatteryWmi),
        Metric<TimeSpan>.Unsupported(DataSources.PowerStatus), null, null, powerPlan);
}

public interface IBatteryProvider
{
    BatterySample Sample();
}

public sealed record ProcessSample(
    int Pid,
    string Name,
    double? CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    double MemoryPercent,
    double? DiskReadBytesPerSec,
    double? DiskWriteBytesPerSec,
    int ThreadCount,
    string? Path,
    DateTime? StartTime,
    bool AccessLimited);

public interface IProcessProvider
{
    IReadOnlyList<ProcessSample> Sample();
}

/// <summary>Typed CPU sensor values extracted from a hardware snapshot.</summary>
public sealed record CpuSensors(
    string? Name,
    Metric<double> PackageTemperature,
    IReadOnlyList<SensorReading> CoreTemperatures,
    IReadOnlyList<SensorReading> CoreClocks,
    Metric<double> AverageCoreClock,
    Metric<double> PackagePower,
    Metric<double> CoreVoltage,
    Metric<string> ThermalThrottling,
    Metric<double> FanRpm);

/// <summary>Typed GPU sensor values extracted from a hardware snapshot.</summary>
public sealed record GpuSensors(
    string Id,
    string Name,
    string Vendor,
    bool IsLikelyIntegrated,
    Metric<double> Load,
    Metric<double> Temperature,
    Metric<double> HotSpot,
    Metric<double> MemoryJunction,
    Metric<double> CoreClock,
    Metric<double> MemoryClock,
    Metric<double> Voltage,
    Metric<double> Power,
    Metric<double> FanRpm,
    Metric<double> FanPercent,
    Metric<double> DedicatedUsedMB,
    Metric<double> DedicatedTotalMB,
    Metric<double> SharedUsedMB,
    Metric<double> SharedTotalMB,
    Metric<double> MemoryControllerLoad,
    Metric<double> VideoEngineLoad,
    Metric<double> VideoDecodeLoad,
    Metric<double> VideoEncodeLoad,
    Metric<double> BusLoad);

public sealed record FanReading(string Device, string Name, double? Rpm, double? Percent, bool IsPump);

/// <summary>One coherent live snapshot pushed to the UI and history on every sensor tick.</summary>
public sealed record LiveSnapshot(
    DateTimeOffset Timestamp,
    HardwareSnapshot Hardware,
    CpuUsageSample? CpuUsage,
    CpuSensors Cpu,
    IReadOnlyList<GpuSensors> Gpus,
    MemorySample? Memory,
    IReadOnlyList<NetworkAdapterSample> Network,
    BatterySample? Battery,
    IReadOnlyList<FanReading> Fans,
    bool IsMock);
