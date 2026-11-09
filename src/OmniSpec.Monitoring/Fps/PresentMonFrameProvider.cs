using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using OmniSpec.Core.Fps;

namespace OmniSpec.Monitoring.Fps;

/// <summary>
/// Frame-timing provider that runs the Intel PresentMon console application (MIT licensed) and parses its CSV output
/// from stdout. OmniSpec never downloads or installs PresentMon or any service: the user supplies the executable
/// (Settings › FPS) or places it next to OmniSpec in a "tools" folder.
/// <para>
/// PresentMon uses ETW and needs either administrator rights or membership in the "Performance Log Users" group.
/// </para>
/// </summary>
public sealed class PresentMonFrameProvider : IFrameRateProvider
{
    public const string SessionName = "OmniSpec_PresentMon";
    private static readonly SecurityIdentifier PerformanceLogUsers = new("S-1-5-32-559");

    private readonly ILogger<PresentMonFrameProvider> _logger;
    private readonly Func<string?> _configuredPath;
    private readonly Func<string> _extraArgs;
    private readonly object _gate = new();
    private Process? _process;
    private CancellationTokenSource? _cts;
    private string _lastError = string.Empty;

    public PresentMonFrameProvider(ILogger<PresentMonFrameProvider> logger, Func<string?> configuredPath, Func<string> extraArgs)
    {
        _logger = logger;
        _configuredPath = configuredPath;
        _extraArgs = extraArgs;
    }

    public string Name => "Intel PresentMon (console)";
    public bool IsRunning { get { lock (_gate) return _process is { HasExited: false }; } }
    public int? TargetProcessId { get; private set; }

    public event EventHandler<IReadOnlyList<FrameSample>>? FramesReceived;
    public event EventHandler<string>? Stopped;

    public FpsProviderAvailability CheckAvailability()
    {
        var exe = LocateExecutable();
        if (exe is null)
        {
            return new FpsProviderAvailability(FpsProviderState.NotInstalled,
                "PresentMon was not found. Download the PresentMon console application from https://github.com/GameTechDev/PresentMon/releases " +
                "and select it in Settings › FPS & Gaming, or place it in a 'tools' folder next to OmniSpec.", null);
        }
        if (IsRunning) return new FpsProviderAvailability(FpsProviderState.Running, "Capturing frames", exe);
        if (!CanUseEtw())
        {
            return new FpsProviderAvailability(FpsProviderState.PermissionRequired,
                "PresentMon needs administrator rights or membership in the 'Performance Log Users' group to capture frame events. " +
                "Restart OmniSpec as administrator, or add your account to that group and sign in again.", exe);
        }
        return new FpsProviderAvailability(FpsProviderState.Ready, "Ready", exe);
    }

    public static bool CanUseEtw()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            return principal.IsInRole(WindowsBuiltInRole.Administrator) || principal.IsInRole(PerformanceLogUsers);
        }
        catch { return false; }
    }

    public string? LocateExecutable()
    {
        var configured = _configuredPath();
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var baseDir = AppContext.BaseDirectory;
        foreach (var dir in new[] { Path.Combine(baseDir, "tools"), baseDir })
        {
            if (!Directory.Exists(dir)) continue;
            var hit = Directory.EnumerateFiles(dir, "PresentMon*.exe").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (hit is not null) return hit;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var hit = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "PresentMon*.exe").FirstOrDefault() : null;
                if (hit is not null) return hit;
            }
            catch { /* inaccessible PATH entry */ }
        }
        return null;
    }

    public void Start(int processId)
    {
        Stop();
        var exe = LocateExecutable() ?? throw new InvalidOperationException("PresentMon executable not found.");
        var args = $"--process_id {processId} --output_stdout --stop_existing_session --session_name {SessionName} --terminate_on_proc_exit --no_console_stats {_extraArgs()}".Trim();

        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("PresentMon failed to start.");
        _logger.LogInformation("Started PresentMon for PID {Pid}: {Exe} {Args}", processId, exe, args);

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _process = process;
            _cts = cts;
            TargetProcessId = processId;
            _lastError = string.Empty;
        }

        _ = Task.Run(() => ReadErrorsAsync(process, cts.Token));
        _ = Task.Run(() => ReadFramesAsync(process, cts.Token));
    }

    private async Task ReadErrorsAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && await process.StandardError.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                _logger.LogDebug("PresentMon: {Line}", line);
                lock (_gate) _lastError = line.Trim();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "PresentMon stderr reader ended"); }
    }

    private async Task ReadFramesAsync(Process process, CancellationToken ct)
    {
        var parser = new PresentMonCsvParser();
        var batch = new List<FrameSample>(256);
        var lastFlush = Stopwatch.GetTimestamp();
        try
        {
            while (!ct.IsCancellationRequested && await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                var frame = parser.ParseLine(line);
                if (frame is not null) batch.Add(frame);
                if (batch.Count > 0 && Stopwatch.GetElapsedTime(lastFlush) > TimeSpan.FromMilliseconds(250))
                {
                    FramesReceived?.Invoke(this, batch.ToArray());
                    batch.Clear();
                    lastFlush = Stopwatch.GetTimestamp();
                }
            }
            if (batch.Count > 0) FramesReceived?.Invoke(this, batch.ToArray());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "PresentMon output reader failed"); }
        finally
        {
            string reason;
            lock (_gate)
            {
                reason = ct.IsCancellationRequested ? "Stopped by user"
                    : !string.IsNullOrEmpty(_lastError) ? $"PresentMon stopped: {_lastError}"
                    : !parser.HasHeader ? "PresentMon exited without producing frame data (check permissions and that the target uses DirectX/Vulkan/OpenGL)."
                    : "Target application exited";
                if (ReferenceEquals(_process, process)) { _process = null; TargetProcessId = null; }
            }
            Stopped?.Invoke(this, reason);
        }
    }

    public void Stop()
    {
        Process? process;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            process = _process;
            cts = _cts;
            _process = null;
            _cts = null;
            TargetProcessId = null;
        }
        cts?.Cancel();
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Stopping PresentMon"); }
        finally
        {
            process.Dispose();
            cts?.Dispose();
        }
    }

    public void Dispose() => Stop();
}
