using System.Diagnostics;
using OmniSpec.Core.Live;
using OmniSpec.Core.Metrics;
using OmniSpec.Monitoring.Native;
using OmniSpec.SystemInformation;
using OmniSpec.SystemInformation.Wmi;

namespace OmniSpec.Monitoring.Providers;

/// <summary>
/// Battery information. Charge/AC state comes from GetSystemPowerStatus on every sample (cheap). Capacity, cycle count,
/// voltage and rate come from the battery WMI classes in root\wmi (BatteryStaticData, BatteryFullChargedCapacity,
/// BatteryStatus, BatteryCycleCount) and are refreshed on a slower interval.
/// Wear is an estimate: 1 − FullChargeCapacity / DesignCapacity, computed only when both values are reported.
/// </summary>
public sealed class BatteryProvider : IBatteryProvider
{
    private readonly Func<TimeSpan> _detailInterval;
    private long _lastDetail;
    private Detail? _detail;
    private string? _powerPlan;

    private sealed record Detail(
        Metric<double> Design, Metric<double> Full, Metric<double> Remaining, Metric<double> Cycles,
        Metric<double> Voltage, Metric<double> Rate, string? Name, string? Chemistry, bool? Charging);

    public BatteryProvider(Func<TimeSpan> detailInterval) => _detailInterval = detailInterval;

    public BatterySample Sample()
    {
        var now = DateTimeOffset.Now;
        if (!MonitoringNative.GetSystemPowerStatus(out var ps))
            return BatterySample.None(_powerPlan);

        // BatteryFlag 128 = no system battery, 255 = unknown status.
        var hasBattery = ps.BatteryFlag != 128 && ps.BatteryFlag != 255;
        if (_detail is null || Stopwatch.GetElapsedTime(_lastDetail) > _detailInterval())
        {
            _detail = ReadDetail(now);
            _lastDetail = Stopwatch.GetTimestamp();
            _powerPlan = PowerPlan.ActiveName();
        }
        if (!hasBattery) return BatterySample.None(_powerPlan);

        var d = _detail!;
        var onAc = ps.ACLineStatus switch { 1 => Metric<bool>.Available(true, DataSources.PowerStatus, now), 0 => Metric<bool>.Available(false, DataSources.PowerStatus, now), _ => Metric<bool>.Unavailable(DataSources.PowerStatus) };
        var charging = (ps.BatteryFlag & 8) != 0;
        var isCharging = Metric<bool>.Available(charging || d.Charging == true, DataSources.PowerStatus, now);
        var percent = ps.BatteryLifePercent <= 100
            ? Metric<double>.Available(ps.BatteryLifePercent, DataSources.PowerStatus, now)
            : Metric<double>.Unavailable(DataSources.PowerStatus);

        string status = charging ? "Charging"
            : ps.ACLineStatus == 1 && ps.BatteryLifePercent >= 95 ? "Fully charged / plugged in"
            : ps.ACLineStatus == 1 ? "Plugged in, not charging"
            : (ps.BatteryFlag & 4) != 0 ? "Discharging (critical)"
            : (ps.BatteryFlag & 2) != 0 ? "Discharging (low)"
            : "Discharging";

        var runtime = ps.BatteryLifeTime != uint.MaxValue && ps.ACLineStatus != 1
            ? Metric<TimeSpan>.Available(TimeSpan.FromSeconds(ps.BatteryLifeTime), DataSources.PowerStatus, now, "Windows estimate")
            : Metric<TimeSpan>.Unavailable(DataSources.PowerStatus, ps.ACLineStatus == 1 ? "Not estimated while on AC power" : "Windows has no estimate yet");

        var wear = d.Design.HasValue && d.Full.HasValue && d.Design.Value > 0
            ? Metric<double>.Available(Math.Clamp(100.0 * (1 - d.Full.Value / d.Design.Value), 0, 100), DataSources.Calculated, now,
                "Estimate: 1 − full-charge capacity ÷ design capacity")
            : Metric<double>.Unavailable(DataSources.Calculated, "Requires design and full-charge capacity");

        return new BatterySample(true, percent, Metric<string>.Available(status, DataSources.PowerStatus, now), isCharging, onAc,
            d.Design, d.Full, d.Remaining, wear, d.Cycles, d.Voltage, d.Rate, runtime, d.Name, d.Chemistry, _powerPlan);
    }

    private static Detail ReadDetail(DateTimeOffset now)
    {
        Metric<double> FromRow(WmiResult r, string column, double scale, string note, bool zeroIsMissing = true)
        {
            if (!r.Ok) return Metric<double>.WithState(r.State, DataSources.BatteryWmi, r.Error);
            var v = r.Rows.Select(x => x.Dbl(column)).FirstOrDefault(x => x.HasValue);
            if (v is null || (zeroIsMissing && v == 0) || v == uint.MaxValue) return Metric<double>.Unsupported(DataSources.BatteryWmi, "Not reported by this battery");
            return Metric<double>.Available(v.Value * scale, DataSources.BatteryWmi, now, note);
        }

        var stat = WmiQuery.Query("SELECT DesignedCapacity, ManufactureName, DeviceName, Chemistry FROM BatteryStaticData", WmiQuery.RootWmi);
        var full = WmiQuery.Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", WmiQuery.RootWmi);
        var status = WmiQuery.Query("SELECT RemainingCapacity, Voltage, ChargeRate, DischargeRate, Charging FROM BatteryStatus", WmiQuery.RootWmi);
        var cycles = WmiQuery.Query("SELECT CycleCount FROM BatteryCycleCount", WmiQuery.RootWmi);
        var win32 = WmiQuery.Query("SELECT Name, Chemistry FROM Win32_Battery");

        var s0 = status.Rows.FirstOrDefault();
        Metric<double> rate;
        if (!status.Ok) rate = Metric<double>.WithState(status.State, DataSources.BatteryWmi, status.Error);
        else
        {
            var charge = s0?.Dbl("ChargeRate") ?? 0;
            var discharge = s0?.Dbl("DischargeRate") ?? 0;
            rate = charge > 0 ? Metric<double>.Available(charge / 1000.0, DataSources.BatteryWmi, now, "Charging rate")
                : discharge > 0 ? Metric<double>.Available(-discharge / 1000.0, DataSources.BatteryWmi, now, "Discharge rate")
                : Metric<double>.Available(0, DataSources.BatteryWmi, now, "Idle");
        }

        return new Detail(
            FromRow(stat, "DesignedCapacity", 1, "Design capacity reported by battery firmware"),
            FromRow(full, "FullChargedCapacity", 1, "Full-charge capacity reported by battery firmware"),
            FromRow(status, "RemainingCapacity", 1, "Measured by battery firmware", zeroIsMissing: false),
            FromRow(cycles, "CycleCount", 1, "Reported by battery firmware"),
            FromRow(status, "Voltage", 0.001, "Measured by battery firmware"),
            rate,
            win32.Rows.FirstOrDefault()?.Str("Name") ?? stat.Rows.FirstOrDefault()?.Str("DeviceName"),
            SystemInfoCollector.BatteryChemistry(win32.Rows.FirstOrDefault()?.U64("Chemistry")),
            s0?.Bool("Charging"));
    }
}
