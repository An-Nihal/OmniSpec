using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using OmniSpec.Core.Settings;

namespace OmniSpec.App.Services;

/// <summary>The game currently in the foreground, and the monitor it occupies (in physical pixels).</summary>
public sealed record GameWindow(int ProcessId, string ProcessName, IntPtr Handle, GameDetector.RECT Monitor, bool ExclusiveFullscreen);

/// <summary>
/// Detects a game in the foreground without touching the game process: it only inspects the foreground window's
/// size relative to its monitor and the shell's "full-screen Direct3D app running" state. Nothing is injected,
/// so it is safe with anti-cheat systems.
/// </summary>
public static partial class GameDetector
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [LibraryImport("user32.dll")] private static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(IntPtr hWnd);
    [LibraryImport("user32.dll")] private static partial IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [LibraryImport("user32.dll")] private static partial IntPtr MonitorFromPoint(POINT pt, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [LibraryImport("shell32.dll")] private static partial int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private const uint MONITOR_DEFAULTTONEAREST = 2, MONITOR_DEFAULTTOPRIMARY = 1;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    private static readonly Dictionary<int, string> NameCache = new();

    public static GameWindow? Detect(OverlaySettings settings)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        var name = ProcessName(pid);
        if (name is null) return null;

        var exclusive = SHQueryUserNotificationState(out var state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN;
        var monitor = MonitorRect(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
        var covers = GetWindowRect(hwnd, out var r) && r.Left <= monitor.Left && r.Top <= monitor.Top && r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;

        return GameRules.IsGame(name, covers || exclusive, settings.ExcludedApps, settings.AlwaysGames)
            ? new GameWindow(pid, name, hwnd, monitor, exclusive)
            : null;
    }

    /// <summary>Process id of the foreground window (0 if none).</summary>
    public static int ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    public static RECT PrimaryMonitor() => MonitorRect(MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY));

    private static RECT MonitorRect(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? info.rcMonitor : default;
    }

    private static string? ProcessName(int pid)
    {
        if (NameCache.TryGetValue(pid, out var cached)) return cached;
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName;
            if (NameCache.Count > 512) NameCache.Clear();
            NameCache[pid] = name;
            return name;
        }
        catch { return null; }
    }
}

/// <summary>System-wide hotkey via RegisterHotKey on a hidden message window.</summary>
public sealed partial class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HotkeyId = 0x4F53; // "OS"

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private bool _registered;

    public GlobalHotkey()
    {
        _source = new HwndSource(new HwndSourceParameters("OmniSpecHotkey") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    public event EventHandler? Pressed;

    /// <summary>Registers the hotkey, replacing any previous one. Returns false if another app already owns it.</summary>
    public bool Register(string text)
    {
        Unregister();
        if (!HotkeyParser.TryParse(text, out var hk)) return false;
        _registered = RegisterHotKey(_source.Handle, HotkeyId, hk.Modifiers | MOD_NOREPEAT, hk.VirtualKey);
        return _registered;
    }

    private void Unregister()
    {
        if (_registered) UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.Dispose();
    }
}
