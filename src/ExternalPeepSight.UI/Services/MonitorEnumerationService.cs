using System.Runtime.InteropServices;

namespace ExternalPeepSight.UI.Services;

internal sealed record MonitorInfo(
    string Id,
    string DisplayName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary);

internal interface IMonitorEnumerationService
{
    public IReadOnlyList<MonitorInfo> Enumerate();
}

internal sealed class MonitorEnumerationService : IMonitorEnumerationService
{
    private const int MonitorInfoPrimary = 1;
    private const uint QueryOnlyActivePaths = 0x00000002;

    public IReadOnlyList<MonitorInfo> Enumerate()
    {
        Dictionary<string, string> devicePaths = QueryTargetPaths();
        var monitors = new List<MonitorInfo>();
        bool success = EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (monitor, _, _, _) =>
            {
                var information = new MonitorInfoEx
                {
                    Size = Marshal.SizeOf<MonitorInfoEx>(),
                    DeviceName = string.Empty,
                };
                if (!GetMonitorInfo(monitor, ref information))
                {
                    return false;
                }

                string id = MonitorIdentity.Create(
                    devicePaths.GetValueOrDefault(information.DeviceName.ToLowerInvariant(), string.Empty),
                    information.DeviceName,
                    information.Monitor.Left,
                    information.Monitor.Top,
                    information.Monitor.Right,
                    information.Monitor.Bottom);
                monitors.Add(
                    new MonitorInfo(
                        id,
                        information.DeviceName,
                        information.Monitor.Left,
                        information.Monitor.Top,
                        information.Monitor.Right - information.Monitor.Left,
                        information.Monitor.Bottom - information.Monitor.Top,
                        (information.Flags & MonitorInfoPrimary) != 0));
                return true;
            },
            IntPtr.Zero);
        if (!success)
        {
            return [];
        }

        return monitors
            .OrderBy(monitor => monitor.Y)
            .ThenBy(monitor => monitor.X)
            .ToArray();
    }

    private static Dictionary<string, string> QueryTargetPaths()
    {
        if (GetDisplayConfigBufferSizes(QueryOnlyActivePaths, out uint pathCount, out uint modeCount) != 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];
        if (QueryDisplayConfig(
                QueryOnlyActivePaths,
                ref pathCount,
                paths,
                ref modeCount,
                modes,
                IntPtr.Zero) != 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DisplayConfigPathInfo path in paths.Take((int)pathCount))
        {
            var source = new DisplayConfigSourceDeviceName
            {
                Header = new DisplayConfigDeviceInfoHeader
                {
                    Type = 1,
                    Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                    AdapterId = path.SourceInfo.AdapterId,
                    Id = path.SourceInfo.Id,
                },
                ViewGdiDeviceName = string.Empty,
            };
            var target = new DisplayConfigTargetDeviceName
            {
                Header = new DisplayConfigDeviceInfoHeader
                {
                    Type = 2,
                    Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                    AdapterId = path.TargetInfo.AdapterId,
                    Id = path.TargetInfo.Id,
                },
                MonitorFriendlyDeviceName = string.Empty,
                MonitorDevicePath = string.Empty,
            };
            if (DisplayConfigGetDeviceInfo(ref source) == 0 &&
                DisplayConfigGetDeviceInfo(ref target) == 0)
            {
                result[source.ViewGdiDeviceName.ToLowerInvariant()] = target.MonitorDevicePath;
            }
        }

        return result;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRectangle,
        MonitorEnumProcedure callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx information);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint pathCount,
        out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] DisplayConfigPathInfo[] paths,
        ref uint modeCount,
        [Out] DisplayConfigModeInfo[] modes,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName request);

    private delegate bool MonitorEnumProcedure(
        IntPtr monitor,
        IntPtr deviceContext,
        IntPtr monitorRectangle,
        IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rectangle Monitor;
        public Rectangle WorkArea;
        public int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        [FieldOffset(0)]
        public uint InfoType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }
}

internal static class MonitorIdentity
{
    public static string Create(
        string devicePath,
        string deviceName,
        int left,
        int top,
        int right,
        int bottom)
    {
        string source = !string.IsNullOrEmpty(devicePath)
            ? $"path:{devicePath.ToLowerInvariant()}"
            : !string.IsNullOrEmpty(deviceName)
                ? $"gdi:{deviceName.ToLowerInvariant()}"
                : $"bounds:{left},{top},{right},{bottom}";
        ulong hash = 14_695_981_039_346_656_037UL;
        foreach (char character in source)
        {
            uint code = character;
            for (int shift = 0; shift < 32; shift += 8)
            {
                hash ^= (code >> shift) & 0xFF;
                hash *= 1_099_511_628_211UL;
            }
        }

        return $"monitor-{hash:x16}";
    }
}
