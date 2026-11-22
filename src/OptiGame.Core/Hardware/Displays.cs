using System.Globalization;
using System.Runtime.InteropServices;

namespace OptiGame.Core.Hardware;

public readonly record struct DisplayMode(int Width, int Height, double Hz);

/// <summary>
/// One connected display. "Current" is the mode Windows is using right now; "max" values are what the
/// display supports, which can be higher than what is active (e.g. a 165 Hz panel running at 60 Hz).
/// </summary>
public sealed class DisplayInfo
{
    /// <summary>GDI name such as \\.\DISPLAY1. Stable while the display stays connected.</summary>
    public string DeviceName { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public bool IsPrimary { get; init; }
    public int CurrentWidth { get; init; }
    public int CurrentHeight { get; init; }
    /// <summary>Active refresh rate, fractional when Windows reports one (59.94, 143.98).</summary>
    public double CurrentHz { get; init; }
    public List<DisplayMode> Modes { get; init; } = new();

    public (int w, int h) Native => Modes.Count == 0 ? (CurrentWidth, CurrentHeight)
        : Modes.OrderByDescending(m => (long)m.Width * m.Height).Select(m => (m.Width, m.Height)).First();
    public double MaxHz => Modes.Count == 0 ? CurrentHz : Modes.Max(m => m.Hz);
    public double MaxHzAtCurrentResolution => Modes.Where(m => m.Width == CurrentWidth && m.Height == CurrentHeight)
        .Select(m => m.Hz).DefaultIfEmpty(CurrentHz).Max();

    /// <summary>Distinct resolutions the display supports, largest first.</summary>
    public IEnumerable<(int w, int h)> Resolutions => Modes.Select(m => (m.Width, m.Height)).Distinct()
        .OrderByDescending(r => (long)r.Width * r.Height);

    public bool Supports(int w, int h) => Modes.Count == 0 ? (w, h) == (CurrentWidth, CurrentHeight) : Modes.Any(m => m.Width == w && m.Height == h);

    /// <summary>Suggested automatic frame cap: 3 fps under the ACTIVE refresh rate (never the maximum supported one).</summary>
    public int AutoFpsCap => DisplayMath.AutoCap(CurrentHz);

    public string ActiveModeText => $"{CurrentWidth}x{CurrentHeight} at {DisplayMath.Hz(CurrentHz)} Hz";
    public string Label => $"{(FriendlyName.Length > 0 ? FriendlyName : DeviceName)}{(IsPrimary ? " (main)" : "")}: {ActiveModeText}";
    public override string ToString() => Label;
}

public static class DisplayMath
{
    public static int AutoCap(double activeHz) => Math.Max(20, (int)Math.Floor(activeHz) - 3);

    /// <summary>Formats a refresh rate without hiding fractions: 165, 59.94, 143.98.</summary>
    public static string Hz(double hz) => Math.Abs(hz - Math.Round(hz)) < 0.005
        ? Math.Round(hz).ToString(CultureInfo.InvariantCulture)
        : hz.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Reads connected displays. Never changes Windows display settings.</summary>
public static class DisplayDetector
{
    public static List<DisplayInfo> Detect()
    {
        var list = new List<DisplayInfo>();
        Dictionary<string, (double hz, string name)> exact;
        try { exact = ExactActiveModes(); }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException or ExternalException) { exact = new(); }

        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            var cur = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref cur)) continue;
            var modes = new List<DisplayMode>();
            for (int m = 0; ; m++)
            {
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettings(dd.DeviceName, m, ref dm)) break;
                if (dm.dmBitsPerPel is 0 or >= 24)
                    modes.Add(new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
            }
            double hz = cur.dmDisplayFrequency;
            string friendly = "";
            if (exact.TryGetValue(dd.DeviceName, out var ex))
            {
                if (ex.hz > 1 && Math.Abs(ex.hz - hz) < 1.0) hz = ex.hz; // keep 59.94 instead of 59
                friendly = ex.name;
            }
            list.Add(new DisplayInfo
            {
                DeviceName = dd.DeviceName,
                FriendlyName = friendly,
                IsPrimary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                CurrentWidth = cur.dmPelsWidth,
                CurrentHeight = cur.dmPelsHeight,
                CurrentHz = hz,
                Modes = modes.Distinct().ToList(),
            });
        }
        return list;
    }

    /// <summary>Exact (rational) active refresh rate and monitor name per GDI device, from QueryDisplayConfig.</summary>
    static Dictionary<string, (double hz, string name)> ExactActiveModes()
    {
        var result = new Dictionary<string, (double, string)>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return result;
        var paths = new DISPLAYCONFIG_PATH_INFO[np];
        var modes = new DISPLAYCONFIG_MODE_INFO[nm];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return result;
        for (int i = 0; i < np; i++)
        {
            var p = paths[i];
            var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            src.header.type = 1; // GET_SOURCE_NAME
            src.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            src.header.adapterId = p.sourceInfo.adapterId;
            src.header.id = p.sourceInfo.id;
            if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;
            var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            tgt.header.type = 2; // GET_TARGET_NAME
            tgt.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            tgt.header.adapterId = p.targetInfo.adapterId;
            tgt.header.id = p.targetInfo.id;
            string name = DisplayConfigGetDeviceInfo(ref tgt) == 0 ? tgt.monitorFriendlyDeviceName ?? "" : "";
            var r = p.targetInfo.refreshRate;
            double hz = r.Denominator == 0 ? 0 : r.Numerator / (double)r.Denominator;
            result[src.viewGdiDeviceName] = (hz, name);
        }
        return result;
    }

    // ------------------------------------------------------------------ interop
    const int ENUM_CURRENT_SETTINGS = -1;
    const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1, DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    const uint QDC_ONLY_ACTIVE_PATHS = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    // The mode union is 64 bytes plus its header; only its size matters here.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    struct DISPLAYCONFIG_MODE_INFO { public uint infoType, id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);
}
