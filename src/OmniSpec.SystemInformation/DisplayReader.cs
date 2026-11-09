using System.Runtime.InteropServices;
using OmniSpec.Core.SystemInfo;
using static OmniSpec.SystemInformation.Native.NativeMethods;

namespace OmniSpec.SystemInformation;

/// <summary>Reads active displays through the Windows Display Configuration API (no elevation required).</summary>
public static class DisplayReader
{
    public static IReadOnlyList<DisplayInfo> ReadActiveDisplays()
    {
        var result = new List<DisplayInfo>();
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var numPaths, out var numModes) != ERROR_SUCCESS) return result;

        var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
        var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != ERROR_SUCCESS) return result;

        for (var i = 0; i < numPaths; i++)
        {
            var path = paths[i];

            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id,
                },
            };
            var monitorName = DisplayConfigGetDeviceInfo(ref target) == ERROR_SUCCESS && !string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName)
                ? target.monitorFriendlyDeviceName
                : (path.targetInfo.outputTechnology == unchecked((int)0x80000000) || path.targetInfo.outputTechnology == 11 ? "Built-in display" : "Generic display");

            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = path.sourceInfo.adapterId,
                    id = path.sourceInfo.id,
                },
            };
            var gdiName = DisplayConfigGetDeviceInfo(ref source) == ERROR_SUCCESS ? source.viewGdiDeviceName : string.Empty;

            var color = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id,
                },
            };
            bool? hdrSupported = null, hdrEnabled = null;
            int? bpc = null;
            if (DisplayConfigGetDeviceInfo(ref color) == ERROR_SUCCESS)
            {
                hdrSupported = (color.value & 0x1) != 0;
                hdrEnabled = (color.value & 0x2) != 0;
                bpc = color.bitsPerColorChannel > 0 ? (int)color.bitsPerColorChannel : null;
            }

            int? width = null, height = null;
            var srcIdx = path.sourceInfo.modeInfoIdx;
            if (srcIdx != 0xFFFFFFFF && srcIdx < numModes && modes[srcIdx].infoType == 1)
            {
                width = (int)modes[srcIdx].mode.width;
                height = (int)modes[srcIdx].mode.height;
            }

            double? refresh = path.targetInfo.refreshRate.Denominator > 0
                ? Math.Round((double)path.targetInfo.refreshRate.Numerator / path.targetInfo.refreshRate.Denominator, 2)
                : null;

            result.Add(new DisplayInfo(monitorName, gdiName, width, height, refresh, hdrSupported, hdrEnabled, bpc,
                OutputTechnologyName(path.targetInfo.outputTechnology)));
        }
        return result;
    }
}
