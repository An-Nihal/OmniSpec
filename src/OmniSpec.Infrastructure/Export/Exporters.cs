using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniSpec.Core.Fps;
using OmniSpec.Core.History;
using OmniSpec.Core.Live;
using OmniSpec.Core.Metrics;
using OmniSpec.Core.Storage;
using OmniSpec.Core.SystemInfo;

namespace OmniSpec.Infrastructure.Export;

/// <summary>Everything included in a full system report export.</summary>
public sealed record SystemReport(
    string AppVersion,
    DateTimeOffset GeneratedAt,
    SystemInfoReport SystemInfo,
    IReadOnlyList<(PhysicalDriveInfo Drive, DriveHealthAssessment Health)> Drives,
    LiveSnapshot? Live,
    bool IncludeSensitive);

/// <summary>
/// Report exporters. All exports are written to local files chosen by the user; nothing is uploaded.
/// Sensitive identifiers (serial numbers, MAC addresses, hostname, user name) are excluded unless the user enabled them.
/// </summary>
public static class Exporters
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // ------------------------------------------------------------------ CSV (history)
    /// <summary>Writes the selected history series as a wide CSV (timestamp + one column per series).</summary>
    public static void WriteHistoryCsv(TextWriter writer, MetricHistory history, IReadOnlyList<(string Key, string Header)> series)
    {
        var data = series.Select(s => (s.Header, Points: history.Get(s.Key))).ToList();
        var timestamps = data.SelectMany(d => d.Points.Select(p => p.UnixMs)).Distinct().Order().ToList();
        var lookups = data.Select(d => d.Points.GroupBy(p => p.UnixMs).ToDictionary(g => g.Key, g => g.Last().Value)).ToList();

        writer.WriteLine("Timestamp," + string.Join(",", data.Select(d => Csv(d.Header))));
        foreach (var ts in timestamps)
        {
            var sb = new StringBuilder(DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            foreach (var l in lookups)
            {
                sb.Append(',');
                if (l.TryGetValue(ts, out var v)) sb.Append(v.ToString("0.###", CultureInfo.InvariantCulture));
            }
            writer.WriteLine(sb.ToString());
        }
    }

    // ------------------------------------------------------------------ CSV (frames)
    public static void WriteFramesCsv(TextWriter writer, string application, int processId, IReadOnlyList<FrameSample> frames, FrameStatistics stats)
    {
        var inv = CultureInfo.InvariantCulture;
        writer.WriteLine($"# OmniSpec frame capture — {Csv(application)} (PID {processId}) — source: Intel PresentMon");
        writer.WriteLine(string.Create(inv, $"# Frames,{stats.FrameCount},Duration s,{stats.DurationSeconds:0.###},Avg FPS,{stats.AverageFps:0.##},1% low,{stats.OnePercentLowFps:0.##},0.1% low,{stats.PointOnePercentLowFps:0.##},Min FPS,{stats.MinimumFps:0.##},Max FPS,{stats.MaximumFps:0.##}"));
        writer.WriteLine("TimeSeconds,FrameTimeMs,FPS,CpuBusyMs,CpuWaitMs,GpuTimeMs,GpuBusyMs,DisplayedTimeMs,DisplayLatencyMs,Dropped,PresentMode");
        static string N(double? v) => v is double d ? d.ToString("0.####", CultureInfo.InvariantCulture) : "";
        foreach (var f in frames)
        {
            writer.WriteLine(string.Join(",",
                N(f.TimeSeconds), N(f.FrameTimeMs), N(1000.0 / f.FrameTimeMs), N(f.CpuBusyMs), N(f.CpuWaitMs), N(f.GpuTimeMs),
                N(f.GpuBusyMs), N(f.DisplayedTimeMs), N(f.DisplayLatencyMs), f.Dropped switch { true => "1", false => "0", _ => "" },
                Csv(f.PresentMode ?? "")));
        }
    }

    // ------------------------------------------------------------------ JSON
    public static void WriteJsonReport(Stream stream, SystemReport report)
    {
        var doc = new
        {
            generator = $"OmniSpec {report.AppVersion}",
            generatedAt = report.GeneratedAt,
            note = "Values marked with a state other than 'Available' were not exposed by the hardware/OS. Nothing in this report is estimated unless labelled.",
            sections = report.SystemInfo.Sections.Select(s => SectionDto(s, report.IncludeSensitive)),
            drives = report.Drives.Select(d => new
            {
                d.Drive.DeviceId,
                d.Drive.Model,
                d.Drive.Manufacturer,
                SerialNumber = report.IncludeSensitive ? d.Drive.SerialNumber : null,
                d.Drive.FirmwareVersion,
                d.Drive.BusType,
                d.Drive.MediaType,
                d.Drive.SizeBytes,
                d.Drive.WindowsHealthStatus,
                d.Drive.OperationalStatus,
                d.Drive.PartitionStyle,
                Partitions = d.Drive.Partitions,
                Reliability = d.Drive.Reliability,
                SmartSensors = d.Drive.SmartSensors.Select(s => new { s.Name, Kind = s.Kind.ToString(), s.Value, s.Unit }),
                Health = new
                {
                    Level = d.Health.Level.ToString(),
                    Findings = d.Health.Findings,
                    d.Health.OfficialStatus,
                    d.Health.DeviceReportedWear,
                    d.Health.EstimatedRemainingLife,
                    d.Health.Temperature,
                    d.Health.PowerOnHours,
                    d.Health.PowerCycles,
                    d.Health.HostReadsGb,
                    d.Health.HostWritesGb,
                },
            }),
            liveSnapshot = report.Live is null ? null : new
            {
                report.Live.Timestamp,
                cpuLoad = report.Live.CpuUsage?.TotalLoad,
                cpu = report.Live.Cpu,
                gpus = report.Live.Gpus,
                memory = report.Live.Memory,
                fans = report.Live.Fans,
                battery = report.Live.Battery,
                sensors = report.Live.Hardware.Devices.Select(d => new
                {
                    d.Name,
                    Kind = d.Kind.ToString(),
                    Sensors = d.Sensors.Select(s => new { s.Name, Kind = s.Kind.ToString(), s.Value, s.Min, s.Max, s.Unit }),
                }),
                source = report.Live.Hardware.Source,
            },
        };
        JsonSerializer.Serialize(stream, doc, Json);
    }

    private static object SectionDto(InfoSection s, bool sensitive) => new
    {
        s.Title,
        Items = s.Items.Where(i => sensitive || !i.IsSensitive).Select(i => new { i.Label, Value = i.State == MetricState.Available ? i.Value : null, State = i.State.ToString(), Note = i.State == MetricState.Available ? null : i.Value, i.Source }),
        Children = s.Children?.Select(c => SectionDto(c, sensitive)),
    };

    // ------------------------------------------------------------------ TXT
    public static void WriteTextReport(TextWriter w, SystemReport report)
    {
        w.WriteLine("==============================================================");
        w.WriteLine($" OmniSpec System Report — generated {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine($" OmniSpec version {report.AppVersion}");
        w.WriteLine("==============================================================");
        w.WriteLine();
        foreach (var section in report.SystemInfo.Sections) WriteSection(w, section, report.IncludeSensitive, 0);

        w.WriteLine("[Storage]");
        foreach (var (drive, health) in report.Drives)
        {
            w.WriteLine($"  Disk {drive.DeviceId}: {drive.Model}");
            Line(w, 4, "Bus / media", $"{drive.BusType} / {drive.MediaType}");
            Line(w, 4, "Capacity", BytesText(drive.SizeBytes));
            Line(w, 4, "Firmware", drive.FirmwareVersion ?? "Unavailable");
            if (report.IncludeSensitive) Line(w, 4, "Serial", drive.SerialNumber ?? "Unavailable");
            Line(w, 4, "Health verdict", DriveHealthEvaluator.Describe(health.Level));
            Line(w, 4, "Windows health status", MetricValue(health.OfficialStatus));
            Line(w, 4, "Endurance consumed", MetricNum(health.DeviceReportedWear, "0", "%"));
            Line(w, 4, "Estimated remaining life", MetricNum(health.EstimatedRemainingLife, "0", "% (estimate)"));
            Line(w, 4, "Temperature", MetricNum(health.Temperature, "0", "°C"));
            Line(w, 4, "Power-on hours", MetricNum(health.PowerOnHours, "0", "h"));
            foreach (var f in health.Findings) w.WriteLine($"      - [{f.Severity}] {f.Message} (source: {f.Source})");
            foreach (var p in drive.Partitions)
                w.WriteLine($"      Partition {p.PartitionNumber} {p.DriveLetter} {p.Label} {p.FileSystem} {BytesText(p.SizeBytes)}{(p.FreeBytes is ulong fr ? $", {BytesText(fr)} free" : "")}".TrimEnd());
        }
        w.WriteLine();

        if (report.Live is { } live)
        {
            w.WriteLine($"[Live snapshot @ {live.Timestamp:HH:mm:ss}]");
            Line(w, 2, "CPU load", MetricNum(live.CpuUsage?.TotalLoad ?? Metric<double>.Unavailable(""), "0", "%"));
            Line(w, 2, "CPU package temperature", MetricNum(live.Cpu.PackageTemperature, "0", "°C"));
            Line(w, 2, "CPU package power", MetricNum(live.Cpu.PackagePower, "0.0", "W"));
            foreach (var g in live.Gpus)
            {
                Line(w, 2, $"{g.Name} load", MetricNum(g.Load, "0", "%"));
                Line(w, 2, $"{g.Name} temperature", MetricNum(g.Temperature, "0", "°C"));
            }
            if (live.Memory is { } m) Line(w, 2, "Memory in use", $"{BytesText(m.UsedPhysical)} of {BytesText(m.TotalPhysical)} ({m.UsedPercent:0}%)");
            w.WriteLine();
            w.WriteLine("[All sensors]");
            foreach (var d in live.Hardware.Devices.Where(d => d.Sensors.Count > 0))
            {
                w.WriteLine($"  {d.Name} ({d.Kind})");
                foreach (var s in d.Sensors)
                    Line(w, 4, $"{s.Kind}: {s.Name}", s.Value is double v ? $"{v.ToString("0.##", CultureInfo.InvariantCulture)} {s.Unit}" : "Unavailable");
            }
        }
    }

    private static void WriteSection(TextWriter w, InfoSection s, bool sensitive, int depth)
    {
        var pad = new string(' ', depth * 2);
        w.WriteLine(depth == 0 ? $"[{s.Title}]" : $"{pad}{s.Title}");
        foreach (var i in s.Items.Where(i => sensitive || !i.IsSensitive))
            Line(w, depth * 2 + 2, i.Label, i.DisplayValue(sensitive));
        if (s.Children is not null)
            foreach (var c in s.Children) WriteSection(w, c, sensitive, depth + 1);
        if (depth == 0) w.WriteLine();
    }

    private static void Line(TextWriter w, int indent, string label, string value) =>
        w.WriteLine($"{new string(' ', indent)}{(label + ":").PadRight(Math.Max(1, 34 - indent))} {value}");

    private static string MetricValue(Metric<string> m) => m.HasValue ? m.Value! : MetricText.ForState(m.State);
    private static string MetricNum(Metric<double> m, string format, string unit) => m.Format(format, unit);

    public static string BytesText(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{v:0} {units[u]}" : $"{v:0.##} {units[u]}";
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
