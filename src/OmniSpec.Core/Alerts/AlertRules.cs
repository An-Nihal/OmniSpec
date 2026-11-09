using OmniSpec.Core.Live;
using OmniSpec.Core.Settings;
using OmniSpec.Core.Storage;

namespace OmniSpec.Core.Alerts;

/// <summary>Builds alert conditions from live data and user thresholds. Missing metrics never trigger alerts.</summary>
public static class AlertRules
{
    public static IEnumerable<AlertCondition> FromLive(LiveSnapshot live, AlertSettings s)
    {
        if (live.Cpu.PackageTemperature.HasValue)
        {
            var t = live.Cpu.PackageTemperature.Value;
            yield return new AlertCondition("cpu.temp", t >= s.CpuTemperatureC, "CPU temperature high",
                $"CPU temperature is {t:0} °C (limit {s.CpuTemperatureC:0} °C).", AlertSeverity.Warning);
        }

        foreach (var gpu in live.Gpus)
        {
            if (!gpu.Temperature.HasValue) continue;
            var t = gpu.Temperature.Value;
            yield return new AlertCondition($"gpu.temp.{gpu.Id}", t >= s.GpuTemperatureC, "GPU temperature high",
                $"{gpu.Name} is at {t:0} °C (limit {s.GpuTemperatureC:0} °C).", AlertSeverity.Warning);
        }

        if (live.Memory is { } mem && mem.TotalPhysical > 0)
        {
            yield return new AlertCondition("ram.usage", mem.UsedPercent >= s.RamUsagePercent, "High memory usage",
                $"RAM usage is {mem.UsedPercent:0}% (limit {s.RamUsagePercent:0}%).", AlertSeverity.Warning);
        }

        if (live.Battery is { HasBattery: true } bat && bat.WearPercentEstimate.HasValue)
        {
            yield return new AlertCondition("battery.wear", bat.WearPercentEstimate.Value >= s.BatteryWearPercent, "Battery wear",
                $"Estimated battery wear is {bat.WearPercentEstimate.Value:0}% (limit {s.BatteryWearPercent:0}%).", AlertSeverity.Info);
        }
    }

    /// <summary>User-defined per-sensor limits (Sensors page). A sensor above its limit raises a warning.</summary>
    public static IEnumerable<AlertCondition> FromSensorLimits(Hardware.HardwareSnapshot hardware, IReadOnlyDictionary<string, double> limits)
    {
        if (limits.Count == 0) yield break;
        foreach (var device in hardware.Devices)
        {
            foreach (var sensor in device.Sensors)
            {
                if (!limits.TryGetValue(sensor.Id, out var limit) || sensor.Value is not double v) continue;
                yield return new AlertCondition($"sensor.{sensor.Id}", v >= limit, $"{sensor.Name} above limit",
                    $"{device.Name} › {sensor.Name} is {v:0.#} {sensor.Unit} (limit {limit:0.#} {sensor.Unit}).", AlertSeverity.Warning);
            }
        }
    }

    public static IEnumerable<AlertCondition> FromDrives(IEnumerable<(PhysicalDriveInfo Drive, DriveHealthAssessment Health)> drives, AlertSettings s)
    {
        foreach (var (drive, health) in drives)
        {
            if (s.DriveHealthAlerts)
            {
                var bad = health.Level is DriveHealthLevel.Warning or DriveHealthLevel.Critical;
                yield return new AlertCondition($"drive.health.{drive.DeviceId}", bad,
                    health.Level == DriveHealthLevel.Critical ? "Drive health critical" : "Drive health warning",
                    $"{drive.Model}: {DriveHealthEvaluator.Describe(health.Level)}. {health.Findings.FirstOrDefault(f => f.Severity >= FindingSeverity.Warning)?.Message}",
                    health.Level == DriveHealthLevel.Critical ? AlertSeverity.Critical : AlertSeverity.Warning);
            }

            if (health.Temperature.HasValue)
            {
                yield return new AlertCondition($"drive.temp.{drive.DeviceId}", health.Temperature.Value >= s.DriveTemperatureC, "Drive temperature high",
                    $"{drive.Model} is at {health.Temperature.Value:0} °C (limit {s.DriveTemperatureC:0} °C).", AlertSeverity.Warning);
            }

            foreach (var p in drive.Partitions.Where(p => p.FreeBytes.HasValue && p.SizeBytes > 0 && !string.IsNullOrEmpty(p.DriveLetter)))
            {
                var freePct = 100.0 * p.FreeBytes!.Value / p.SizeBytes;
                yield return new AlertCondition($"disk.space.{p.DriveLetter}", freePct < s.LowDiskFreePercent, "Low disk space",
                    $"Drive {p.DriveLetter}: has {freePct:0.#}% free space left.", AlertSeverity.Warning);
            }
        }
    }
}
