using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using OmniSpec.Core.Hardware;
using OmniSpec.Core.Metrics;

namespace OmniSpec.Hardware;

/// <summary>
/// Hardware sensor provider backed by LibreHardwareMonitorLib (MPL-2.0).
/// <para>
/// Without administrator rights LHM still exposes GPU sensors (NVAPI/ADLX/D3D), storage usage and some
/// battery data. CPU temperature/power, motherboard Super I/O sensors (fans, voltages) and SMART data
/// require administrator rights and the separately-installed PawnIO driver. OmniSpec never installs drivers.
/// </para>
/// </summary>
public sealed class LibreHardwareMonitorProvider : IHardwareSensorProvider
{
    private readonly ILogger<LibreHardwareMonitorProvider> _logger;
    private readonly object _gate = new();
    private Computer? _computer;
    private bool _disposed;
    private int _consecutiveErrors;
    private bool _lowLevelAvailable;

    public LibreHardwareMonitorProvider(ILogger<LibreHardwareMonitorProvider> logger)
    {
        _logger = logger;
        Privileges = new PrivilegeInfo(IsProcessElevated(), DetectPawnIo(), "PawnIO");
    }

    public string Name => DataSources.LibreHardwareMonitor;
    public bool IsMock => false;
    public ProviderStatus Status { get; private set; } = ProviderStatus.NotInitialized;
    public PrivilegeInfo Privileges { get; }

    public void Initialize(HardwareProviderOptions options)
    {
        lock (_gate)
        {
            if (_computer is not null || _disposed) return;
            try
            {
                var computer = new Computer
                {
                    IsCpuEnabled = options.Cpu,
                    IsGpuEnabled = options.Gpu,
                    IsMotherboardEnabled = options.Motherboard,
                    IsMemoryEnabled = options.Memory,
                    IsStorageEnabled = options.Storage,
                    IsControllerEnabled = options.Controllers,
                    IsBatteryEnabled = options.Battery,
                    IsPsuEnabled = options.Psu,
                    IsNetworkEnabled = false, // OmniSpec has its own lightweight network provider.
                };
                computer.Open();
                _computer = computer;
                _lowLevelAvailable = Privileges.IsElevated && IsPawnIoLoaded();

                var note = Privileges.IsElevated
                    ? Privileges.LowLevelDriverInstalled == false
                        ? "Running as administrator, but the PawnIO driver is not installed: CPU temperatures, fans and voltages may be unavailable."
                        : "Running with full sensor access."
                    : "Running without administrator rights: CPU temperatures/power, motherboard fans/voltages and SMART data may be unavailable.";
                Status = new ProviderStatus(MetricState.Available, note);
                _logger.LogInformation("LibreHardwareMonitor opened with {Count} hardware items. {Note}", computer.Hardware.Count, note);
            }
            catch (Exception ex)
            {
                Status = new ProviderStatus(MetricState.Error, $"Hardware library failed to initialize: {ex.Message}");
                _logger.LogError(ex, "LibreHardwareMonitor initialization failed");
            }
        }
    }

    public HardwareSnapshot Poll()
    {
        lock (_gate)
        {
            if (_computer is null || _disposed) return HardwareSnapshot.Empty(Name);
            var devices = new List<HardwareDevice>();
            try
            {
                foreach (var hw in _computer.Hardware) Collect(hw, null, devices);
                _consecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                // A single device failing (e.g. disconnected USB cooler) must not stop monitoring.
                _consecutiveErrors++;
                if (_consecutiveErrors <= 3 || _consecutiveErrors % 60 == 0)
                    _logger.LogWarning(ex, "Sensor poll failed ({Count} consecutive)", _consecutiveErrors);
            }
            return new HardwareSnapshot(DateTimeOffset.Now, devices, Name);
        }
    }

    private void Collect(IHardware hw, string? parentId, List<HardwareDevice> devices)
    {
        try
        {
            hw.Update();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update failed for {Hardware}", hw.Name);
        }

        var id = hw.Identifier.ToString();
        var deviceKind = MapDeviceKind(hw.HardwareType);
        var sensors = new List<SensorReading>(hw.Sensors.Length);
        foreach (var s in hw.Sensors)
        {
            var kind = MapSensorKind(s.SensorType);
            // Without the PawnIO driver, LHM returns placeholder values (0 °C, 0 W, fixed VID) for CPU telemetry. Drop them.
            if (!_lowLevelAvailable && SensorNormalizer.RequiresLowLevelAccess(deviceKind, kind)) continue;
            var value = SensorNormalizer.Normalize(deviceKind, kind, s.Value);
            var min = SensorNormalizer.Normalize(deviceKind, kind, s.Min);
            var max = SensorNormalizer.Normalize(deviceKind, kind, s.Max);
            sensors.Add(new SensorReading(s.Identifier.ToString(), s.Name, kind, value, min, max));
        }
        devices.Add(new HardwareDevice(id, hw.Name.Trim(), deviceKind, parentId, sensors));

        foreach (var sub in hw.SubHardware) Collect(sub, id, devices);
    }

    internal static DeviceKind MapDeviceKind(HardwareType type)
    {
        var name = type.ToString();
        if (name.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Gpu;
        return Enum.TryParse<DeviceKind>(name, ignoreCase: true, out var kind) ? kind : DeviceKind.Other;
    }

    internal static SensorKind MapSensorKind(SensorType type) =>
        Enum.TryParse<SensorKind>(type.ToString(), ignoreCase: true, out var kind) ? kind : SensorKind.Other;

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when ring-0 access is actually usable (driver installed and loaded by LHM).</summary>
    public bool LowLevelAccessAvailable => _lowLevelAvailable;

    private static bool IsPawnIoLoaded()
    {
        // LHM loads the driver itself when it is installed and the process is elevated.
        try { return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
        catch { return false; }
    }

    private static bool? DetectPawnIo()
    {
        try
        {
            return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _computer?.Close(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error closing LibreHardwareMonitor"); }
            _computer = null;
        }
    }
}
