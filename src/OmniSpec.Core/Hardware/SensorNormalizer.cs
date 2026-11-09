namespace OmniSpec.Core.Hardware;

/// <summary>
/// Rejects physically implausible readings. Hardware libraries sometimes return 0 (or garbage) when a sensor could not
/// actually be read — e.g. CPU temperature/power without the low-level driver. Showing "0 °C" would be a fabricated value,
/// so such readings become null ("Unavailable") instead.
/// </summary>
public static class SensorNormalizer
{
    public static double? Normalize(DeviceKind device, SensorKind kind, double? value)
    {
        if (value is not double v || !double.IsFinite(v)) return null;
        return kind switch
        {
            // No running CPU/GPU/drive is at or below 0 °C; such values mean "not read". Above 150 °C is a bogus reading.
            SensorKind.Temperature => v <= 0 || v > 150 ? null : v,
            SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => v < 0 || v > 100.5 ? null : Math.Min(v, 100),
            SensorKind.Fan => v < 0 || v > 30_000 ? null : v,
            SensorKind.Voltage => v < 0 || v > 30 ? null : v,
            // A CPU package always draws power; exactly 0 W means the counter was not read.
            SensorKind.Power => v < 0 || v > 3000 || (device == DeviceKind.Cpu && v == 0) ? null : v,
            // CPU cores always run at a non-zero clock; 0 MHz means not read. GPUs can legitimately idle at 0 MHz.
            SensorKind.Clock => v < 0 || v > 20_000 || (device == DeviceKind.Cpu && v == 0) ? null : v,
            SensorKind.Factor => v < 0 ? null : v,
            SensorKind.Data or SensorKind.SmallData or SensorKind.Throughput or SensorKind.Energy => v < 0 ? null : v,
            _ => v,
        };
    }

    /// <summary>
    /// Sensor kinds on a CPU that are only meaningful with low-level (ring-0) access. Without it LHM returns placeholders.
    /// </summary>
    public static bool RequiresLowLevelAccess(DeviceKind device, SensorKind kind) =>
        device == DeviceKind.Cpu && kind is SensorKind.Temperature or SensorKind.Power or SensorKind.Clock or SensorKind.Voltage or SensorKind.Factor or SensorKind.Current;
}
