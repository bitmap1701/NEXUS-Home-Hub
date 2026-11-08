using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NEXUSHomeHub.Core
{
    /// <summary>
    /// Physical-pixel work area of the monitor the Cockpit should live on.
    /// </summary>
    public sealed record TargetMonitor(
        string FriendlyName,
        string GdiDeviceName,
        int WorkLeft,
        int WorkTop,
        int WorkWidth,
        int WorkHeight,
        bool IsPrimary,
        string MatchedBy)
    {
        public override string ToString() =>
            $"{FriendlyName} ({GdiDeviceName}, {WorkWidth}x{WorkHeight}px at {WorkLeft},{WorkTop}, primary={IsPrimary}, match={MatchedBy})";
    }

    /// <summary>
    /// Resolves the Cockpit target monitor (MSI Optix G272 wired to the iGPU Intel UHD 730) by its EDID friendly name.
    /// WPF renders a window on the adapter that drives the monitor it sits on, so placing the Cockpit on the
    /// iGPU-connected G272 is what keeps the GTX 1660 free for the primary LG display.
    /// Resolution order: friendly-name match -> first non-primary monitor -> primary monitor.
    /// </summary>
    public static class MonitorLocator
    {
        /// <param name="keywords">Every keyword must appear in the EDID friendly name (TASK-06: ["MSI", "G272"]).</param>
        public static TargetMonitor? Find(IReadOnlyList<string> keywords)
        {
            Dictionary<string, string> friendlyNames = QueryFriendlyNames();
            List<MonitorEntry> monitors = EnumerateMonitors();
            if (monitors.Count == 0) return null;

            var wanted = new List<string>();
            foreach (string k in keywords)
            {
                if (!string.IsNullOrWhiteSpace(k)) wanted.Add(k.Trim());
            }
            if (wanted.Count > 0)
            {
                foreach (MonitorEntry m in monitors)
                {
                    if (friendlyNames.TryGetValue(m.Device, out string? name) &&
                        wanted.TrueForAll(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        return ToTarget(m, name, "FriendlyName");
                    }
                }
            }

            foreach (MonitorEntry m in monitors)
            {
                if (!m.IsPrimary) return ToTarget(m, NameOf(friendlyNames, m.Device), "FirstSecondary");
            }

            MonitorEntry primary = monitors.Find(m => m.IsPrimary) ?? monitors[0];
            return ToTarget(primary, NameOf(friendlyNames, primary.Device), "PrimaryFallback");
        }

        private static TargetMonitor ToTarget(MonitorEntry m, string friendlyName, string matchedBy) =>
            new(friendlyName, m.Device, m.Work.Left, m.Work.Top,
                m.Work.Right - m.Work.Left, m.Work.Bottom - m.Work.Top, m.IsPrimary, matchedBy);

        private static string NameOf(Dictionary<string, string> names, string device) =>
            names.TryGetValue(device, out string? name) ? name : "Unknown monitor";

        private sealed record MonitorEntry(string Device, RECT Work, bool IsPrimary);

        private static List<MonitorEntry> EnumerateMonitors()
        {
            var list = new List<MonitorEntry>();
            MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    list.Add(new MonitorEntry(info.szDevice ?? string.Empty, info.rcWork,
                        (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return list;
        }

        /// <summary>Maps GDI device names (\\.\DISPLAYn) to EDID friendly names (e.g. "MSI G272").</summary>
        private static Dictionary<string, string> QueryFriendlyNames()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
                    return map;

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                    return map;

                for (int i = 0; i < pathCount; i++)
                {
                    var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    source.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                    source.header.adapterId = paths[i].sourceInfo.adapterId;
                    source.header.id = paths[i].sourceInfo.id;
                    if (DisplayConfigGetDeviceInfo(ref source) != 0) continue;

                    var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                    target.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                    target.header.adapterId = paths[i].targetInfo.adapterId;
                    target.header.id = paths[i].targetInfo.id;
                    if (DisplayConfigGetDeviceInfo(ref target) != 0) continue;

                    if (!string.IsNullOrEmpty(source.viewGdiDeviceName) &&
                        !string.IsNullOrEmpty(target.monitorFriendlyDeviceName))
                    {
                        map[source.viewGdiDeviceName] = target.monitorFriendlyDeviceName;
                    }
                }
            }
            catch (Exception)
            {
                // DisplayConfig API unavailable: fall back to "first non-primary monitor".
            }
            return map;
        }

        // ---------------- Win32 interop ----------------
#pragma warning disable CS0649, CS0169 // fields are filled by the marshaler

        private const uint MONITORINFOF_PRIMARY = 0x1;
        private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        /// <summary>Opaque 64-byte DISPLAYCONFIG_MODE_INFO (contents unused, only the buffer is required).</summary>
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            [FieldOffset(0)] public uint infoType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

#pragma warning restore CS0649, CS0169
    }
}
