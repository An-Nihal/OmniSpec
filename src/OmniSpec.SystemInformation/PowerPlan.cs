using System.Runtime.InteropServices;

namespace OmniSpec.SystemInformation;

/// <summary>Active power plan name via powrprof.dll (Win32_PowerPlan is empty on recent Windows builds).</summary>
public static partial class PowerPlan
{
    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadFriendlyName(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroup, IntPtr setting, IntPtr buffer, ref uint bufferSize);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);

    public static string? ActiveName()
    {
        IntPtr guid = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out guid) != 0 || guid == IntPtr.Zero) return null;
            uint size = 0;
            PowerReadFriendlyName(IntPtr.Zero, guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            if (size == 0) return null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PowerReadFriendlyName(IntPtr.Zero, guid, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return null;
                var name = Marshal.PtrToStringUni(buffer);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (guid != IntPtr.Zero) LocalFree(guid);
        }
    }
}
