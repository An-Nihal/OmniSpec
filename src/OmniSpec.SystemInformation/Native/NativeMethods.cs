using System.Runtime.InteropServices;

namespace OmniSpec.SystemInformation.Native;

internal static partial class NativeMethods
{
    // ---------------- Firmware ----------------
    public enum FirmwareType { Unknown = 0, Bios = 1, Uefi = 2, Max = 3 }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFirmwareType(out FirmwareType firmwareType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsProcessorFeaturePresent(uint processorFeature);

    public const uint PF_NX_ENABLED = 12;
    public const uint PF_SECOND_LEVEL_ADDRESS_TRANSLATION = 20;
    public const uint PF_VIRT_FIRMWARE_ENABLED = 21;

    // ---------------- Logical processor information ----------------
    public const int RelationProcessorCore = 0;
    public const int RelationCache = 2;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    public sealed record CoreInfo(int CoreIndex, byte EfficiencyClass, bool SmtEnabled, IReadOnlyList<(ushort Group, int Number)> LogicalProcessors);
    public sealed record CacheInfo(byte Level, int Type, uint SizeBytes, ushort LineSize, byte Associativity);

    public static IReadOnlyList<CoreInfo> GetCores()
    {
        var result = new List<CoreInfo>();
        ReadProcessorInfo(RelationProcessorCore, (ptr, _) =>
        {
            var flags = Marshal.ReadByte(ptr, 8);
            var efficiency = Marshal.ReadByte(ptr, 9);
            var groupCount = (ushort)Marshal.ReadInt16(ptr, 30);
            var lps = new List<(ushort, int)>();
            for (var g = 0; g < groupCount; g++)
            {
                var entry = 32 + g * 16;
                var mask = (ulong)Marshal.ReadInt64(ptr, entry);
                var group = (ushort)Marshal.ReadInt16(ptr, entry + 8);
                for (var bit = 0; bit < 64; bit++)
                    if ((mask & (1UL << bit)) != 0) lps.Add((group, bit));
            }
            result.Add(new CoreInfo(result.Count, efficiency, (flags & 1) != 0, lps));
        });
        return result;
    }

    public static IReadOnlyList<CacheInfo> GetCaches()
    {
        var result = new List<CacheInfo>();
        ReadProcessorInfo(RelationCache, (ptr, _) =>
        {
            result.Add(new CacheInfo(
                Marshal.ReadByte(ptr, 8),
                Marshal.ReadInt32(ptr, 16),
                (uint)Marshal.ReadInt32(ptr, 12),
                (ushort)Marshal.ReadInt16(ptr, 10),
                Marshal.ReadByte(ptr, 9)));
        });
        return result;
    }

    private static void ReadProcessorInfo(int relationship, Action<IntPtr, int> onEntry)
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(relationship, IntPtr.Zero, ref length);
        if (length == 0) return;
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(relationship, buffer, ref length)) return;
            var offset = 0;
            while (offset < length)
            {
                var ptr = buffer + offset;
                var rel = Marshal.ReadInt32(ptr, 0);
                var size = Marshal.ReadInt32(ptr, 4);
                if (size <= 0) break;
                if (rel == relationship) onEntry(ptr, size);
                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------------- Display configuration ----------------
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    public const int ERROR_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public int outputTechnology;
        public int rotation;
        public int scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public int scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public struct DISPLAYCONFIG_MODE_UNION
    {
        // DISPLAYCONFIG_SOURCE_MODE
        [FieldOffset(0)] public uint width;
        [FieldOffset(4)] public uint height;
        [FieldOffset(8)] public int pixelFormat;
        [FieldOffset(12)] public int positionX;
        [FieldOffset(16)] public int positionY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        public int infoType; // 1 = source, 2 = target, 3 = desktop image
        public uint id;
        public LUID adapterId;
        public DISPLAYCONFIG_MODE_UNION mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public int outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
        public int colorEncoding;
        public uint bitsPerColorChannel;
    }

    public const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    public const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    public static string OutputTechnologyName(int tech) => tech switch
    {
        -1 => "Other",
        0 => "VGA",
        1 => "S-Video",
        2 => "Composite",
        3 => "Component",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        8 => "D-Jpn",
        9 => "SDI",
        10 => "DisplayPort (external)",
        11 => "DisplayPort (embedded)",
        12 => "UDI (external)",
        13 => "UDI (embedded)",
        14 => "SDTV dongle",
        15 => "Miracast",
        16 => "Indirect (wired)",
        17 => "Indirect (virtual)",
        18 => "DisplayPort (USB tunnel)",
        unchecked((int)0x80000000) => "Internal",
        _ => $"Unknown ({tech})",
    };
}
