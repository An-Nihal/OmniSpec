using Microsoft.Extensions.Logging.Abstractions;
using OmniSpec.Core.Fps;
using OmniSpec.Monitoring.Fps;

namespace OmniSpec.Tests;

public class PresentMonParserTests
{
    [Fact]
    public void Parses_presentmon_v2_columns_by_header()
    {
        var p = new PresentMonCsvParser();
        Assert.Null(p.ParseLine("Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime"));
        var f = p.ParseLine("game.exe,1234,0x1,DXGI,0,0,1,Hardware: Independent Flip,Application,100.5,6.94,3.10,3.84,0.5,6.2,5.9,0.3,12.1,6.95");
        Assert.NotNull(f);
        Assert.Equal(6.94, f!.FrameTimeMs, 3);
        Assert.Equal(3.10, f.CpuBusyMs!.Value, 3);
        Assert.Equal(6.2, f.GpuTimeMs!.Value, 3);
        Assert.Equal(6.95, f.DisplayedTimeMs!.Value, 3);
        Assert.False(f.Dropped);
        Assert.Equal(1234, f.ProcessId);
        Assert.Equal("Hardware: Independent Flip", f.PresentMode);
    }

    [Fact]
    public void Na_displayed_time_marks_dropped_frame()
    {
        var p = new PresentMonCsvParser();
        p.ParseLine("Application,ProcessID,FrameTime,DisplayedTime");
        var f = p.ParseLine("game.exe,1,16.6,NA");
        Assert.True(f!.Dropped);
        Assert.Null(f.DisplayedTimeMs);
    }

    [Fact]
    public void Parses_presentmon_v1_columns()
    {
        var p = new PresentMonCsvParser();
        p.ParseLine("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsUntilRenderComplete,MsUntilDisplayed");
        var f = p.ParseLine("game.exe,99,0x2,DXGI,1,0,1,12.5,8.33,8.40,0.2,7.1,15.0");
        Assert.Equal(8.33, f!.FrameTimeMs, 3);
        Assert.True(f.Dropped);
        Assert.Equal(12.5, f.TimeSeconds, 3);
        Assert.Equal(15.0, f.DisplayLatencyMs!.Value, 3);
    }

    [Fact]
    public void Ignores_lines_before_header_and_garbage()
    {
        var p = new PresentMonCsvParser();
        Assert.Null(p.ParseLine("warning: something"));
        Assert.Null(p.ParseLine(""));
        p.ParseLine("Application,ProcessID,FrameTime");
        Assert.Null(p.ParseLine("game.exe,1,NA"));
        Assert.Null(p.ParseLine("game.exe,1,notanumber"));
    }

    [Fact]
    public void Csv_split_respects_quotes() =>
        Assert.Equal(["a", "b,c", "d"], PresentMonCsvParser.SplitCsv("a,\"b,c\",d"));
}

public class FrameStatisticsTests
{
    private static FrameSample F(double ms, bool? dropped = null) => new(double.NaN, ms, null, null, null, null, null, null, dropped, null, null, null);

    [Fact]
    public void Average_fps_is_frames_over_total_time()
    {
        // 50 frames at 10 ms + 50 frames at 30 ms = 2 s for 100 frames -> 50 FPS (not the mean of per-frame FPS, which would be 66.7).
        var frames = Enumerable.Repeat(F(10), 50).Concat(Enumerable.Repeat(F(30), 50)).ToList();
        var s = FrameStatisticsCalculator.Compute(frames);
        Assert.Equal(50, s.AverageFps, 3);
        Assert.Equal(2, s.DurationSeconds, 3);
        Assert.Equal(100, s.MaximumFps, 3);
        Assert.Equal(1000.0 / 30, s.MinimumFps, 3);
    }

    [Fact]
    public void One_percent_low_reflects_slowest_frames()
    {
        var frames = Enumerable.Repeat(F(10), 980).Concat(Enumerable.Repeat(F(50), 20)).ToList(); // 2% slow tail
        var s = FrameStatisticsCalculator.Compute(frames);
        Assert.Equal(1000.0 * 1000 / (980 * 10 + 20 * 50), s.AverageFps, 6); // 92.6 FPS
        Assert.Equal(20, s.OnePercentLowFps, 6);      // 99th percentile frame time = 50 ms
        Assert.True(s.PointOnePercentLowFps <= s.OnePercentLowFps);
        Assert.Equal(20, s.StutterFrames);
    }

    [Fact]
    public void Dropped_frames_are_counted_and_invalid_frames_ignored()
    {
        var s = FrameStatisticsCalculator.Compute([F(16.6, true), F(16.6, false), F(0), F(double.NaN)]);
        Assert.Equal(2, s.FrameCount);
        Assert.Equal(1, s.DroppedFrames);
    }

    [Fact]
    public void Empty_input_gives_empty_statistics() =>
        Assert.Equal(0, FrameStatisticsCalculator.Compute([]).FrameCount);

    [Fact]
    public void Percentile_interpolates()
    {
        Assert.Equal(5.5, FrameStatisticsCalculator.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 50), 6);
        Assert.Equal(10, FrameStatisticsCalculator.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 100), 6);
    }
}

public class FpsSessionTests
{
    [Fact]
    public void Session_builds_timeline_and_rolling_fps()
    {
        var session = new FpsSession(1, "game");
        var frames = Enumerable.Range(0, 240).Select(_ => new FrameSample(double.NaN, 1000.0 / 120, null, null, null, null, null, null, false, null, null, null)).ToList();
        session.Add(frames);
        Assert.Equal(240, session.FrameCount);
        Assert.InRange(session.CurrentFps!.Value, 119, 121);
        Assert.Equal(2.0, session.Frames()[^1].TimeSeconds, 3);
    }

    [Fact]
    public void Current_fps_is_null_without_data()
    {
        var session = new FpsSession(1, "game");
        Assert.Null(session.CurrentFps);
        Assert.Null(session.LastFrameTimeMs);
    }
}

public class FpsProviderAvailabilityTests
{
    [Fact]
    public void Missing_presentmon_is_reported_as_not_installed_with_instructions()
    {
        var provider = new PresentMonFrameProvider(NullLogger<PresentMonFrameProvider>.Instance, () => @"Z:\does\not\exist\PresentMon.exe", () => "");
        var located = provider.LocateExecutable();
        var availability = provider.CheckAvailability();
        if (located is null)
        {
            Assert.Equal(FpsProviderState.NotInstalled, availability.State);
            Assert.Contains("github.com/GameTechDev/PresentMon", availability.Message);
        }
        else
        {
            // A PresentMon happens to be on PATH on this machine.
            Assert.NotEqual(FpsProviderState.NotInstalled, availability.State);
        }
        Assert.False(provider.IsRunning);
    }

    [Fact]
    public void Configured_executable_is_used_when_present()
    {
        var dir = Directory.CreateTempSubdirectory("omnispec-pm-");
        var fake = Path.Combine(dir.FullName, "PresentMon-test-x64.exe");
        File.WriteAllBytes(fake, []);
        try
        {
            var provider = new PresentMonFrameProvider(NullLogger<PresentMonFrameProvider>.Instance, () => fake, () => "");
            var availability = provider.CheckAvailability();
            Assert.Equal(fake, availability.ExecutablePath);
            Assert.Contains(availability.State, new[] { FpsProviderState.Ready, FpsProviderState.PermissionRequired });
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

public class FpsSessionAverageTests
{
    [Fact]
    public void Average_fps_covers_the_whole_session()
    {
        var session = new FpsSession(1, "game");
        Assert.Null(session.AverageFps);
        FrameSample F(double ms) => new(double.NaN, ms, null, null, null, null, null, null, false, null, null, null);
        session.Add(Enumerable.Repeat(F(10), 100).ToList());   // 100 FPS for 1 s
        session.Add(Enumerable.Repeat(F(20), 50).ToList());    // 50 FPS for 1 s
        Assert.Equal(75, session.AverageFps!.Value, 6);        // 150 frames / 2 s
    }
}
