using System.Management;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using OmniSpec.Core.Hardware;
using OmniSpec.Core.Metrics;
using OmniSpec.Core.Storage;
using OmniSpec.SystemInformation.Wmi;

namespace OmniSpec.SystemInformation;

/// <summary>
/// Collects physical drive data from the Windows Storage Management API (root\Microsoft\Windows\Storage):
/// MSFT_PhysicalDisk (identity, health status), MSFT_Disk/MSFT_Partition (layout) and
/// MSFT_StorageReliabilityCounter (temperature, wear, power-on hours, error counters — usually requires administrator).
/// SMART/NVMe health sensors are attached from the LibreHardwareMonitor snapshot when the drive can be matched unambiguously.
/// </summary>
public sealed class StorageInfoProvider : IStorageInfoProvider
{
    private readonly ILogger<StorageInfoProvider> _logger;
    private readonly bool _elevated;

    public StorageInfoProvider(ILogger<StorageInfoProvider> logger)
    {
        _logger = logger;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            _elevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { _elevated = false; }
    }

    public Task<IReadOnlyList<PhysicalDriveInfo>> GetDrivesAsync(HardwareSnapshot? hardware, CancellationToken cancellationToken) =>
        Task.Run(() => Collect(hardware, cancellationToken), cancellationToken);

    private IReadOnlyList<PhysicalDriveInfo> Collect(HardwareSnapshot? hardware, CancellationToken ct)
    {
        var result = new List<PhysicalDriveInfo>();
        var disks = WmiQuery.Query("SELECT Number, IsBoot, IsSystem, PartitionStyle FROM MSFT_Disk", WmiQuery.Storage).Rows
            .Where(r => r.U64("Number") is not null)
            .ToDictionary(r => r.U64("Number")!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), r => r);
        var partitions = WmiQuery.Query("SELECT DiskNumber, PartitionNumber, DriveLetter, Size, GptType, MbrType, IsSystem, IsBoot FROM MSFT_Partition", WmiQuery.Storage).Rows;

        var storageDevices = hardware?.OfKind(DeviceKind.Storage).ToList() ?? new List<HardwareDevice>();

        try
        {
            var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(20) };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(WmiQuery.Storage),
                new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"), options);
            using var physical = searcher.Get();
            foreach (ManagementObject pd in physical)
            {
                ct.ThrowIfCancellationRequested();
                using (pd)
                {
                    var row = WmiQuery.ToDictionary(pd);
                    var deviceId = row.Str("DeviceId") ?? "?";
                    disks.TryGetValue(deviceId, out var disk);
                    var model = row.Str("Model") ?? row.Str("FriendlyName") ?? "Unknown drive";
                    var (smartDevice, smartSensors) = MatchSmart(storageDevices, model, row.Str("FriendlyName"), deviceId);

                    result.Add(new PhysicalDriveInfo
                    {
                        DeviceId = deviceId,
                        Model = model,
                        Manufacturer = row.Str("Manufacturer"),
                        SerialNumber = row.Str("SerialNumber"),
                        FirmwareVersion = row.Str("FirmwareVersion"),
                        BusType = BusTypeName(row.U64("BusType")),
                        MediaType = MediaTypeName(row.U64("MediaType")),
                        SizeBytes = row.U64("Size") ?? 0,
                        WindowsHealthStatus = HealthName(row.U64("HealthStatus")),
                        OperationalStatus = OperationalStatusNames(row),
                        SpindleSpeedRpm = row.U64("SpindleSpeed") is ulong sp and > 0 and < uint.MaxValue ? (uint)sp : null,
                        IsBootDisk = disk?.Bool("IsBoot") == true,
                        IsSystemDisk = disk?.Bool("IsSystem") == true,
                        PartitionStyle = disk?.U64("PartitionStyle") switch { 1 => "MBR", 2 => "GPT", 0 => "RAW", _ => null },
                        Partitions = BuildPartitions(partitions, deviceId),
                        Reliability = ReadReliability(pd),
                        SmartSensors = smartSensors,
                        SmartSourceDevice = smartDevice,
                        CollectedAt = DateTimeOffset.Now,
                    });
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enumerating MSFT_PhysicalDisk failed");
        }

        return result.OrderBy(d => int.TryParse(d.DeviceId, out var n) ? n : int.MaxValue).ToList();
    }

    private Metric<ReliabilityCounters> ReadReliability(ManagementObject physicalDisk)
    {
        try
        {
            using var related = physicalDisk.GetRelated("MSFT_StorageReliabilityCounter");
            foreach (ManagementObject rc in related)
            {
                using (rc)
                {
                    var r = WmiQuery.ToDictionary(rc);
                    var counters = new ReliabilityCounters(
                        Temperature: Positive(r.Dbl("Temperature")),
                        TemperatureMax: Positive(r.Dbl("TemperatureMax")),
                        Wear: r.Dbl("Wear"),
                        PowerOnHours: r.Dbl("PowerOnHours"),
                        StartStopCycleCount: r.Dbl("StartStopCycleCount"),
                        LoadUnloadCycleCount: r.Dbl("LoadUnloadCycleCount"),
                        ReadErrorsTotal: r.Dbl("ReadErrorsTotal"),
                        ReadErrorsCorrected: r.Dbl("ReadErrorsCorrected"),
                        ReadErrorsUncorrected: r.Dbl("ReadErrorsUncorrected"),
                        WriteErrorsTotal: r.Dbl("WriteErrorsTotal"),
                        WriteErrorsCorrected: r.Dbl("WriteErrorsCorrected"),
                        WriteErrorsUncorrected: r.Dbl("WriteErrorsUncorrected"),
                        ReadLatencyMaxMs: r.Dbl("ReadLatencyMax"),
                        WriteLatencyMaxMs: r.Dbl("WriteLatencyMax"),
                        FlushLatencyMaxMs: r.Dbl("FlushLatencyMax"));
                    return Metric<ReliabilityCounters>.Available(counters, DataSources.ReliabilityCounters);
                }
            }
            return _elevated
                ? Metric<ReliabilityCounters>.Unsupported(DataSources.ReliabilityCounters, "This drive or its controller does not report reliability counters")
                : Metric<ReliabilityCounters>.PermissionRequired(DataSources.ReliabilityCounters, "Run OmniSpec as administrator to read reliability counters");
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return Metric<ReliabilityCounters>.PermissionRequired(DataSources.ReliabilityCounters, "Run OmniSpec as administrator to read reliability counters");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reliability counters unavailable");
            return _elevated
                ? Metric<ReliabilityCounters>.Error(DataSources.ReliabilityCounters, ex.Message.Trim())
                : Metric<ReliabilityCounters>.PermissionRequired(DataSources.ReliabilityCounters, "Run OmniSpec as administrator to read reliability counters");
        }
    }

    private static double? Positive(double? v) => v is > 0 ? v : null;

    private static IReadOnlyList<PartitionInfo> BuildPartitions(IReadOnlyList<IReadOnlyDictionary<string, object?>> partitions, string deviceId)
    {
        var list = new List<PartitionInfo>();
        foreach (var p in partitions.Where(p => p.U64("DiskNumber")?.ToString(System.Globalization.CultureInfo.InvariantCulture) == deviceId))
        {
            string? letter = null;
            if (p.TryGetValue("DriveLetter", out var dl) && dl is char ch && char.IsLetter(ch)) letter = $"{ch}:";
            else if (p.Str("DriveLetter") is { Length: 1 } s && char.IsLetter(s[0])) letter = $"{s}:";

            string? label = null, fs = null;
            ulong? free = null;
            var size = p.U64("Size") ?? 0;
            if (letter is not null)
            {
                try
                {
                    var di = new DriveInfo(letter);
                    if (di.IsReady)
                    {
                        label = di.VolumeLabel;
                        fs = di.DriveFormat;
                        free = (ulong)di.AvailableFreeSpace;
                        size = (ulong)di.TotalSize;
                    }
                }
                catch { /* volume not ready / locked (BitLocker) */ }
            }
            list.Add(new PartitionInfo((int)(p.U64("PartitionNumber") ?? 0), letter, label, fs, size, free, PartitionRole(p)));
        }
        return list.OrderBy(p => p.PartitionNumber).ToList();
    }

    private static string? PartitionRole(IReadOnlyDictionary<string, object?> p)
    {
        var gpt = p.Str("GptType")?.ToLowerInvariant();
        return gpt switch
        {
            "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}" => "EFI System",
            "{e3c9e316-0b5c-4db8-817d-f92df00215ae}" => "Microsoft Reserved",
            "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}" => "Basic data",
            "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}" => "Recovery",
            null => p.U64("MbrType") switch { 7 => "NTFS/exFAT (MBR)", 12 or 11 => "FAT32 (MBR)", 39 => "Recovery (MBR)", null => null, var t => $"MBR type {t}" },
            _ => "Other (GPT)",
        };
    }

    /// <summary>
    /// Matches a LibreHardwareMonitor storage device to a physical disk. Prefers matching both index and model;
    /// otherwise accepts a model match only when it is unique. Never guesses between identical drives.
    /// </summary>
    internal static (string? Device, IReadOnlyList<SensorReading> Sensors) MatchSmart(IReadOnlyList<HardwareDevice> storage, string model, string? friendlyName, string deviceId)
    {
        if (storage.Count == 0) return (null, Array.Empty<SensorReading>());
        var key = Normalize(model);
        var key2 = Normalize(friendlyName);
        bool NameMatches(HardwareDevice d)
        {
            var n = Normalize(d.Name);
            return n.Length > 0 && ((key.Length > 0 && (n.Contains(key) || key.Contains(n))) || (key2.Length > 0 && (n.Contains(key2) || key2.Contains(n))));
        }

        var candidates = storage.Where(NameMatches).ToList();
        var byIndex = candidates.FirstOrDefault(d => d.Id.TrimEnd('/').Split('/').LastOrDefault() == deviceId);
        var chosen = byIndex ?? (candidates.Count == 1 ? candidates[0] : null);
        return chosen is null ? (null, Array.Empty<SensorReading>()) : (chosen.Name, chosen.Sensors);
    }

    private static string Normalize(string? s) => s is null ? string.Empty : new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static string? BusTypeName(ulong? bus) => bus switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "File-backed virtual",
        16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS",
        _ => "Unknown",
    };

    public static string? MediaTypeName(ulong? media) => media switch
    {
        3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified",
    };

    public static string? HealthName(ulong? health) => health switch
    {
        0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => null,
    };

    private static IReadOnlyList<string> OperationalStatusNames(IReadOnlyDictionary<string, object?> row)
    {
        if (!row.TryGetValue("OperationalStatus", out var v) || v is not Array arr) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var o in arr)
        {
            var code = Convert.ToUInt32(o, System.Globalization.CultureInfo.InvariantCulture);
            list.Add(code switch
            {
                0 => "Unknown", 1 => "Other", 2 => "OK", 3 => "Degraded", 4 => "Stressed", 5 => "Predictive Failure",
                6 => "Error", 7 => "Non-Recoverable Error", 8 => "Starting", 9 => "Stopping", 10 => "Stopped",
                11 => "In Service", 12 => "No Contact", 13 => "Lost Communication", 14 => "Aborted", 15 => "Dormant",
                16 => "Supporting Entity in Error", 17 => "Completed", 18 => "Power Mode",
                _ => $"Status code 0x{code:X}",
            });
        }
        return list;
    }
}
