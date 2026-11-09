using OmniSpec.Core.Alerts;
using OmniSpec.Core.Hardware;
using OmniSpec.Core.History;
using OmniSpec.Core.Live;
using OmniSpec.Core.Metrics;
using OmniSpec.Core.Settings;

namespace OmniSpec.Tests;

public class MetricTests
{
    [Fact]
    public void Available_metric_has_value_and_source()
    {
        var m = Metric<double>.Available(42.5, "test");
        Assert.True(m.HasValue);
        Assert.Equal("42.5 °C", m.Format("0.0", "°C"));
        Assert.Equal("test", m.Source);
    }

    [Theory]
    [InlineData(MetricState.Unsupported, "Not Supported")]
    [InlineData(MetricState.Unavailable, "Unavailable")]
    [InlineData(MetricState.PermissionRequired, "Permission Required")]
    [InlineData(MetricState.Error, "Error")]
    public void Missing_metric_never_formats_as_a_number(MetricState state, string expected)
    {
        var m = Metric<double>.WithState(state, "test");
        Assert.False(m.HasValue);
        Assert.Equal(expected, m.Format("0", "%"));
        Assert.Null(m.ValueOrNull());
    }
}

public class SensorNormalizerTests
{
    [Theory]
    [InlineData(DeviceKind.Cpu, SensorKind.Temperature, 0.0, null)]       // placeholder without driver
    [InlineData(DeviceKind.Cpu, SensorKind.Temperature, 55.0, 55.0)]
    [InlineData(DeviceKind.Gpu, SensorKind.Temperature, 200.0, null)]     // bogus
    [InlineData(DeviceKind.Cpu, SensorKind.Power, 0.0, null)]             // CPU package never draws 0 W
    [InlineData(DeviceKind.Gpu, SensorKind.Power, 0.0, 0.0)]
    [InlineData(DeviceKind.Cpu, SensorKind.Clock, 0.0, null)]
    [InlineData(DeviceKind.Gpu, SensorKind.Clock, 0.0, 0.0)]              // GPUs can idle at 0 MHz
    [InlineData(DeviceKind.Gpu, SensorKind.Load, -1.0, null)]
    [InlineData(DeviceKind.Gpu, SensorKind.Load, 100.3, 100.0)]
    [InlineData(DeviceKind.Gpu, SensorKind.Factor, -1.0, null)]           // e.g. "Fullscreen FPS" = -1
    [InlineData(DeviceKind.Gpu, SensorKind.Fan, 0.0, 0.0)]                // zero-RPM fan mode is real
    public void Normalize_rejects_implausible_values(DeviceKind device, SensorKind kind, double input, double? expected) =>
        Assert.Equal(expected, SensorNormalizer.Normalize(device, kind, input));

    [Fact]
    public void Normalize_handles_null_and_nan()
    {
        Assert.Null(SensorNormalizer.Normalize(DeviceKind.Cpu, SensorKind.Load, null));
        Assert.Null(SensorNormalizer.Normalize(DeviceKind.Cpu, SensorKind.Load, double.NaN));
        Assert.Null(SensorNormalizer.Normalize(DeviceKind.Cpu, SensorKind.Load, double.PositiveInfinity));
    }

    [Fact]
    public void Cpu_thermal_sensors_require_low_level_access()
    {
        Assert.True(SensorNormalizer.RequiresLowLevelAccess(DeviceKind.Cpu, SensorKind.Temperature));
        Assert.False(SensorNormalizer.RequiresLowLevelAccess(DeviceKind.Cpu, SensorKind.Load));
        Assert.False(SensorNormalizer.RequiresLowLevelAccess(DeviceKind.Gpu, SensorKind.Temperature));
    }
}

public class SensorMappingTests
{
    private static HardwareSnapshot Snapshot(params HardwareDevice[] devices) => new(DateTimeOffset.Now, devices, "test");
    private static SensorReading S(string name, SensorKind kind, double? value) => new($"/x/{name}/{kind}", name, kind, value, value, value);

    [Fact]
    public void Exact_name_match_wins_over_partial()
    {
        var sensors = new[] { S("GPU VR SoC", SensorKind.Temperature, 46), S("GPU Core", SensorKind.Temperature, 40) };
        Assert.Equal(40, SensorQuery.Find(sensors, SensorKind.Temperature, "GPU Core")!.Value);
    }

    [Fact]
    public void Missing_cpu_temperature_without_driver_is_permission_required_not_zero()
    {
        var cpu = new HardwareDevice("/cpu", "CPU", DeviceKind.Cpu, null, [S("CPU Total", SensorKind.Load, 12)]);
        var mapped = SensorMapping.MapCpu(Snapshot(cpu), lowLevelAccess: false);
        Assert.Equal(MetricState.PermissionRequired, mapped.PackageTemperature.State);
        Assert.False(mapped.PackageTemperature.HasValue);
        Assert.Equal(MetricState.PermissionRequired, mapped.PackagePower.State);
    }

    [Fact]
    public void Amd_cpu_temperature_and_clocks_are_mapped_without_mixing_effective_clocks()
    {
        var cpu = new HardwareDevice("/amdcpu/0", "AMD Ryzen", DeviceKind.Cpu, null,
        [
            S("Core (Tctl/Tdie)", SensorKind.Temperature, 61),
            S("Core #1", SensorKind.Clock, 5000),
            S("Core #2", SensorKind.Clock, 4800),
            S("Core #1 (Effective)", SensorKind.Clock, 300),
            S("Package", SensorKind.Power, 45),
        ]);
        var mapped = SensorMapping.MapCpu(Snapshot(cpu), lowLevelAccess: true);
        Assert.Equal(61, mapped.PackageTemperature.Value);
        Assert.Equal(4900, mapped.AverageCoreClock.Value);
        Assert.Equal(45, mapped.PackagePower.Value);
    }

    [Fact]
    public void Performance_limit_is_used_for_throttle_status_when_tjmax_missing()
    {
        var cpu = new HardwareDevice("/cpu", "CPU", DeviceKind.Cpu, null, []);
        var limited = SensorMapping.MapCpu(Snapshot(cpu), true, Metric<double>.Available(72, "pc"));
        Assert.StartsWith("Limited to 72%", limited.ThermalThrottling.Value);
        var notLimited = SensorMapping.MapCpu(Snapshot(cpu), true, Metric<double>.Available(100, "pc"));
        Assert.Equal("Not limited", notLimited.ThermalThrottling.Value);
    }

    [Fact]
    public void Gpu_mapping_reports_unsupported_for_missing_sensors_and_orders_dedicated_first()
    {
        var igpu = new HardwareDevice("/gpu-amd/0", "AMD Radeon(TM) Graphics", DeviceKind.Gpu, null,
            [S("GPU VR SoC", SensorKind.Temperature, 46), S("GPU Core", SensorKind.Load, 1)]);
        var dgpu = new HardwareDevice("/gpu-amd/5", "AMD Radeon RX 9070 XT", DeviceKind.Gpu, null,
            [S("GPU Core", SensorKind.Temperature, 44), S("GPU Hot Spot", SensorKind.Temperature, 50), S("GPU Memory Used", SensorKind.SmallData, 2800), S("GPU Memory Total", SensorKind.SmallData, 16304)]);
        var gpus = SensorMapping.MapGpus(Snapshot(igpu, dgpu));

        Assert.Equal("AMD Radeon RX 9070 XT", gpus[0].Name);
        Assert.Equal(44, gpus[0].Temperature.Value);
        Assert.Equal(50, gpus[0].HotSpot.Value);
        Assert.True(gpus[1].IsLikelyIntegrated);
        // The iGPU's VR SoC temperature must not be presented as its GPU temperature.
        Assert.Equal(MetricState.Unsupported, gpus[1].Temperature.State);
        Assert.Equal(MetricState.Unsupported, gpus[1].VideoEncodeLoad.State);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4080", "NVIDIA")]
    [InlineData("AMD Radeon RX 7900 XTX", "AMD")]
    [InlineData("Intel(R) Arc(TM) A770", "Intel")]
    [InlineData("Some Adapter", "Unknown")]
    public void Vendor_detection(string name, string vendor) => Assert.Equal(vendor, SensorMapping.VendorOf(name, ""));

    [Fact]
    public void Fans_include_pumps_and_control_percent()
    {
        var board = new HardwareDevice("/lpc", "Super I/O", DeviceKind.SuperIO, null,
            [S("CPU Fan", SensorKind.Fan, 1100), S("Pump Fan", SensorKind.Fan, 2400), S("CPU Fan", SensorKind.Control, 40)]);
        var fans = SensorMapping.MapFans(Snapshot(board));
        Assert.Equal(2, fans.Count);
        Assert.Equal(40, fans.Single(f => f.Name == "CPU Fan").Percent);
        Assert.True(fans.Single(f => f.Name == "Pump Fan").IsPump);
    }
}

public class HistoryTests
{
    [Fact]
    public void Ring_buffer_keeps_most_recent_items_in_order()
    {
        var rb = new RingBuffer<int>(3);
        for (var i = 1; i <= 5; i++) rb.Add(i);
        Assert.Equal([3, 4, 5], rb.ToArray());
        Assert.Equal(3, rb.Count);
        Assert.Equal(3, rb[0]);
    }

    [Fact]
    public void Metric_history_is_bounded_and_ignores_missing_values()
    {
        var h = new MetricHistory(10);
        var t = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        for (var i = 0; i < 25; i++) h.Add("k", t.AddSeconds(i), i);
        h.Add("k", t.AddSeconds(30), null);
        h.Add("k", t.AddSeconds(31), double.NaN);

        var points = h.Get("k");
        Assert.Equal(10, points.Length);
        Assert.Equal(24, points[^1].Value);
        var stats = h.Stats("k");
        Assert.Equal(0, stats.Min);
        Assert.Equal(24, stats.Max);
        Assert.Equal(25, stats.Count);
    }

    [Fact]
    public void Resize_preserves_newest_points()
    {
        var h = new MetricHistory(20);
        for (var i = 0; i < 20; i++) h.Add("k", DateTimeOffset.Now, i);
        h.Resize(12);
        Assert.Equal(12, h.Get("k").Length);
        Assert.Equal(19, h.Get("k")[^1].Value);
    }

    [Fact]
    public void Running_stats_reset()
    {
        var s = new RunningStats();
        s.Add(2); s.Add(4);
        Assert.Equal(3, s.Average);
        s.Reset();
        Assert.Equal(0, s.Count);
        Assert.True(double.IsNaN(s.Average));
    }
}

public class AlertEngineTests
{
    private static AlertCondition C(bool active) => new("cpu.temp", active, "CPU hot", "msg", AlertSeverity.Warning);

    [Fact]
    public void Alert_fires_once_after_sustain_and_rearms_only_after_clearing()
    {
        var engine = new AlertEngine { Sustain = TimeSpan.FromSeconds(10) };
        var t0 = DateTimeOffset.Now;

        Assert.Empty(engine.Evaluate([C(true)], t0));
        Assert.Empty(engine.Evaluate([C(true)], t0.AddSeconds(5)));
        Assert.Single(engine.Evaluate([C(true)], t0.AddSeconds(10)));
        // Still active: no repeated notification.
        Assert.Empty(engine.Evaluate([C(true)], t0.AddSeconds(60)));
        // Briefly clears then returns: still no repeat (must stay clear for the sustain period).
        Assert.Empty(engine.Evaluate([C(false)], t0.AddSeconds(61)));
        Assert.Empty(engine.Evaluate([C(true)], t0.AddSeconds(62)));
        Assert.Empty(engine.Evaluate([C(true)], t0.AddSeconds(80)));
        // Clears for the full period, then a new episode fires again.
        Assert.Empty(engine.Evaluate([C(false)], t0.AddSeconds(90)));
        Assert.Empty(engine.Evaluate([C(false)], t0.AddSeconds(101)));
        Assert.Empty(engine.Evaluate([C(true)], t0.AddSeconds(102)));
        Assert.Single(engine.Evaluate([C(true)], t0.AddSeconds(112)));
    }

    [Fact]
    public void Missing_metrics_never_produce_alert_conditions()
    {
        var hw = HardwareSnapshot.Empty("test");
        var unavailable = Metric<double>.PermissionRequired("x");
        var cpu = new CpuSensors(null, unavailable, [], [], unavailable, unavailable, unavailable, Metric<string>.Unsupported("x"), unavailable);
        var live = new LiveSnapshot(DateTimeOffset.Now, hw, null, cpu, [], null, [], null, [], false);
        Assert.Empty(AlertRules.FromLive(live, new AlertSettings()));
    }

    [Fact]
    public void Sensor_limits_create_conditions_only_for_configured_sensors()
    {
        var dev = new HardwareDevice("/d", "Dev", DeviceKind.Gpu, null,
            [new SensorReading("/d/t", "Hot", SensorKind.Temperature, 90, null, null), new SensorReading("/d/u", "Other", SensorKind.Temperature, 99, null, null)]);
        var snap = new HardwareSnapshot(DateTimeOffset.Now, [dev], "t");
        var conditions = AlertRules.FromSensorLimits(snap, new Dictionary<string, double> { ["/d/t"] = 85 }).ToList();
        Assert.Single(conditions);
        Assert.True(conditions[0].IsActive);
    }
}

public class SettingsNormalizationTests
{
    [Fact]
    public void Normalize_clamps_out_of_range_values()
    {
        var s = new AppSettings { SensorIntervalMs = 1, HistoryMinutes = 100000, AccentColor = "nope", Widget = null!, Alerts = null! }.Normalize();
        Assert.Equal(250, s.SensorIntervalMs);
        Assert.Equal(240, s.HistoryMinutes);
        Assert.Equal("#4F8CFF", s.AccentColor);
        Assert.NotNull(s.Widget);
        Assert.NotNull(s.Alerts);
    }
}
