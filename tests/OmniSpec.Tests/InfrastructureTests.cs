using System.Text.Json;
using OmniSpec.Core.Fps;
using OmniSpec.Core.Hardware;
using OmniSpec.Core.History;
using OmniSpec.Core.Live;
using OmniSpec.Core.Metrics;
using OmniSpec.Core.Settings;
using OmniSpec.Core.Storage;
using OmniSpec.Core.SystemInfo;
using OmniSpec.Hardware.Mock;
using OmniSpec.Infrastructure.Export;
using OmniSpec.Infrastructure.Settings;
using OmniSpec.Monitoring.Providers;

namespace OmniSpec.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("omnispec-test-").FullName;
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class SettingsStoreTests
{
    [Fact]
    public void Settings_roundtrip()
    {
        using var dir = new TempDir();
        var store = new JsonSettingsStore(System.IO.Path.Combine(dir.Path, "settings.json"));
        var s = store.Load();
        s.Theme = AppTheme.Light;
        s.SensorIntervalMs = 2000;
        s.Widget.Metrics = ["cpu", "fps"];
        s.SensorLimits["/gpu/0/temperature/0"] = 80;
        store.Save(s);

        var loaded = store.Load();
        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.Equal(2000, loaded.SensorIntervalMs);
        Assert.Equal(["cpu", "fps"], loaded.Widget.Metrics);
        Assert.Equal(80, loaded.SensorLimits["/gpu/0/temperature/0"]);
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults_and_are_preserved()
    {
        using var dir = new TempDir();
        var file = System.IO.Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(file, "{ this is not json");
        var loaded = new JsonSettingsStore(file).Load();
        Assert.Equal(1000, loaded.SensorIntervalMs);
        Assert.Single(Directory.GetFiles(dir.Path, "settings.corrupt-*.json"));
    }

    [Fact]
    public void Partial_settings_file_gets_defaults_for_missing_properties()
    {
        using var dir = new TempDir();
        var file = System.IO.Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(file, """{ "Theme": "Light" }""");
        var loaded = new JsonSettingsStore(file).Load();
        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.NotNull(loaded.Alerts);
        Assert.Equal(90, loaded.Alerts.CpuTemperatureC);
    }

    [Fact]
    public void Settings_service_raises_changed()
    {
        using var dir = new TempDir();
        var service = new SettingsService(new JsonSettingsStore(System.IO.Path.Combine(dir.Path, "s.json")));
        var raised = false;
        service.Changed += (_, _) => raised = true;
        service.Current.HistoryMinutes = 30;
        service.Save();
        Assert.True(raised);
        Assert.True(File.Exists(service.SettingsPath));
    }
}

public class ExportTests
{
    private static SystemReport Report(bool sensitive)
    {
        var info = new SystemInfoReport(DateTimeOffset.Now,
        [
            new InfoSection("Operating System",
            [
                InfoItem.Of("Windows edition", "Windows 11 Pro", DataSources.Wmi),
                InfoItem.Of("Hostname", "SECRET-PC", DataSources.Win32, sensitive: true),
                InfoItem.Missing("Timings", MetricState.Unsupported, DataSources.Wmi, "Not exposed"),
            ]),
        ], null, [], [], "Windows 11 Pro", "SECRET-PC", null);
        var drive = new PhysicalDriveInfo { DeviceId = "0", Model = "Disk", SerialNumber = "SN-SECRET-123", WindowsHealthStatus = "Healthy", BusType = "NVMe", MediaType = "SSD" };
        return new SystemReport("1.0.0", DateTimeOffset.Now, info, [(drive, DriveHealthEvaluator.Evaluate(drive))], null, sensitive);
    }

    [Fact]
    public void Json_report_excludes_sensitive_identifiers_by_default()
    {
        using var ms = new MemoryStream();
        Exporters.WriteJsonReport(ms, Report(sensitive: false));
        var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        using var _ = JsonDocument.Parse(json); // valid JSON
        Assert.DoesNotContain("SECRET-PC", json);
        Assert.DoesNotContain("SN-SECRET-123", json);
        Assert.Contains("Windows 11 Pro", json);
        Assert.Contains("Unsupported", json);
    }

    [Fact]
    public void Json_report_includes_sensitive_identifiers_when_enabled()
    {
        using var ms = new MemoryStream();
        Exporters.WriteJsonReport(ms, Report(sensitive: true));
        var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("SECRET-PC", json);
        Assert.Contains("SN-SECRET-123", json);
    }

    [Fact]
    public void Text_report_contains_sections_and_states()
    {
        using var w = new StringWriter();
        Exporters.WriteTextReport(w, Report(sensitive: false));
        var text = w.ToString();
        Assert.Contains("[Operating System]", text);
        Assert.Contains("Not Supported", text);
        Assert.DoesNotContain("SECRET-PC", text);
        Assert.Contains("Health verdict", text);
    }

    [Fact]
    public void History_csv_has_header_and_aligned_rows()
    {
        var h = new MetricHistory(100);
        var t = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        h.Add("a", t, 1.5);
        h.Add("b", t, 2);
        h.Add("a", t.AddSeconds(1), 3);
        using var w = new StringWriter();
        Exporters.WriteHistoryCsv(w, h, [("a", "CPU %"), ("b", "GPU %")]);
        var lines = w.ToString().Trim().Split(Environment.NewLine);
        Assert.Equal("Timestamp,CPU %,GPU %", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(",1.5,2", lines[1]);
        Assert.EndsWith(",3,", lines[2]); // missing value stays empty, never zero
    }

    [Fact]
    public void Frames_csv_contains_summary_and_rows()
    {
        var frames = new[] { new FrameSample(0.01, 10, 4, null, 9, null, 10, null, false, "g", 1, "Flip") };
        using var w = new StringWriter();
        Exporters.WriteFramesCsv(w, "game.exe", 1, frames, FrameStatisticsCalculator.Compute(frames));
        var text = w.ToString();
        Assert.Contains("Avg FPS,100", text);
        Assert.Contains("TimeSeconds,FrameTimeMs,FPS", text);
        Assert.Contains("0.01,10,100,4,,9,,10,,0,Flip", text);
    }

    [Fact]
    public void History_logger_row_leaves_missing_values_empty()
    {
        var unavailable = Metric<double>.Unavailable("x");
        var cpu = new CpuSensors(null, unavailable, [], [], unavailable, unavailable, unavailable, Metric<string>.Unavailable("x"), unavailable);
        var live = new LiveSnapshot(DateTimeOffset.Now, HardwareSnapshot.Empty("t"), null, cpu, [], null, [], null, [], false);
        var row = HistoryCsvLogger.Row(live).Split(',');
        Assert.Equal(11, row.Length);
        Assert.All(row.Skip(1).Take(8), Assert.Empty);
    }
}

public class MockProviderTests
{
    [Fact]
    public void Mock_provider_is_clearly_labelled_and_produces_data_after_initialization()
    {
        using var mock = new MockHardwareProvider();
        Assert.True(mock.IsMock);
        Assert.Empty(mock.Poll().Devices); // nothing before Initialize
        mock.Initialize(new HardwareProviderOptions());
        var snap = mock.Poll();
        Assert.Equal(DataSources.Mock, snap.Source);
        Assert.Contains(snap.Devices, d => d.Kind == DeviceKind.Cpu);
        Assert.Contains(snap.Devices, d => d.Kind == DeviceKind.Gpu);
        var cpu = SensorMapping.MapCpu(snap, mock.LowLevelAccessAvailable);
        Assert.True(cpu.PackageTemperature.HasValue);
    }

    [Fact]
    public void Mock_can_simulate_missing_cpu_temperatures()
    {
        using var mock = new MockHardwareProvider { SimulateMissingCpuTemperatures = true };
        mock.Initialize(new HardwareProviderOptions());
        var cpu = SensorMapping.MapCpu(mock.Poll(), mock.LowLevelAccessAvailable);
        Assert.Equal(MetricState.PermissionRequired, cpu.PackageTemperature.State);
    }
}

public class NetworkFilterTests
{
    [Theory]
    [InlineData("Ethernet-WFP Native MAC Layer LightWeight Filter-0000", true)]
    [InlineData("Wi-Fi-QoS Packet Scheduler-0000", true)]
    [InlineData("Ethernet", false)]
    [InlineData("Wi-Fi", false)]
    public void Filter_pseudo_interfaces_are_detected(string name, bool expected) =>
        Assert.Equal(expected, NetworkProvider.IsFilterPseudoInterface(name));

    [Fact]
    public void Utilization_requires_link_speed()
    {
        var a = new NetworkAdapterSample("id", "n", "d", "Ethernet", "Up", null, 1000, 0, 0, 0, 0, 0, null, [], null, false);
        Assert.Null(a.UtilizationPercent);
        var b = a with { LinkSpeedBitsPerSecond = 1_000_000_000, DownloadBytesPerSecond = 12_500_000 };
        Assert.Equal(10, b.UtilizationPercent!.Value, 3);
    }
}
