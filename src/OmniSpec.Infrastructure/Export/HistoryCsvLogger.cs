using System.Globalization;
using Microsoft.Extensions.Logging;
using OmniSpec.Core.Live;
using OmniSpec.Core.Settings;

namespace OmniSpec.Infrastructure.Export;

/// <summary>
/// Optional on-disk history (disabled by default). Appends one CSV row every N seconds to history\history-YYYYMMDD.csv.
/// Files older than 30 days are removed.
/// </summary>
public sealed class HistoryCsvLogger
{
    private const string Header = "Timestamp,CpuLoad%,CpuTempC,CpuPowerW,RamUsed%,GpuLoad%,GpuTempC,GpuPowerW,VramUsedMB,NetDownBps,NetUpBps";
    private readonly string _directory;
    private readonly ISettingsService _settings;
    private readonly ILogger<HistoryCsvLogger> _logger;
    private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;
    private bool _cleaned;

    public HistoryCsvLogger(string directory, ISettingsService settings, ILogger<HistoryCsvLogger> logger)
    {
        _directory = directory;
        _settings = settings;
        _logger = logger;
    }

    public string Directory => _directory;

    public void OnLive(LiveSnapshot live)
    {
        var s = _settings.Current;
        if (!s.HistoryLoggingEnabled || live.IsMock) return;
        if (live.Timestamp - _lastWrite < TimeSpan.FromSeconds(s.HistoryLogIntervalSeconds)) return;
        _lastWrite = live.Timestamp;
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            if (!_cleaned) { Cleanup(); _cleaned = true; }
            var file = Path.Combine(_directory, $"history-{live.Timestamp:yyyyMMdd}.csv");
            var newFile = !File.Exists(file);
            using var w = new StreamWriter(file, append: true);
            if (newFile) w.WriteLine(Header);
            w.WriteLine(Row(live));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Writing history row failed");
        }
    }

    internal static string Row(LiveSnapshot live)
    {
        static string N(double? v) => v is double d && double.IsFinite(d) ? d.ToString("0.##", CultureInfo.InvariantCulture) : "";
        var gpu = live.Gpus.FirstOrDefault();
        var net = live.Network.Where(n => n.Status == "Up" && !n.IsVirtualOrLoopback).ToList();
        return string.Join(",",
            live.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            N(live.CpuUsage?.TotalLoad.HasValue == true ? live.CpuUsage.TotalLoad.Value : null),
            N(live.Cpu.PackageTemperature.HasValue ? live.Cpu.PackageTemperature.Value : null),
            N(live.Cpu.PackagePower.HasValue ? live.Cpu.PackagePower.Value : null),
            N(live.Memory?.UsedPercent),
            N(gpu?.Load.HasValue == true ? gpu.Load.Value : null),
            N(gpu?.Temperature.HasValue == true ? gpu.Temperature.Value : null),
            N(gpu?.Power.HasValue == true ? gpu.Power.Value : null),
            N(gpu?.DedicatedUsedMB.HasValue == true ? gpu.DedicatedUsedMB.Value : null),
            N(net.Sum(n => n.DownloadBytesPerSecond)),
            N(net.Sum(n => n.UploadBytesPerSecond)));
    }

    private void Cleanup()
    {
        foreach (var f in System.IO.Directory.EnumerateFiles(_directory, "history-*.csv"))
            if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30)) File.Delete(f);
    }
}
