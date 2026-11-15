using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameOptimizer.Core.Hardware;

public static partial class HardwareDetector
{
    public static HardwareInfo Detect()
    {
        var hw = new HardwareInfo();
        TryWmi(hw);
        TryVram(hw);
        (hw.NativeWidth, hw.NativeHeight, hw.MaxRefreshHz) = Display.Native();
        Classify(hw);
        return hw;
    }

    static void TryWmi(HardwareInfo hw)
    {
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
                foreach (var o in s.Get())
                {
                    hw.Cpu = (o["Name"] as string ?? hw.Cpu).Trim();
                    hw.CpuCores = Convert.ToInt32(o["NumberOfCores"] ?? 0);
                    hw.CpuThreads = Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
                    break;
                }
            using (var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory, Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem"))
                foreach (var o in s.Get())
                {
                    hw.RamGb = Math.Round(Convert.ToDouble(o["TotalPhysicalMemory"] ?? 0) / (1L << 30), 1);
                    hw.Model = $"{o["Manufacturer"]} {o["Model"]}".Trim();
                    hw.IsLaptop = Convert.ToInt32(o["PCSystemType"] ?? 0) == 2;
                    break;
                }
            var gpus = new List<string>();
            using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                foreach (var o in s.Get())
                    if (o["Name"] is string n && n.Length > 0) gpus.Add(n.Trim());
            // Laptops list the integrated GPU too: use the strongest adapter.
            gpus = gpus.OrderByDescending(GpuScore).ToList();
            if (gpus.Count > 0) { hw.Gpu = gpus[0]; hw.OtherGpus = gpus.Skip(1).ToList(); }
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException)
        {
            hw.Reasons.Add("Could not read WMI hardware info: " + e.Message);
        }
    }

    static void TryVram(HardwareInfo hw)
    {
        // WMI AdapterRAM is a 32-bit field and stops at 4 GB, so prefer nvidia-smi, then the driver's registry key.
        if (hw.Gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.total --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                var line = p.StandardOutput.ReadLine();
                p.WaitForExit(10000);
                if (double.TryParse(line?.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var mib))
                { hw.GpuVramGb = Math.Round(mib / 1024, 1); return; }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        using var root = Registry.LocalMachine.OpenSubKey(cls);
        if (root is null) return;
        foreach (var sub in root.GetSubKeyNames())
        {
            try
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string desc || !desc.Trim().Equals(hw.Gpu, StringComparison.OrdinalIgnoreCase)) continue;
                var v = k.GetValue("HardwareInformation.qwMemorySize");
                long bytes = v switch { long l => l, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), _ => 0 };
                if (bytes > 0) { hw.GpuVramGb = Math.Round(bytes / (double)(1L << 30), 1); return; }
            }
            catch (System.Security.SecurityException) { }
        }
    }

    /// <summary>Rough relative performance class. About 1 = entry discrete GPU, 2.5 = upper-mid laptop, 4+ = enthusiast.</summary>
    public static double GpuScore(string name)
    {
        var n = name.ToLowerInvariant();
        bool laptop = n.Contains("laptop") || n.Contains("mobile") || n.Contains("max-q");
        double? score = null;
        Match m;
        if ((m = RtxRegex().Match(n)).Success)
        {
            int gen = m.Groups[1].Value[0] - '0', cls = m.Groups[2].Value[0] - '0';
            score = ClassScore(cls) + gen switch { 2 => -1.0, 3 => 0.0, 4 => 0.6, 5 => 1.0, _ => 0.0 };
            if (m.Groups[3].Success) score += 0.5;
        }
        else if ((m = GtxRegex().Match(n)).Success)
            score = m.Groups[1].Value == "16" ? 0.3 : -0.3;
        else if ((m = RxRegex().Match(n)).Success)
        {
            int gen = m.Groups[1].Value[0] - '0', cls = m.Groups[2].Value[0] - '0';
            score = ClassScore(cls) + gen switch { 5 => -1.0, 6 => 0.0, 7 => 0.5, 9 => 1.0, _ => 0.0 };
            if (m.Groups[3].Success) score += 0.5;
        }
        else if (n.Contains("arc")) score = ArcHighRegex().IsMatch(n) ? 2.0 : 0.8;
        else if (n.Contains("intel") || n.Contains("radeon(tm) graphics") || n.Contains("radeon graphics") ||
                 n.Contains("vega") || n.Contains("microsoft basic")) score = -1.0;
        score ??= 1.0; // unknown discrete GPU: assume the middle of the road
        if (laptop) score -= 1.0;
        return score.Value;

        static double ClassScore(int cls) => cls switch { 5 => 1.0, 6 => 2.0, 7 => 3.0, 8 => 4.0, 9 => 5.0, _ => 1.0 };
    }

    public static void Classify(HardwareInfo hw)
    {
        double s = GpuScore(hw.Gpu);
        hw.Reasons.Add($"GPU class score {s:+0.0;-0.0} for {hw.Gpu}");
        long px = (long)hw.NativeWidth * hw.NativeHeight;
        if (px >= 3840L * 2160 * 9 / 10) { s -= 1.0; hw.Reasons.Add($"4K-class screen: {-1.0:0.0}"); }
        else if (px >= 2560L * 1440 * 9 / 10) { s -= 0.5; hw.Reasons.Add($"1440p-class screen: {-0.5:0.0}"); }
        if (hw.CpuThreads is > 0 and < 8) { s -= 0.5; hw.Reasons.Add($"{hw.CpuThreads} CPU threads: {-0.5:0.0}"); }
        hw.Score = Math.Round(s, 2);

        var step = s switch
        {
            < 0.0 => QualityStep.Low,
            < 0.75 => QualityStep.Medium,
            < 1.5 => QualityStep.Balanced,
            < 2.75 => QualityStep.High,
            < 3.75 => QualityStep.VeryHigh,
            _ => QualityStep.Ultra,
        };
        var cap = QualityStep.Ultra;
        if (hw.GpuVramGb is > 0 and <= 4.5) { cap = QualityStep.Balanced; hw.Reasons.Add($"{hw.GpuVramGb} GB VRAM: at most Balanced"); }
        else if (hw.GpuVramGb is > 0 and <= 6.5) { cap = QualityStep.High; hw.Reasons.Add($"{hw.GpuVramGb} GB VRAM: at most High"); }
        if (hw.RamGb is > 0 and < 12) { cap = (QualityStep)Math.Min((int)cap, (int)QualityStep.Balanced); hw.Reasons.Add($"{hw.RamGb} GB RAM: at most Balanced"); }
        hw.Recommended = (QualityStep)Math.Min((int)step, (int)cap);
    }

    [GeneratedRegex(@"rtx\s*(\d)0(\d)0\s*(ti|super)?")] private static partial Regex RtxRegex();
    [GeneratedRegex(@"gtx\s*(\d{2})\d0")] private static partial Regex GtxRegex();
    [GeneratedRegex(@"rx\s*(\d)(\d)\d0\s*(xtx|xt|gre)?")] private static partial Regex RxRegex();
    [GeneratedRegex(@"a7\d0|b5\d0|b7\d0")] private static partial Regex ArcHighRegex();
}

/// <summary>Primary display: native resolution and the best refresh rate at that resolution.</summary>
internal static class Display
{
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);

    public static (int w, int h, int hz) Native()
    {
        var modes = new List<(int w, int h, int hz)>();
        for (int i = 0; ; i++)
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(null, i, ref dm)) break;
            modes.Add((dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
        }
        if (modes.Count == 0) return (1920, 1080, 60);
        var native = modes.MaxBy(m => (long)m.w * m.h);
        int hz = modes.Where(m => m.w == native.w && m.h == native.h).Max(m => m.hz);
        return (native.w, native.h, hz);
    }
}
