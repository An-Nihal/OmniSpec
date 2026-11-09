using OmniSpec.Core.Hardware;
using OmniSpec.Core.Metrics;

namespace OmniSpec.Core.Storage;

/// <summary>
/// Turns raw drive data into a health verdict. The rules are deliberately conservative:
/// <list type="bullet">
/// <item>Only standardized indicators are interpreted: Windows HealthStatus, the Windows reliability "Wear"
/// counter, NVMe "Percentage Used"/"Available Spare"/"Available Spare Threshold", uncorrected error counters,
/// and "Remaining Life" as already normalized by LibreHardwareMonitor's verified vendor mappings.</item>
/// <item>Vendor-specific raw SMART attributes are never interpreted.</item>
/// <item>No health percentage is ever fabricated. When nothing is known the level is <see cref="DriveHealthLevel.Unknown"/>.</item>
/// <item>The Windows "Wear" counter and NVMe "Percentage Used" express consumed endurance: 100 means the
/// rated endurance has been reached, NOT 100% remaining life.</item>
/// </list>
/// </summary>
public static class DriveHealthEvaluator
{
    public const double WearWarningPercent = 90;
    public const double WearGoodPercent = 50;

    public static DriveHealthAssessment Evaluate(PhysicalDriveInfo drive)
    {
        var findings = new List<HealthFinding>();
        var now = drive.CollectedAt;
        var smart = drive.SmartSensors;
        var rel = drive.Reliability.HasValue ? drive.Reliability.Value : null;

        // ---- Official Windows status ----
        Metric<string> official = string.IsNullOrWhiteSpace(drive.WindowsHealthStatus)
            ? Metric<string>.Unavailable(DataSources.WmiStorage)
            : Metric<string>.Available(drive.WindowsHealthStatus!, DataSources.WmiStorage, now);

        var level = DriveHealthLevel.Unknown;
        void Raise(DriveHealthLevel candidate)
        {
            if (level == DriveHealthLevel.Unknown || Rank(candidate) > Rank(level)) level = candidate;
        }

        switch (drive.WindowsHealthStatus?.Trim().ToLowerInvariant())
        {
            case "healthy":
                findings.Add(new(FindingSeverity.Good, "Windows reports the drive as Healthy.", DataSources.WmiStorage));
                Raise(DriveHealthLevel.Healthy);
                break;
            case "warning":
                findings.Add(new(FindingSeverity.Warning, "Windows reports a Warning health status for this drive.", DataSources.WmiStorage));
                Raise(DriveHealthLevel.Warning);
                break;
            case "unhealthy":
                findings.Add(new(FindingSeverity.Critical, "Windows reports the drive as Unhealthy. Back up your data immediately.", DataSources.WmiStorage));
                Raise(DriveHealthLevel.Critical);
                break;
        }

        foreach (var status in drive.OperationalStatus)
        {
            var s = status.ToLowerInvariant();
            if (s.Contains("predictive failure") || s.Contains("failed media") || s.Contains("lost communication"))
            {
                findings.Add(new(FindingSeverity.Critical, $"Windows operational status: {status}.", DataSources.WmiStorage));
                Raise(DriveHealthLevel.Critical);
            }
            else if (s.Contains("degraded") || s.Contains("stressed") || s.Contains("transient error") || s.Contains("abnormal latency") || s.Contains("io error"))
            {
                findings.Add(new(FindingSeverity.Warning, $"Windows operational status: {status}.", DataSources.WmiStorage));
                Raise(DriveHealthLevel.Warning);
            }
        }

        // ---- Device-reported wear (consumed endurance) ----
        Metric<double> wear = Metric<double>.Unavailable(DataSources.Calculated, "Drive does not report a standardized wear indicator.");
        var pctUsed = FindSmart(smart, "Percentage Used");
        if (pctUsed?.Value is double used && used >= 0)
        {
            wear = Metric<double>.Available(used, DataSources.Smart, now, "NVMe Percentage Used (endurance consumed)");
        }
        else if (rel?.Wear is double w && w >= 0 && drive.IsSolidState)
        {
            // Windows only populates Wear meaningfully for SSDs. A zero on HDDs is not a wear statistic.
            wear = Metric<double>.Available(w, DataSources.ReliabilityCounters, now, "Windows reliability Wear counter (endurance consumed)");
        }

        if (wear.HasValue)
        {
            var v = wear.Value;
            if (v >= 100)
            {
                findings.Add(new(FindingSeverity.Warning, $"Rated endurance reached ({v:0}% used). The drive may keep working, but keep backups current.", wear.Source));
                Raise(DriveHealthLevel.Warning);
            }
            else if (v >= WearWarningPercent)
            {
                findings.Add(new(FindingSeverity.Warning, $"{v:0}% of rated endurance consumed.", wear.Source));
                Raise(DriveHealthLevel.Warning);
            }
            else if (v >= WearGoodPercent)
            {
                findings.Add(new(FindingSeverity.Info, $"{v:0}% of rated endurance consumed.", wear.Source));
                Raise(DriveHealthLevel.Good);
            }
            else
            {
                findings.Add(new(FindingSeverity.Good, $"{v:0}% of rated endurance consumed.", wear.Source));
                Raise(DriveHealthLevel.Healthy);
            }
        }

        // ---- Estimated remaining life ----
        Metric<double> remaining = Metric<double>.Unavailable(DataSources.Calculated, "No reliable endurance data reported by this drive.");
        if (wear.HasValue)
        {
            remaining = Metric<double>.Available(Math.Max(0, 100 - wear.Value), DataSources.Calculated, now,
                $"Estimate: 100 − endurance consumed ({wear.Source}). Not a guarantee of remaining lifetime.");
        }
        else if (FindSmart(smart, "Remaining Life")?.Value is double life && life is >= 0 and <= 100)
        {
            remaining = Metric<double>.Available(life, DataSources.Smart, now,
                "Device-reported remaining life, normalized by LibreHardwareMonitor using a verified vendor attribute mapping.");
            if (life <= 10)
            {
                findings.Add(new(FindingSeverity.Warning, $"Device reports {life:0}% remaining life.", DataSources.Smart));
                Raise(DriveHealthLevel.Warning);
            }
            else
            {
                Raise(life < 50 ? DriveHealthLevel.Good : DriveHealthLevel.Healthy);
            }
        }

        // ---- NVMe spare capacity ----
        var spare = FindSmart(smart, "Available Spare")?.Value;
        var spareThreshold = FindSmart(smart, "Available Spare Threshold")?.Value;
        if (spare is double sp && spareThreshold is double th && th > 0)
        {
            if (sp < th)
            {
                findings.Add(new(FindingSeverity.Critical, $"Available spare ({sp:0}%) is below the drive's threshold ({th:0}%).", DataSources.Smart));
                Raise(DriveHealthLevel.Critical);
            }
            else
            {
                findings.Add(new(FindingSeverity.Good, $"Available spare {sp:0}% (threshold {th:0}%).", DataSources.Smart));
            }
        }

        // ---- NVMe critical warning (when exposed) ----
        if (FindSmart(smart, "Critical Warning")?.Value is double cw && cw > 0)
        {
            findings.Add(new(FindingSeverity.Critical, "The drive reports an NVMe critical warning.", DataSources.Smart));
            Raise(DriveHealthLevel.Critical);
        }

        // ---- Uncorrected errors ----
        var uncorrected = (rel?.ReadErrorsUncorrected ?? 0) + (rel?.WriteErrorsUncorrected ?? 0);
        if (uncorrected > 0)
        {
            findings.Add(new(FindingSeverity.Warning, $"{uncorrected:0} uncorrected read/write error(s) recorded.", DataSources.ReliabilityCounters));
            Raise(DriveHealthLevel.Warning);
        }
        if (FindSmart(smart, "Media Errors", "Media and Data Integrity Errors")?.Value is double media && media > 0)
        {
            findings.Add(new(FindingSeverity.Warning, $"{media:0} media/data integrity error(s) reported by the drive.", DataSources.Smart));
            Raise(DriveHealthLevel.Warning);
        }

        // ---- Informational metrics ----
        var temperature = FirstAvailable(now,
            (FindSmart(smart, "Temperature", "Composite Temperature")?.Value, DataSources.Smart),
            (rel?.Temperature is > 0 ? rel.Temperature : null, DataSources.ReliabilityCounters));
        var poh = FirstAvailable(now,
            (FindSmart(smart, "Power On Hours", "Power-On Hours")?.Value, DataSources.Smart),
            (rel?.PowerOnHours, DataSources.ReliabilityCounters));
        var cycles = FirstAvailable(now,
            (FindSmart(smart, "Power Cycles", "Power Cycle Count")?.Value, DataSources.Smart),
            (rel?.StartStopCycleCount, DataSources.ReliabilityCounters));
        var reads = FirstAvailable(now, (FindSmart(smart, "Data Read", "Host Reads", "Total Bytes Read")?.Value, DataSources.Smart));
        var writes = FirstAvailable(now, (FindSmart(smart, "Data Written", "Host Writes", "Total Bytes Written")?.Value, DataSources.Smart));

        if (level == DriveHealthLevel.Unknown)
        {
            findings.Add(new(FindingSeverity.Info,
                "This drive does not report health indicators to Windows (common for USB enclosures and some controllers).",
                DataSources.Calculated));
        }

        return new DriveHealthAssessment(level, findings, official, wear, remaining, temperature, poh, cycles, reads, writes);
    }

    private static int Rank(DriveHealthLevel level) => level switch
    {
        DriveHealthLevel.Healthy => 1,
        DriveHealthLevel.Good => 2,
        DriveHealthLevel.Warning => 3,
        DriveHealthLevel.Critical => 4,
        _ => 0,
    };

    /// <summary>Finds a SMART sensor by exact name (case-insensitive) regardless of sensor kind.</summary>
    public static SensorReading? FindSmart(IEnumerable<SensorReading> sensors, params string[] names)
    {
        foreach (var n in names)
        {
            var hit = sensors.FirstOrDefault(s => s.Value.HasValue && double.IsFinite(s.Value.Value) && string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    private static Metric<double> FirstAvailable(DateTimeOffset now, params (double? value, string source)[] candidates)
    {
        foreach (var (value, source) in candidates)
        {
            if (value is double v && double.IsFinite(v) && v >= 0) return Metric<double>.Available(v, source, now);
        }
        return Metric<double>.Unavailable(candidates.Length > 0 ? candidates[0].source : DataSources.Calculated, "Not reported by this drive");
    }

    public static string Describe(DriveHealthLevel level) => level switch
    {
        DriveHealthLevel.Healthy => "Healthy",
        DriveHealthLevel.Good => "Good",
        DriveHealthLevel.Warning => "Warning",
        DriveHealthLevel.Critical => "Critical",
        _ => "Unknown",
    };
}
