using Microsoft.Extensions.Logging.Abstractions;
using OmniSpec.Core.Hardware;
using OmniSpec.Core.Metrics;
using OmniSpec.Hardware;
using OmniSpec.Monitoring.Providers;
using OmniSpec.SystemInformation;

namespace OmniSpec.Tests;

/// <summary>
/// Tests that touch the real machine (WMI, performance counters, hardware library).
/// They are excluded in CI with: dotnet test --filter "Category!=Hardware".
/// </summary>
[Trait("Category", "Hardware")]
public class HardwareTests
{
    [Fact]
    public async Task System_information_collects_core_sections()
    {
        var report = await new SystemInfoCollector(NullLogger<SystemInfoCollector>.Instance).CollectAsync(CancellationToken.None);
        Assert.Contains(report.Sections, s => s.Title == "Operating System");
        Assert.Contains(report.Sections, s => s.Title == "Processor");
        Assert.NotNull(report.Cpu);
        Assert.True(report.Cpu!.LogicalProcessors > 0);
        Assert.All(report.Sections.SelectMany(s => s.Items), i => Assert.False(string.IsNullOrEmpty(i.Source)));
    }

    [Fact]
    public void Hardware_provider_initializes_and_never_reports_placeholder_cpu_zero_temperatures()
    {
        using var provider = new LibreHardwareMonitorProvider(NullLogger<LibreHardwareMonitorProvider>.Instance);
        provider.Initialize(new HardwareProviderOptions());
        Assert.NotEqual(MetricState.Error, provider.Status.State);
        var snap = provider.Poll();
        foreach (var s in snap.OfKind(DeviceKind.Cpu).SelectMany(d => d.Sensors).Where(s => s.Kind == SensorKind.Temperature))
            Assert.True(s.Value is null or > 0);
    }

    [Fact]
    public async Task Cpu_usage_provider_returns_a_load_value()
    {
        using var cpu = new CpuUsageProvider(NullLogger<CpuUsageProvider>.Instance);
        cpu.Sample();
        await Task.Delay(1100);
        var sample = cpu.Sample();
        Assert.True(sample.TotalLoad.HasValue);
        Assert.InRange(sample.TotalLoad.Value, 0, 100);
    }

    [Fact]
    public void Memory_provider_reports_consistent_values()
    {
        using var mem = new MemoryProvider();
        var m = mem.Sample();
        Assert.True(m.TotalPhysical > 0);
        Assert.True(m.AvailablePhysical <= m.TotalPhysical);
        Assert.True(m.CommitLimitBytes >= m.CommittedBytes);
    }

    [Fact]
    public void Displays_are_enumerated() =>
        Assert.All(DisplayReader.ReadActiveDisplays(), d => Assert.False(string.IsNullOrEmpty(d.MonitorName)));

    [Fact]
    public async Task Storage_provider_lists_drives_with_health_status()
    {
        var drives = await new StorageInfoProvider(NullLogger<StorageInfoProvider>.Instance).GetDrivesAsync(null, CancellationToken.None);
        Assert.NotEmpty(drives);
        Assert.All(drives, d => Assert.False(string.IsNullOrEmpty(d.Model)));
    }
}
