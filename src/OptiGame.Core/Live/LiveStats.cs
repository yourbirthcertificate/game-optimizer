using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace OptiGame.Core.Live;

public sealed class GpuStats
{
    public string Name { get; set; } = "";
    public int UtilPct { get; set; }
    public double MemUsedMB { get; set; }
    public double MemTotalMB { get; set; }
    public int TempC { get; set; }
    public double PowerW { get; set; }
    public int MemPct => MemTotalMB > 0 ? (int)(MemUsedMB / MemTotalMB * 100) : 0;
}

public sealed class MemStats
{
    public double TotalMB, AvailMB;
    public double UsedMB => TotalMB - AvailMB;
    public int UsedPct => TotalMB > 0 ? (int)Math.Round(UsedMB / TotalMB * 100) : 0;
}

public sealed class PowerInfo
{
    public bool HasBattery, OnAc;
    public int Percent;
    public Guid? Plan;
    public string PlanName => LiveStats.PlanName(Plan);
    public bool PlanIsHigh => Plan == Booster.HighPerf || Plan == Booster.Ultimate;
}

/// <summary>Live readings: memory, CPU load, power state and (through nvidia-smi) the NVIDIA GPU.</summary>
public static class LiveStats
{
    public static MemStats Mem()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref m);
        return new MemStats { TotalMB = m.ullTotalPhys / 1048576.0, AvailMB = m.ullAvailPhys / 1048576.0 };
    }

    // ---- CPU load via GetSystemTimes deltas ----
    static ulong _idle, _kernel, _user; static bool _cpuInit;
    public static double CpuLoad()
    {
        if (!Native.GetSystemTimes(out var i, out var k, out var u)) return 0;
        double load = 0;
        if (_cpuInit)
        {
            ulong di = i.Value - _idle, dk = k.Value - _kernel, du = u.Value - _user;
            ulong total = dk + du;
            if (total > 0) load = (total - di) * 100.0 / total;
        }
        _idle = i.Value; _kernel = k.Value; _user = u.Value; _cpuInit = true;
        return Math.Clamp(load, 0, 100);
    }

    public static PowerInfo Power()
    {
        var info = new PowerInfo { Plan = Native.GetActiveScheme() };
        if (Native.GetSystemPowerStatus(out var s))
        {
            info.HasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255;
            info.OnAc = s.ACLineStatus == 1;
            info.Percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : 0;
        }
        return info;
    }

    public static string PlanName(Guid? g)
    {
        if (g == null) return "Unknown";
        if (g == Booster.HighPerf) return "High performance";
        if (g == Booster.Ultimate) return "Ultimate performance";
        if (g == Booster.Balanced) return "Balanced";
        if (g == Booster.Saver) return "Power saver";
        return "Custom plan";
    }

    // ---- NVIDIA GPU stats through nvidia-smi (ships with the driver) ----
    static string? _smi; static bool _smiSearched;
    static string? FindSmi()
    {
        if (_smiSearched) return _smi;
        _smiSearched = true;
        string[] c =
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
        };
        _smi = c.FirstOrDefault(File.Exists);
        return _smi;
    }
    public static bool HasNvidiaSmi => FindSmi() != null;

    public static GpuStats? Gpu()
    {
        string? exe = FindSmi();
        if (exe == null) return null;
        try
        {
            var psi = new ProcessStartInfo(exe, "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var pr = Process.Start(psi);
            if (pr == null) return null;
            string line = pr.StandardOutput.ReadLine() ?? "";
            if (!pr.WaitForExit(2500)) { try { pr.Kill(); } catch { } return null; }
            var f = line.Split(',').Select(x => x.Trim()).ToArray();
            if (f.Length < 5) return null;
            double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            return new GpuStats
            {
                Name = f[0].Replace("NVIDIA ", "").Replace("GeForce ", ""), UtilPct = (int)D(f[1]), MemUsedMB = D(f[2]), MemTotalMB = D(f[3]),
                TempC = (int)D(f[4]), PowerW = f.Length > 5 ? D(f[5]) : 0
            };
        }
        catch { return null; }
    }
}

/// <summary>Windows Graphics settings per app (HKCU\Software\Microsoft\DirectX\UserGpuPreferences).</summary>
public static class GpuPref
{
    const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public static int Get(string exePath)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            if (k?.GetValue(exePath) is string v)
                foreach (var part in v.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    if (part.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase) && int.TryParse(part[14..], out int n)) return n;
        }
        catch { }
        return 0; // let Windows decide
    }

    public static bool SetHighPerformance(string exePath)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            string cur = k.GetValue(exePath) as string ?? "";
            var parts = cur.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)).ToList();
            parts.Insert(0, "GpuPreference=2");
            k.SetValue(exePath, string.Join(';', parts) + ";", RegistryValueKind.String);
            return true;
        }
        catch { return false; }
    }

    public static void Remove(string exePath)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(Key, true); k?.DeleteValue(exePath, false); } catch { }
    }
}
