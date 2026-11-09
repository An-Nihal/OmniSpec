using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OmniSpec.Core.Alerts;
using OmniSpec.Core.Hardware;
using OmniSpec.Core.History;
using OmniSpec.Core.Live;
using OmniSpec.Core.Settings;
using OmniSpec.Core.Storage;
using OmniSpec.Core.SystemInfo;

namespace OmniSpec.Monitoring;

public sealed record DriveState(PhysicalDriveInfo Drive, DriveHealthAssessment Health);

/// <summary>
/// The single owner of all polling. It runs exactly one loop per data class (sensors, storage, processes),
/// each with its own interval, and publishes immutable snapshots via events raised on background threads.
/// UI code must marshal to its dispatcher. Starting twice is a no-op, so duplicate polling cannot occur.
/// </summary>
public sealed class MonitoringService : IAsyncDisposable
{
    private readonly ILogger<MonitoringService> _logger;
    private readonly IHardwareSensorProvider _hardware;
    private readonly ICpuUsageProvider _cpu;
    private readonly IMemoryProvider _memory;
    private readonly INetworkProvider _network;
    private readonly IBatteryProvider _battery;
    private readonly IProcessProvider _processes;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly IStorageInfoProvider _storage;
    private readonly ISettingsService _settings;
    private readonly SemaphoreSlim _systemInfoGate = new(1, 1);
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private readonly ManualResetEventSlim _hardwareReady = new(false);
    private readonly AutoResetEvent _storageRefreshRequested = new(false);
    private CancellationTokenSource? _cts;
    private readonly List<Task> _loops = new();
    private PeriodicTimer? _sensorTimer;
    private PeriodicTimer? _processTimer;
    private int _processSubscribers;
    private int _started;

    public MonitoringService(
        ILogger<MonitoringService> logger,
        IHardwareSensorProvider hardware,
        ICpuUsageProvider cpu,
        IMemoryProvider memory,
        INetworkProvider network,
        IBatteryProvider battery,
        IProcessProvider processes,
        ISystemInfoProvider systemInfo,
        IStorageInfoProvider storage,
        ISettingsService settings)
    {
        _logger = logger;
        _hardware = hardware;
        _cpu = cpu;
        _memory = memory;
        _network = network;
        _battery = battery;
        _processes = processes;
        _systemInfo = systemInfo;
        _storage = storage;
        _settings = settings;
        History = new MetricHistory(HistoryCapacity());
        Alerts = new AlertEngine { Sustain = TimeSpan.FromSeconds(settings.Current.Alerts.SustainSeconds) };
        _settings.Changed += (_, _) => ApplySettings();
    }

    public MetricHistory History { get; }
    public AlertEngine Alerts { get; }
    public IHardwareSensorProvider HardwareProvider => _hardware;

    public LiveSnapshot? Latest { get; private set; }
    public IReadOnlyList<DriveState> Drives { get; private set; } = Array.Empty<DriveState>();
    public SystemInfoReport SystemInfo { get; private set; } = SystemInfoReport.Empty;
    public IReadOnlyList<ProcessSample> Processes { get; private set; } = Array.Empty<ProcessSample>();
    public bool IsHardwareReady => _hardwareReady.IsSet;
    public DateTimeOffset? DrivesUpdatedAt { get; private set; }

    public event EventHandler<LiveSnapshot>? LiveUpdated;
    public event EventHandler<IReadOnlyList<DriveState>>? DrivesUpdated;
    public event EventHandler<SystemInfoReport>? SystemInfoUpdated;
    public event EventHandler<IReadOnlyList<ProcessSample>>? ProcessesUpdated;
    public event EventHandler<AlertEvent>? AlertRaised;
    public event EventHandler? HardwareReady;

    private int HistoryCapacity()
    {
        var s = _settings.Current;
        return (int)Math.Ceiling(s.HistoryMinutes * 60_000.0 / s.SensorIntervalMs);
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _sensorTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(_settings.Current.SensorIntervalMs));
        _processTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(_settings.Current.ProcessIntervalMs));

        _loops.Add(Task.Run(() => InitializeHardware(ct), ct));
        _loops.Add(Task.Run(() => SensorLoopAsync(ct), ct));
        _loops.Add(Task.Run(() => StorageLoopAsync(ct), ct));
        _loops.Add(Task.Run(() => ProcessLoopAsync(ct), ct));
        _ = RefreshSystemInfoAsync();
    }

    private void InitializeHardware(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var s = _settings.Current;
        try
        {
            _hardware.Initialize(new HardwareProviderOptions
            {
                Cpu = s.EnableCpuSensors,
                Gpu = s.EnableGpuSensors,
                Motherboard = s.EnableMotherboardSensors,
                Storage = s.EnableStorageSensors,
                Memory = s.EnableMemorySensors,
                Controllers = s.EnableControllerSensors,
                Psu = s.EnablePsuSensors,
                Battery = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hardware provider initialization failed");
        }
        finally
        {
            _logger.LogInformation("Hardware provider ready in {Ms} ms: {Status}", sw.ElapsedMilliseconds, _hardware.Status.Message);
            _hardwareReady.Set();
            if (!ct.IsCancellationRequested) HardwareReady?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task SensorLoopAsync(CancellationToken ct)
    {
        try
        {
            // Produce an immediate first snapshot (performance counters/memory/network work before hardware init completes).
            Tick(ct);
            while (await _sensorTimer!.WaitForNextTickAsync(ct))
            {
                Tick(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Sensor loop terminated unexpectedly"); }
    }

    private void Tick(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        try
        {
            var hw = _hardwareReady.IsSet ? _hardware.Poll() : HardwareSnapshot.Empty(_hardware.Name);
            var cpuUsage = Safe(() => _cpu.Sample(), "CPU usage");
            var mem = Safe(() => _memory.Sample(), "memory");
            var net = Safe(() => _network.Sample(), "network") ?? Array.Empty<NetworkAdapterSample>();
            var bat = Safe(() => _battery.Sample(), "battery");

            var live = new LiveSnapshot(
                DateTimeOffset.Now, hw, cpuUsage,
                SensorMapping.MapCpu(hw, _hardware.LowLevelAccessAvailable, cpuUsage?.PerformanceLimitPercent),
                SensorMapping.MapGpus(hw),
                mem, net, bat,
                SensorMapping.MapFans(hw),
                _hardware.IsMock);

            Record(live);
            Latest = live;
            EvaluateAlerts(live);
            LiveUpdated?.Invoke(this, live);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sensor tick failed");
        }
    }

    private T? Safe<T>(Func<T> read, string what) where T : class
    {
        try { return read(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sampling {What} failed", what);
            return null;
        }
    }

    private static readonly HashSet<SensorKind> RecordedSensorKinds =
    [
        SensorKind.Temperature, SensorKind.Fan, SensorKind.Power, SensorKind.Voltage, SensorKind.Load,
        SensorKind.Clock, SensorKind.Control, SensorKind.Current, SensorKind.Flow, SensorKind.SmallData,
    ];

    private void Record(LiveSnapshot live)
    {
        var t = live.Timestamp;
        var h = History;
        h.Add(HistoryKeys.CpuLoad, t, live.CpuUsage?.TotalLoad.HasValue == true ? live.CpuUsage.TotalLoad.Value : null);
        h.Add(HistoryKeys.CpuTemp, t, live.Cpu.PackageTemperature.HasValue ? live.Cpu.PackageTemperature.Value : null);
        h.Add(HistoryKeys.CpuPower, t, live.Cpu.PackagePower.HasValue ? live.Cpu.PackagePower.Value : null);
        var clock = live.Cpu.AverageCoreClock.HasValue ? live.Cpu.AverageCoreClock : live.CpuUsage?.EffectiveClockMHz;
        h.Add(HistoryKeys.CpuClock, t, clock?.HasValue == true ? clock.Value : null);
        if (live.Memory is { } m && m.TotalPhysical > 0)
        {
            h.Add(HistoryKeys.MemUsedPercent, t, m.UsedPercent);
            h.Add(HistoryKeys.MemUsedGb, t, m.UsedPhysical / 1024d / 1024 / 1024);
            h.Add(HistoryKeys.MemCommitPercent, t, m.CommitPercent);
        }
        for (var i = 0; i < live.Gpus.Count; i++)
        {
            var g = live.Gpus[i];
            h.Add(HistoryKeys.GpuLoad(i), t, g.Load.HasValue ? g.Load.Value : null);
            h.Add(HistoryKeys.GpuTemp(i), t, g.Temperature.HasValue ? g.Temperature.Value : null);
            h.Add(HistoryKeys.GpuPower(i), t, g.Power.HasValue ? g.Power.Value : null);
            h.Add(HistoryKeys.GpuVram(i), t, g.DedicatedUsedMB.HasValue ? g.DedicatedUsedMB.Value : null);
        }
        foreach (var n in live.Network.Where(n => n.Status == "Up"))
        {
            h.Add(HistoryKeys.NetDown(n.Id), t, n.DownloadBytesPerSecond);
            h.Add(HistoryKeys.NetUp(n.Id), t, n.UploadBytesPerSecond);
        }
        foreach (var d in live.Hardware.Devices)
            foreach (var s in d.Sensors)
                if (s.Value.HasValue && RecordedSensorKinds.Contains(s.Kind))
                    h.Add(HistoryKeys.Sensor(s.Id), t, s.Value);
    }

    private void EvaluateAlerts(LiveSnapshot live)
    {
        var s = _settings.Current.Alerts;
        if (!s.Enabled) return;
        var conditions = AlertRules.FromLive(live, s)
            .Concat(AlertRules.FromDrives(Drives.Select(d => (d.Drive, d.Health)), s))
            .Concat(AlertRules.FromSensorLimits(live.Hardware, _settings.Current.SensorLimits));
        foreach (var evt in Alerts.Evaluate(conditions, live.Timestamp))
        {
            _logger.LogInformation("Alert: {Title} — {Message}", evt.Title, evt.Message);
            AlertRaised?.Invoke(this, evt);
        }
    }

    // ------------------------------------------------------------------ storage
    private async Task StorageLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Run(() => _hardwareReady.Wait(ct), ct);
            while (!ct.IsCancellationRequested)
            {
                await RefreshDrivesAsync(ct);
                var interval = TimeSpan.FromSeconds(_settings.Current.StorageIntervalSeconds);
                await Task.Run(() => WaitHandle.WaitAny([_storageRefreshRequested, ct.WaitHandle], interval), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Storage loop terminated unexpectedly"); }
    }

    /// <summary>Requests an immediate storage refresh from the storage loop.</summary>
    public void RequestDriveRefresh() => _storageRefreshRequested.Set();

    private async Task RefreshDrivesAsync(CancellationToken ct)
    {
        if (!await _storageGate.WaitAsync(0, ct)) return;
        try
        {
            var drives = await _storage.GetDrivesAsync(Latest?.Hardware, ct);
            Drives = drives.Select(d => new DriveState(d, DriveHealthEvaluator.Evaluate(d))).ToList();
            DrivesUpdatedAt = DateTimeOffset.Now;
            foreach (var d in Drives)
                History.Add(HistoryKeys.DriveTemp(d.Drive.DeviceId), DateTimeOffset.Now, d.Health.Temperature.HasValue ? d.Health.Temperature.Value : null);
            DrivesUpdated?.Invoke(this, Drives);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Drive refresh failed"); }
        finally { _storageGate.Release(); }
    }

    // ------------------------------------------------------------------ processes
    /// <summary>Process polling only runs while at least one view needs it, keeping idle CPU usage low.</summary>
    public IDisposable SubscribeProcesses()
    {
        Interlocked.Increment(ref _processSubscribers);
        return new Unsubscriber(() => Interlocked.Decrement(ref _processSubscribers));
    }

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _processTimer!.WaitForNextTickAsync(ct))
            {
                if (Volatile.Read(ref _processSubscribers) <= 0) continue;
                try
                {
                    Processes = _processes.Sample();
                    ProcessesUpdated?.Invoke(this, Processes);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Process sampling failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ system info
    public async Task RefreshSystemInfoAsync()
    {
        if (!await _systemInfoGate.WaitAsync(0)) return;
        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            SystemInfo = await _systemInfo.CollectAsync(ct);
            SystemInfoUpdated?.Invoke(this, SystemInfo);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "System information collection failed"); }
        finally { _systemInfoGate.Release(); }
    }

    private void ApplySettings()
    {
        var s = _settings.Current;
        try
        {
            if (_sensorTimer is not null) _sensorTimer.Period = TimeSpan.FromMilliseconds(s.SensorIntervalMs);
            if (_processTimer is not null) _processTimer.Period = TimeSpan.FromMilliseconds(s.ProcessIntervalMs);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Updating timer period failed"); }
        if (History.Capacity != HistoryCapacity()) History.Resize(HistoryCapacity());
        Alerts.Sustain = TimeSpan.FromSeconds(Math.Max(0, s.Alerts.SustainSeconds));
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) { _hardware.Dispose(); return; }
        _cts.Cancel();
        try { await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* loops observe cancellation */ }
        _sensorTimer?.Dispose();
        _processTimer?.Dispose();
        _cpu.Dispose();
        (_memory as IDisposable)?.Dispose();
        _hardware.Dispose();
        _cts.Dispose();
    }
}
