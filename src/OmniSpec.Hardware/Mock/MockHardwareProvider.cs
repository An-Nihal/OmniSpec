using OmniSpec.Core.Hardware;
using OmniSpec.Core.Metrics;

namespace OmniSpec.Hardware.Mock;

/// <summary>
/// DEVELOPMENT-ONLY mock provider used by tests and for UI work (start the Debug build with <c>--mock</c>).
/// This file is excluded from Release builds of OmniSpec.Hardware, so production builds can never show mock readings.
/// Every snapshot is labelled with <see cref="DataSources.Mock"/> and <see cref="IsMock"/> is true.
/// </summary>
public sealed class MockHardwareProvider : IHardwareSensorProvider
{
    private readonly Random _random = new(42);
    private int _tick;
    private bool _initialized;

    public string Name => DataSources.Mock;
    public bool IsMock => true;
    public ProviderStatus Status { get; private set; } = ProviderStatus.NotInitialized;
    public PrivilegeInfo Privileges { get; } = new(false, null, "none (mock)");
    public bool LowLevelAccessAvailable => !SimulateMissingCpuTemperatures;

    /// <summary>When true, the mock omits CPU temperature sensors to simulate a non-elevated session.</summary>
    public bool SimulateMissingCpuTemperatures { get; init; }

    public void Initialize(HardwareProviderOptions options)
    {
        _initialized = true;
        Status = new ProviderStatus(MetricState.Available, "MOCK DATA — development only");
    }

    public HardwareSnapshot Poll()
    {
        if (!_initialized) return HardwareSnapshot.Empty(Name);
        _tick++;
        double Wave(double baseValue, double amplitude, double period) =>
            baseValue + amplitude * Math.Sin(_tick * 2 * Math.PI / period) + _random.NextDouble() * amplitude * 0.2;

        var cpuSensors = new List<SensorReading>
        {
            new("/mock/cpu/load/0", "CPU Total", SensorKind.Load, Wave(35, 20, 30), 0, 100),
            new("/mock/cpu/power/0", "CPU Package", SensorKind.Power, Wave(45, 15, 25), 5, 120),
            new("/mock/cpu/voltage/0", "CPU Core", SensorKind.Voltage, Wave(1.2, 0.05, 20), 0.8, 1.4),
        };
        if (!SimulateMissingCpuTemperatures)
        {
            cpuSensors.Add(new("/mock/cpu/temp/0", "CPU Package", SensorKind.Temperature, Wave(58, 10, 40), 35, 90));
        }
        for (var i = 1; i <= 8; i++)
        {
            cpuSensors.Add(new($"/mock/cpu/clock/{i}", $"CPU Core #{i}", SensorKind.Clock, Wave(4200, 300, 15 + i), 800, 5200));
            if (!SimulateMissingCpuTemperatures)
                cpuSensors.Add(new($"/mock/cpu/temp/{i}", $"CPU Core #{i}", SensorKind.Temperature, Wave(55, 8, 30 + i), 30, 90));
        }

        var gpuSensors = new List<SensorReading>
        {
            new("/mock/gpu/load/0", "GPU Core", SensorKind.Load, Wave(60, 30, 45), 0, 100),
            new("/mock/gpu/temp/0", "GPU Core", SensorKind.Temperature, Wave(62, 8, 50), 30, 85),
            new("/mock/gpu/temp/1", "GPU Hot Spot", SensorKind.Temperature, Wave(72, 8, 50), 30, 95),
            new("/mock/gpu/clock/0", "GPU Core", SensorKind.Clock, Wave(2400, 200, 20), 210, 2800),
            new("/mock/gpu/clock/1", "GPU Memory", SensorKind.Clock, 10501, 405, 10501),
            new("/mock/gpu/power/0", "GPU Package", SensorKind.Power, Wave(180, 60, 40), 10, 320),
            new("/mock/gpu/fan/0", "GPU Fan 1", SensorKind.Fan, Wave(1500, 300, 60), 0, 3000),
            new("/mock/gpu/control/0", "GPU Fan 1", SensorKind.Control, Wave(45, 10, 60), 0, 100),
            new("/mock/gpu/smalldata/0", "GPU Memory Used", SensorKind.SmallData, Wave(6000, 1000, 80), 500, 12000),
            new("/mock/gpu/smalldata/1", "GPU Memory Total", SensorKind.SmallData, 12288, 12288, 12288),
        };

        var boardSensors = new List<SensorReading>
        {
            new("/mock/lpc/fan/0", "CPU Fan", SensorKind.Fan, Wave(1100, 150, 35), 600, 2000),
            new("/mock/lpc/fan/1", "System Fan #1", SensorKind.Fan, Wave(800, 80, 50), 500, 1200),
            new("/mock/lpc/temp/0", "Motherboard", SensorKind.Temperature, Wave(38, 2, 90), 30, 45),
            new("/mock/lpc/voltage/0", "+12V", SensorKind.Voltage, Wave(12.1, 0.05, 30), 11.9, 12.2),
        };

        var storageSensors = new List<SensorReading>
        {
            new("/mock/nvme/temp/0", "Temperature", SensorKind.Temperature, Wave(41, 3, 70), 30, 55),
            new("/mock/nvme/level/0", "Percentage Used", SensorKind.Level, 3, 3, 3),
            new("/mock/nvme/level/1", "Available Spare", SensorKind.Level, 100, 100, 100),
            new("/mock/nvme/level/2", "Available Spare Threshold", SensorKind.Level, 10, 10, 10),
            new("/mock/nvme/data/0", "Data Written", SensorKind.Data, 12345, 12345, 12345),
        };

        var devices = new List<HardwareDevice>
        {
            new("/mock/cpu", "Mock CPU 8-Core", DeviceKind.Cpu, null, cpuSensors),
            new("/mock/gpu", "Mock GPU (NVIDIA-like)", DeviceKind.Gpu, null, gpuSensors),
            new("/mock/mainboard", "Mock Mainboard", DeviceKind.Motherboard, null, Array.Empty<SensorReading>()),
            new("/mock/lpc", "Mock Super I/O", DeviceKind.SuperIO, "/mock/mainboard", boardSensors),
            new("/mock/nvme", "Mock NVMe SSD", DeviceKind.Storage, null, storageSensors),
        };
        return new HardwareSnapshot(DateTimeOffset.Now, devices, Name);
    }

    public void Dispose() { }
}
