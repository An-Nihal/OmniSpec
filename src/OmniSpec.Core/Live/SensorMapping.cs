using OmniSpec.Core.Hardware;
using OmniSpec.Core.Metrics;

namespace OmniSpec.Core.Live;

/// <summary>
/// Maps vendor-specific sensor names (as reported by LibreHardwareMonitor) to OmniSpec's typed CPU/GPU/fan models.
/// Candidate names are listed in priority order. Unmatched values become Unavailable/PermissionRequired — never guessed.
/// </summary>
public static class SensorMapping
{
    /// <param name="performanceLimit">Windows "% Performance Limit" (100 = not limited), used when TjMax distance is not exposed.</param>
    public static CpuSensors MapCpu(HardwareSnapshot snapshot, bool lowLevelAccess, Metric<double>? performanceLimit = null)
    {
        var cpu = snapshot.OfKind(DeviceKind.Cpu).FirstOrDefault();
        var ts = snapshot.Timestamp;
        var src = snapshot.Source;
        // CPU temperatures/power/voltage require ring-0 access (PawnIO driver + administrator) on most platforms.
        var missing = lowLevelAccess ? MetricState.Unavailable : MetricState.PermissionRequired;
        var missingNote = lowLevelAccess ? "Not exposed by this CPU" : "Requires administrator mode and the PawnIO driver";

        if (cpu is null)
        {
            var none = Metric<double>.WithState(missing, src, missingNote);
            return new CpuSensors(null, none, Array.Empty<SensorReading>(), Array.Empty<SensorReading>(), none, none, none,
                Metric<string>.WithState(missing, src, missingNote), Metric<double>.Unavailable(src));
        }

        var package = SensorQuery.Find(cpu, SensorKind.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)", "Package", "Core Max", "CPU Core");
        var coreTemps = SensorQuery.All(cpu, SensorKind.Temperature, s => s.Value.HasValue &&
                (s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase) || s.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase) || s.Name.StartsWith("CCD", StringComparison.OrdinalIgnoreCase)) &&
                !s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => SensorQuery.TrailingIndex(s.Name) ?? 0).ToList();
        // Prefer reported core clocks; AMD also exposes "Core #n (Effective)" which must not be mixed with them.
        static bool IsCoreClock(SensorReading s) => s.Value is > 0 && s.Name.Contains("Core #", StringComparison.OrdinalIgnoreCase);
        static bool IsEffective(SensorReading s) => s.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase);
        var clocks = SensorQuery.All(cpu, SensorKind.Clock, s => IsCoreClock(s) && !IsEffective(s)).ToList();
        if (clocks.Count == 0) clocks = SensorQuery.All(cpu, SensorKind.Clock, s => IsCoreClock(s) && IsEffective(s)).ToList();
        clocks = clocks.OrderBy(s => CoreNumber(s.Name)).ToList();
        var avgClock = clocks.Count > 0
            ? Metric<double>.Available(clocks.Average(c => c.Value!.Value), src, ts, "Average of per-core clocks")
            : Metric<double>.Unavailable(src, "Per-core clocks not exposed");
        var power = SensorQuery.Find(cpu, SensorKind.Power, "CPU Package", "Package");
        var voltage = SensorQuery.Find(cpu, SensorKind.Voltage, "CPU Core", "Core (SVI2 TFN)", "Core #1 VID", "VID");

        // Throttling: LHM reports "Distance to TjMax" sensors on Intel; a distance near zero means the CPU is at its thermal limit.
        Metric<string> throttle;
        var distances = SensorQuery.All(cpu, SensorKind.Temperature, s => s.Value.HasValue && s.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase));
        if (distances.Count > 0)
        {
            var min = distances.Min(d => d.Value!.Value);
            throttle = Metric<string>.Available(min <= 1 ? "Thermal limit reached" : "Not throttling", src, ts, $"Closest core is {min:0} °C from TjMax");
        }
        else if (performanceLimit is { HasValue: true } limit)
        {
            throttle = limit.Value >= 99
                ? Metric<string>.Available("Not limited", limit.Source, limit.Timestamp, "Windows % Performance Limit = 100%")
                : Metric<string>.Available($"Limited to {limit.Value:0}% of nominal", limit.Source, limit.Timestamp,
                    "Limited by power policy, power budget or thermal conditions (Windows % Performance Limit)");
        }
        else
        {
            throttle = Metric<string>.Unsupported(src, "Thermal headroom (TjMax distance) not exposed for this CPU");
        }

        var fan = snapshot.Devices.SelectMany(d => d.Sensors.Where(s => s.Kind == SensorKind.Fan && s.Value.HasValue && s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))).FirstOrDefault();

        return new CpuSensors(
            cpu.Name,
            SensorQuery.ToMetric(package, ts, src, missing, missingNote),
            coreTemps,
            clocks,
            avgClock,
            SensorQuery.ToMetric(power, ts, src, missing, missingNote),
            SensorQuery.ToMetric(voltage, ts, src, missing, missingNote),
            throttle,
            SensorQuery.ToMetric(fan, ts, src, MetricState.Unavailable, "No fan header labelled as CPU fan"));
    }

    public static IReadOnlyList<GpuSensors> MapGpus(HardwareSnapshot snapshot)
    {
        var ts = snapshot.Timestamp;
        var src = snapshot.Source;
        var result = new List<GpuSensors>();
        foreach (var gpu in snapshot.OfKind(DeviceKind.Gpu))
        {
            Metric<double> M(SensorKind kind, params string[] names) =>
                SensorQuery.ToMetric(SensorQuery.Find(gpu, kind, names), ts, src, MetricState.Unsupported, "Not exposed by this GPU/driver");

            var vendor = VendorOf(gpu.Name, gpu.Id);
            var dedicatedTotal = M(SensorKind.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
            var integrated = vendor == "Intel" && !gpu.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase)
                             || gpu.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
                             || gpu.Name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase);

            var fanPercent = SensorQuery.Find(gpu, SensorKind.Control, "GPU Fan", "Fan");
            result.Add(new GpuSensors(
                gpu.Id,
                gpu.Name,
                vendor,
                integrated,
                Load: M(SensorKind.Load, "GPU Core", "D3D 3D"),
                Temperature: M(SensorKind.Temperature, "GPU Core", "GPU Temperature"),
                HotSpot: M(SensorKind.Temperature, "GPU Hot Spot", "GPU Hotspot"),
                MemoryJunction: M(SensorKind.Temperature, "GPU Memory Junction", "GPU Memory"), // AMD reports memory temperature as "GPU Memory"
                CoreClock: M(SensorKind.Clock, "GPU Core", "GPU Shader"),
                MemoryClock: M(SensorKind.Clock, "GPU Memory"),
                Voltage: M(SensorKind.Voltage, "GPU Core"),
                Power: M(SensorKind.Power, "GPU Package", "GPU Power", "GPU Core"),
                FanRpm: M(SensorKind.Fan, "GPU Fan", "GPU Fan 1", "Fan"),
                FanPercent: SensorQuery.ToMetric(fanPercent, ts, src, MetricState.Unsupported, "Not exposed by this GPU/driver"),
                DedicatedUsedMB: M(SensorKind.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
                DedicatedTotalMB: dedicatedTotal,
                SharedUsedMB: M(SensorKind.SmallData, "D3D Shared Memory Used", "GPU Shared Memory Used"),
                SharedTotalMB: M(SensorKind.SmallData, "D3D Shared Memory Total", "GPU Shared Memory Total"),
                MemoryControllerLoad: M(SensorKind.Load, "GPU Memory Controller", "GPU Memory"),
                VideoEngineLoad: M(SensorKind.Load, "GPU Video Engine", "D3D Video Codec"),
                VideoDecodeLoad: M(SensorKind.Load, "D3D Video Decode", "GPU Video Decode"),
                VideoEncodeLoad: M(SensorKind.Load, "D3D Video Encode", "GPU Video Encode"),
                BusLoad: M(SensorKind.Load, "GPU Bus")));
        }
        // Dedicated GPUs first: they are what users usually care about.
        return result.OrderBy(g => g.IsLikelyIntegrated).ToList();
    }

    /// <summary>Core number from names like "Core #3" or "Core #3 (Effective)".</summary>
    public static int CoreNumber(string name)
    {
        var hash = name.IndexOf('#');
        if (hash < 0) return 0;
        var digits = new string(name.Skip(hash + 1).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    public static IReadOnlyList<FanReading> MapFans(HardwareSnapshot snapshot)
    {
        var fans = new List<FanReading>();
        foreach (var device in snapshot.Devices)
        {
            var controls = device.Sensors.Where(s => s.Kind == SensorKind.Control).ToList();
            foreach (var fan in device.Sensors.Where(s => s.Kind == SensorKind.Fan))
            {
                var pct = controls.FirstOrDefault(c => string.Equals(c.Name, fan.Name, StringComparison.OrdinalIgnoreCase))?.Value;
                var isPump = fan.Name.Contains("Pump", StringComparison.OrdinalIgnoreCase) || fan.Name.Contains("AIO", StringComparison.OrdinalIgnoreCase);
                fans.Add(new FanReading(device.Name, fan.Name, fan.Value, pct, isPump));
            }
            if (device.Kind == DeviceKind.Gpu && !device.Sensors.Any(s => s.Kind == SensorKind.Fan))
            {
                foreach (var c in controls.Where(c => c.Name.Contains("Fan", StringComparison.OrdinalIgnoreCase)))
                    fans.Add(new FanReading(device.Name, c.Name, null, c.Value, false));
            }
        }
        return fans;
    }

    public static string VendorOf(string name, string id)
    {
        var s = (name + " " + id).ToLowerInvariant();
        if (s.Contains("nvidia") || s.Contains("geforce") || s.Contains("quadro") || s.Contains("rtx")) return "NVIDIA";
        if (s.Contains("amd") || s.Contains("radeon") || s.Contains("ati ")) return "AMD";
        if (s.Contains("intel") || s.Contains("arc ") || s.Contains("iris") || s.Contains("uhd")) return "Intel";
        if (s.Contains("qualcomm") || s.Contains("adreno")) return "Qualcomm";
        return "Unknown";
    }
}
