using OmniSpec.Core.Hardware;
using OmniSpec.Core.Metrics;
using OmniSpec.Core.Storage;
using OmniSpec.SystemInformation;

namespace OmniSpec.Tests;

public class DriveHealthEvaluatorTests
{
    private static PhysicalDriveInfo Drive(string? health = "Healthy", string bus = "NVMe", string media = "SSD",
        ReliabilityCounters? rel = null, params SensorReading[] smart) => new()
    {
        DeviceId = "0",
        Model = "Test Drive",
        BusType = bus,
        MediaType = media,
        WindowsHealthStatus = health,
        Reliability = rel is null ? Metric<ReliabilityCounters>.PermissionRequired("x") : Metric<ReliabilityCounters>.Available(rel, "x"),
        SmartSensors = smart,
    };

    private static ReliabilityCounters Rel(double? wear = null, double? uncorrected = null, double? temp = null, double? poh = null) =>
        new(temp, null, wear, poh, null, null, null, null, uncorrected, null, null, null, null, null, null);

    private static SensorReading Smart(string name, double value) => new($"/nvme/0/{name}", name, SensorKind.Level, value, null, null);

    [Fact]
    public void No_data_is_unknown_and_never_fabricates_health_percentages()
    {
        var a = DriveHealthEvaluator.Evaluate(Drive(health: null));
        Assert.Equal(DriveHealthLevel.Unknown, a.Level);
        Assert.False(a.EstimatedRemainingLife.HasValue);
        Assert.False(a.DeviceReportedWear.HasValue);
        Assert.False(a.OfficialStatus.HasValue);
    }

    [Fact]
    public void Windows_healthy_without_wear_data_is_healthy_but_remaining_life_unavailable()
    {
        var a = DriveHealthEvaluator.Evaluate(Drive());
        Assert.Equal(DriveHealthLevel.Healthy, a.Level);
        Assert.Equal("Healthy", a.OfficialStatus.Value);
        Assert.Equal(MetricState.Unavailable, a.EstimatedRemainingLife.State);
    }

    [Fact]
    public void Nvme_percentage_used_is_wear_and_remaining_life_is_a_labelled_estimate()
    {
        var a = DriveHealthEvaluator.Evaluate(Drive(smart: Smart("Percentage Used", 3)));
        Assert.Equal(3, a.DeviceReportedWear.Value);
        Assert.Equal(97, a.EstimatedRemainingLife.Value);
        Assert.Contains("Estimate", a.EstimatedRemainingLife.Note);
        Assert.Equal(DriveHealthLevel.Healthy, a.Level);
    }

    [Fact]
    public void Windows_wear_counter_100_means_endurance_reached_not_full_life()
    {
        var a = DriveHealthEvaluator.Evaluate(Drive(rel: Rel(wear: 100)));
        Assert.Equal(100, a.DeviceReportedWear.Value);
        Assert.Equal(0, a.EstimatedRemainingLife.Value);
        Assert.Equal(DriveHealthLevel.Warning, a.Level);
    }

    [Fact]
    public void Wear_counter_is_ignored_for_hard_disks()
    {
        var a = DriveHealthEvaluator.Evaluate(Drive(bus: "SATA", media: "HDD", rel: Rel(wear: 0)));
        Assert.False(a.DeviceReportedWear.HasValue);
    }

    [Theory]
    [InlineData(10, DriveHealthLevel.Healthy)]
    [InlineData(60, DriveHealthLevel.Good)]
    [InlineData(92, DriveHealthLevel.Warning)]
    public void Wear_thresholds(double used, DriveHealthLevel expected) =>
        Assert.Equal(expected, DriveHealthEvaluator.Evaluate(Drive(smart: Smart("Percentage Used", used))).Level);

    [Fact]
    public void Unhealthy_status_is_critical_even_with_low_wear()
    {
        var a = DriveHealthEvaluator.Evaluate(Drive(health: "Unhealthy", smart: Smart("Percentage Used", 1)));
        Assert.Equal(DriveHealthLevel.Critical, a.Level);
    }

    [Fact]
    public void Available_spare_below_threshold_is_critical()
    {
        var a = DriveHealthEvaluator.Evaluate(Drive(smart: [Smart("Available Spare", 5), Smart("Available Spare Threshold", 10)]));
        Assert.Equal(DriveHealthLevel.Critical, a.Level);
    }

    [Fact]
    public void Uncorrected_errors_raise_a_warning()
    {
        var a = DriveHealthEvaluator.Evaluate(Drive(rel: Rel(uncorrected: 2)));
        Assert.Equal(DriveHealthLevel.Warning, a.Level);
        Assert.Contains(a.Findings, f => f.Severity == FindingSeverity.Warning && f.Source == DataSources.ReliabilityCounters);
    }

    [Fact]
    public void Every_finding_has_a_source()
    {
        var a = DriveHealthEvaluator.Evaluate(Drive(health: "Warning", rel: Rel(wear: 95, uncorrected: 1), smart: Smart("Percentage Used", 95)));
        Assert.All(a.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Source)));
    }

    [Fact]
    public void Vendor_specific_raw_attributes_are_not_interpreted()
    {
        // An unknown vendor attribute must not change the verdict.
        var a = DriveHealthEvaluator.Evaluate(Drive(smart: Smart("Vendor Specific 0xE7", 1)));
        Assert.Equal(DriveHealthLevel.Healthy, a.Level);
        Assert.False(a.EstimatedRemainingLife.HasValue);
    }

    [Fact]
    public void Temperature_prefers_smart_then_reliability_counters()
    {
        var a = DriveHealthEvaluator.Evaluate(Drive(rel: Rel(temp: 38)));
        Assert.Equal(38, a.Temperature.Value);
        Assert.Equal(DataSources.ReliabilityCounters, a.Temperature.Source);
        var b = DriveHealthEvaluator.Evaluate(Drive(rel: Rel(temp: 38), smart: new SensorReading("/t", "Temperature", SensorKind.Temperature, 41, null, null)));
        Assert.Equal(41, b.Temperature.Value);
    }
}

public class StorageMatchingTests
{
    private static HardwareDevice Dev(string id, string name) =>
        new(id, name, DeviceKind.Storage, null, [new SensorReading(id + "/t", "Temperature", SensorKind.Temperature, 40, null, null)]);

    [Fact]
    public void Unique_model_match_attaches_smart_sensors()
    {
        var (device, sensors) = StorageInfoProvider.MatchSmart([Dev("/nvme/7", "Samsung SSD 990 PRO 1TB")], "Samsung SSD 990 PRO 1TB", null, "2");
        Assert.Equal("Samsung SSD 990 PRO 1TB", device);
        Assert.Single(sensors);
    }

    [Fact]
    public void Identical_models_are_matched_by_index_or_not_at_all()
    {
        var devices = new[] { Dev("/nvme/0", "WD Blue SN580"), Dev("/nvme/1", "WD Blue SN580") };
        Assert.Equal("WD Blue SN580", StorageInfoProvider.MatchSmart(devices, "WD Blue SN580", null, "1").Device);
        // Ambiguous and no index match: never guess.
        Assert.Null(StorageInfoProvider.MatchSmart(devices, "WD Blue SN580", null, "5").Device);
    }

    [Theory]
    [InlineData(17UL, "NVMe")]
    [InlineData(11UL, "SATA")]
    [InlineData(7UL, "USB")]
    [InlineData(99UL, "Unknown")]
    public void Bus_type_names(ulong code, string name) => Assert.Equal(name, StorageInfoProvider.BusTypeName(code));

    [Theory]
    [InlineData(0UL, "Healthy")]
    [InlineData(1UL, "Warning")]
    [InlineData(2UL, "Unhealthy")]
    [InlineData(5UL, null)]
    public void Health_status_names(ulong code, string? name) => Assert.Equal(name, StorageInfoProvider.HealthName(code));
}
