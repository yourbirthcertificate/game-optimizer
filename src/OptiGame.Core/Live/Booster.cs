using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace OptiGame.Core.Live;

public sealed class BoostPlan
{
    public GameHit? Game { get; set; }
    public List<AppGroup> Trim { get; } = new();
    public List<AppGroup> Throttle { get; } = new();
    public List<AppGroup> Close { get; } = new();
    public bool ApplyPowerPlan { get; set; }
}

/// <summary>
/// Applies and reverts a boost. Everything except closing apps is reversible:
/// priorities and efficiency mode are restored and the power plan is switched back when the game ends.
/// </summary>
public sealed class Booster
{
    public static readonly Guid HighPerf = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid Saver = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    readonly LiveStore _store;
    readonly List<(int pid, uint prio)> _prio = new();
    readonly List<int> _eco = new();
    readonly object _lock = new();
    Guid? _prevPlan;

    public bool Active { get; private set; }
    public string? GameName { get; private set; }
    public BoostResult? Last { get; private set; }
    public event Action? Changed;

    public Booster(LiveStore store)
    {
        _store = store;
        // Crash recovery: if we switched the power plan and died before restoring, put it back now.
        if (store.Data.PendingPowerRestore is Guid g)
        {
            Native.PowerSetActiveScheme(IntPtr.Zero, ref g);
            store.Data.PendingPowerRestore = null; store.Save();
        }
    }

    public static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public async Task<BoostResult> BoostAsync(BoostPlan plan)
    {
        var s = _store.Data.Settings;
        var res = new BoostResult();
        double avail0 = LiveStats.Mem().AvailMB;

        // 1. Close apps politely (the only step that can lose unsaved work, and only ones the user ticked).
        if (plan.Close.Count > 0)
        {
            var closed = _store.Data.LastClosed; closed.Clear();
            var tasks = plan.Close.Select(g => CloseGroupAsync(g, s.ForceClose)).ToList();
            var outcomes = await Task.WhenAll(tasks);
            for (int i = 0; i < plan.Close.Count; i++)
            {
                var g = plan.Close[i];
                if (outcomes[i])
                {
                    res.Closed.Add(g.Name);
                    if (!string.IsNullOrEmpty(g.Path) && closed.All(c => !c.Path.Equals(g.Path, StringComparison.OrdinalIgnoreCase)))
                        closed.Add(new ClosedApp { Name = g.Name, Path = g.Path });
                }
                else res.Stubborn.Add(g.Name);
            }
            _store.Save();
        }

        // 2. Move background apps to efficiency mode + below-normal priority.
        if (s.ThrottleBackground)
        {
            var gamePids = new HashSet<int>(plan.Game?.Pids ?? new List<int>());
            foreach (var g in plan.Throttle)
                foreach (int pid in g.Pids)
                    if (!gamePids.Contains(pid) && Throttle(pid)) res.Throttled++;
        }

        // 3. Raise the game's priority (High, never Realtime).
        if (s.RaiseGamePriority && plan.Game != null)
            foreach (int pid in plan.Game.Pids)
                if (SetPriority(pid, Native.HIGH)) res.PriorityRaised = true;

        // 4. Trim working sets of everything that is not the game: pushes idle pages out of RAM, reversible by nature.
        if (s.TrimMemory)
            foreach (var g in plan.Trim)
                foreach (int pid in g.Pids)
                    if (Trim(pid)) res.Trimmed++;

        // 5. Power plan (only when the user clicked Boost or the one-click suggestion).
        if (plan.ApplyPowerPlan && s.SwitchPowerPlan)
        {
            var cur = Native.GetActiveScheme();
            if (cur != null && cur != HighPerf && cur != Ultimate)
            {
                _prevPlan = cur;
                _store.Data.PendingPowerRestore = cur; _store.Save();
                var hp = HighPerf;
                if (Native.PowerSetActiveScheme(IntPtr.Zero, ref hp) == 0) res.PlanNote = "switched to High performance";
                else { _prevPlan = null; _store.Data.PendingPowerRestore = null; }
            }
        }

        // 6. Deeper clean (administrator only, opt-in).
        if (s.PurgeStandby && IsAdmin() && Native.PurgeStandbyList()) res.StandbyPurged = true;

        await Task.Delay(900);
        res.FreedMB = Math.Max(0, LiveStats.Mem().AvailMB - avail0);

        _store.Data.BoostCount++; _store.Data.TotalFreedMB += res.FreedMB; _store.Save();
        lock (_lock) { Active = true; GameName = plan.Game?.Name; Last = res; }
        Changed?.Invoke();
        return res;
    }

    /// <summary>Undo priorities / efficiency mode / power plan, optionally re-open closed apps. Returns re-opened app names.</summary>
    public List<string> Restore(bool reopenApps)
    {
        var reopened = new List<string>();
        lock (_lock)
        {
            foreach (var (pid, prio) in _prio) SetPriorityRaw(pid, prio);
            foreach (int pid in _eco) SetEco(pid, null);
            _prio.Clear(); _eco.Clear();
            if (_prevPlan is Guid g)
            {
                Native.PowerSetActiveScheme(IntPtr.Zero, ref g);
                _prevPlan = null; _store.Data.PendingPowerRestore = null;
            }
            Active = false; GameName = null;
        }
        if (reopenApps)
        {
            foreach (var app in _store.Data.LastClosed.ToList())
            {
                try
                {
                    if (!File.Exists(app.Path)) continue;
                    string pn = Path.GetFileNameWithoutExtension(app.Path);
                    if (Process.GetProcessesByName(pn).Length > 0) continue;
                    Process.Start(new ProcessStartInfo(app.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app.Path) ?? "" });
                    reopened.Add(app.Name);
                }
                catch { }
            }
        }
        _store.Data.LastClosed.Clear();
        _store.Save();
        Changed?.Invoke();
        return reopened;
    }

    // ---------- helpers ----------
    static bool Trim(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return Native.EmptyWorkingSet(h); } finally { Native.CloseHandle(h); }
    }

    bool Throttle(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_ALL_NEEDED, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            uint orig = Native.GetPriorityClass(h);
            if (orig == 0) return false;
            bool ok = false;
            if (orig is Native.NORMAL or Native.ABOVE_NORMAL or Native.HIGH && Native.SetPriorityClass(h, Native.BELOW_NORMAL))
            {
                lock (_lock) _prio.Add((pid, orig));
                ok = true;
            }
            if (SetEcoHandle(h, true)) { lock (_lock) _eco.Add(pid); ok = true; }
            return ok;
        }
        finally { Native.CloseHandle(h); }
    }

    bool SetPriority(int pid, uint cls)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_ALL_NEEDED, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            uint orig = Native.GetPriorityClass(h);
            if (orig == 0 || orig == cls) return false;
            if (!Native.SetPriorityClass(h, cls)) return false;
            lock (_lock) _prio.Add((pid, orig));
            return true;
        }
        finally { Native.CloseHandle(h); }
    }

    static void SetPriorityRaw(int pid, uint cls)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_ALL_NEEDED, false, pid);
        if (h == IntPtr.Zero) return;
        try { Native.SetPriorityClass(h, cls); } finally { Native.CloseHandle(h); }
    }

    static bool SetEcoHandle(IntPtr h, bool on)
    {
        var st = new Native.PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,
            ControlMask = on ? Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            StateMask = on ? Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
        };
        return Native.SetProcessInformation(h, Native.ProcessPowerThrottling, ref st, System.Runtime.InteropServices.Marshal.SizeOf(st));
    }

    static void SetEco(int pid, bool? on)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_ALL_NEEDED, false, pid);
        if (h == IntPtr.Zero) return;
        try { SetEcoHandle(h, on == true); } finally { Native.CloseHandle(h); }
    }

    public static uint? ReadPriority(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try { return Native.GetPriorityClass(h); } finally { Native.CloseHandle(h); }
    }

    /// <summary>Polite close: window close, then WM_CLOSE to hidden windows (tray apps). Force-kill only if allowed.</summary>
    static async Task<bool> CloseGroupAsync(AppGroup g, bool force)
    {
        var procs = new List<Process>();
        foreach (int pid in g.Pids) { try { procs.Add(Process.GetProcessById(pid)); } catch { } }
        try
        {
            foreach (var p in procs) { try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { } }
            if (await WaitAllAsync(procs, 4000)) return true;

            foreach (var p in procs.Where(x => !Exited(x)))
            {
                try
                {
                    foreach (ProcessThread t in p.Threads)
                        Native.EnumThreadWindows((uint)t.Id, (h, _) => { Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero);
                }
                catch { }
            }
            if (await WaitAllAsync(procs, 3000)) return true;

            if (!force) return false;
            foreach (var p in procs.Where(x => !Exited(x))) { try { p.Kill(); } catch { } }
            return await WaitAllAsync(procs, 2000);
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    static bool Exited(Process p) { try { return p.HasExited; } catch { return true; } }

    static async Task<bool> WaitAllAsync(List<Process> procs, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (procs.All(Exited)) return true;
            await Task.Delay(200);
        }
        return procs.All(Exited);
    }
}
