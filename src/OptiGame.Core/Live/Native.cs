using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OptiGame.Core.Live;

/// <summary>All P/Invoke surface in one place.</summary>
public static class Native
{
    // ---- access rights ----
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_SET_QUOTA = 0x0100;
    public const uint PROCESS_ALL_NEEDED = PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA;

    public const uint IDLE = 0x40, BELOW_NORMAL = 0x4000, NORMAL = 0x20, ABOVE_NORMAL = 0x8000, HIGH = 0x80;

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] public static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetPriorityClass(IntPtr h, uint cls);
    [DllImport("psapi.dll", SetLastError = true)] public static extern bool EmptyWorkingSet(IntPtr h);

    // ---- EcoQoS (efficiency mode) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(IntPtr h, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);
    public const int ProcessPowerThrottling = 4;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 1;

    public static string? GetImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    // ---- DWM (Mica, dark title bar, rounded corners) ----
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int Left, Right, Top, Bottom; }
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // ---- memory / cpu / power ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [StructLayout(LayoutKind.Sequential)] public struct FILETIME { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    [DllImport("powrprof.dll")] public static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
    [DllImport("powrprof.dll")] public static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr p);

    public static Guid? GetActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(p); } finally { LocalFree(p); }
    }

    // ---- hybrid CPU topology (P-core / E-core) ----
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr info, uint length, out uint returned, IntPtr process, uint flags);

    /// <summary>Returns (efficiencyClass, coreIndex) for every logical processor.</summary>
    public static List<(byte eff, byte core)> GetCpuSets()
    {
        var list = new List<(byte, byte)>();
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint need, IntPtr.Zero, 0);
        if (need == 0) return list;
        IntPtr buf = Marshal.AllocHGlobal((int)need);
        try
        {
            if (!GetSystemCpuSetInformation(buf, need, out uint got, IntPtr.Zero, 0)) return list;
            int off = 0;
            while (off < got)
            {
                int size = Marshal.ReadInt32(buf, off);
                int type = Marshal.ReadInt32(buf, off + 4);
                if (size <= 0) break;
                if (type == 0) list.Add((Marshal.ReadByte(buf, off + 18), Marshal.ReadByte(buf, off + 15)));
                off += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    // ---- foreground / fullscreen detection ----
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    public static bool IsFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return false;
        IntPtr mon = MonitorFromWindow(hwnd, 2 /* nearest */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    // ---- polite close for windowless/tray apps ----
    public delegate bool EnumThreadWndProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumThreadWindows(uint threadId, EnumThreadWndProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    public const uint WM_CLOSE = 0x0010;

    // ---- standby list purge (needs administrator) ----
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int cls, ref int info, int len);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? sys, string name, out long luid);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct TOKEN_PRIVILEGES { public uint Count; public long Luid; public uint Attr; }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES tp, uint len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    public static bool PurgeStandbyList()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x28 /* ADJUST|QUERY */, out IntPtr tok)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out long luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attr = 2 };
            if (!AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            int cmd = 4; // MemoryPurgeStandbyList
            return NtSetSystemInformation(80 /* SystemMemoryListInformation */, ref cmd, sizeof(int)) == 0;
        }
        finally { CloseHandle(tok); }
    }
}
