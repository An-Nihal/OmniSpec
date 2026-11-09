using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace OmniSpec.App.Services;

/// <summary>
/// Optional "always run as administrator without a UAC prompt" mode.
/// <para>
/// With the user's one-time consent (installer checkbox or Settings toggle, both of which require one UAC prompt),
/// OmniSpec registers a Windows scheduled task configured to "run with highest privileges". Normal launches then start
/// that task instead of elevating, so no prompt appears on later starts. This is the standard Windows mechanism used by
/// monitoring tools; it is not a UAC bypass and can be removed at any time (Settings or uninstall).
/// </para>
/// <para>
/// Security: an auto-elevating task must never point at a file that a standard user can replace, otherwise any program
/// could gain administrator rights. Registration is therefore only allowed when OmniSpec.exe lives under
/// Program Files, which only administrators can modify.
/// </para>
/// </summary>
public static class ElevatedLaunch
{
    public const string TaskName = @"\OmniSpec\OmniSpec (administrator)";
    public const string FromTaskArg = "--from-task";
    public const string RegisterArg = "--register-elevated-task";
    public const string UnregisterArg = "--unregister-elevated-task";

    public static string? ExePath => Environment.ProcessPath;

    /// <summary>True when the executable is in a location only administrators can write to.</summary>
    public static bool IsInProtectedLocation(out string reason)
    {
        var exe = ExePath;
        if (exe is null) { reason = "The executable path is unknown."; return false; }
        var full = Path.GetFullPath(exe);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }.Where(r => !string.IsNullOrEmpty(r)).Select(r => Path.GetFullPath(r).TrimEnd('\\') + "\\");

        if (roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
        {
            reason = string.Empty;
            return true;
        }
        reason = "For security, automatic administrator mode is only available when OmniSpec is installed under Program Files " +
                 "(use the installer). A portable copy in a user-writable folder could be replaced by other programs, " +
                 "which would then run with administrator rights.";
        return false;
    }

    /// <summary>True if the task exists and points at this executable.</summary>
    public static bool IsRegistered()
    {
        var xml = RunSchtasks($"/query /tn \"{TaskName}\" /xml", out var exitCode);
        return exitCode == 0 && ExePath is { } exe && xml.Contains(SecurityElement.Escape(exe), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates or replaces the task. Must be called from an elevated process.</summary>
    public static bool Register(out string error)
    {
        error = string.Empty;
        if (!Elevation.IsElevated) { error = "Administrator rights are required to register the task."; return false; }
        if (!IsInProtectedLocation(out var reason)) { error = reason; return false; }

        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (sid is null) { error = "Could not determine the current user."; return false; }

        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts OmniSpec with administrator rights without a UAC prompt, so all hardware sensors can be read. Created at the user's request; remove it in OmniSpec Settings or by uninstalling OmniSpec.</Description>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(ExePath)}</Command>
                  <Arguments>{FromTaskArg}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"omnispec-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            var output = RunSchtasks($"/create /tn \"{TaskName}\" /xml \"{file}\" /f", out var exitCode);
            if (exitCode != 0) { error = output.Trim(); return false; }
            return true;
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp file */ }
        }
    }

    /// <summary>Deletes the task. If that requires elevation, asks Windows to run the deletion elevated (one UAC prompt).</summary>
    public static bool Unregister()
    {
        RunSchtasks($"/delete /tn \"{TaskName}\" /f", out var exitCode);
        if (exitCode == 0 || !IsRegistered()) return true;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/delete /tn \"{TaskName}\" /f")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit(15000);
            return !IsRegistered();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC cancelled
        }
    }

    /// <summary>Starts OmniSpec through the elevated task. Returns false if the task could not be started.</summary>
    public static bool LaunchViaTask()
    {
        RunSchtasks($"/run /tn \"{TaskName}\"", out var exitCode);
        return exitCode == 0;
    }

    private static string RunSchtasks(string arguments, out int exitCode)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            exitCode = p.HasExited ? p.ExitCode : -1;
            return stdout + stderr;
        }
        catch (Exception ex)
        {
            exitCode = -1;
            return ex.Message;
        }
    }
}
