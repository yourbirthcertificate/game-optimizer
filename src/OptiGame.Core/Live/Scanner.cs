using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OptiGame.Core.Live;

/// <summary>Snapshots running processes in the user's session, grouped by executable.</summary>
public sealed class Scanner
{
    readonly Dictionary<int, (DateTime at, TimeSpan cpu)> _prev = new();
    readonly Dictionary<string, string> _desc = new(StringComparer.OrdinalIgnoreCase);
    readonly int _cores = Environment.ProcessorCount;
    readonly int _self = Environment.ProcessId;
    readonly int _session = Process.GetCurrentProcess().SessionId;

    public ProcessScan Scan(LiveStore store, IReadOnlyCollection<int>? gamePids = null)
    {
        var result = new ProcessScan();
        var groups = new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);
        var alive = new HashSet<int>();
        var now = DateTime.UtcNow;
        IntPtr fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out int fgPid);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id <= 4 || p.SessionId != _session) continue;
                    string key = p.ProcessName.ToLowerInvariant();
                    alive.Add(p.Id);
                    string? path = Native.GetImagePath(p.Id);
                    if (path == null) { result.ProtectedCount++; continue; }   // cannot even query it: system-level

                    double mem = p.WorkingSet64 / 1048576.0;
                    bool win = p.MainWindowHandle != IntPtr.Zero && Native.IsWindowVisible(p.MainWindowHandle);
                    double cpu = 0;
                    try
                    {
                        var t = p.TotalProcessorTime;
                        if (_prev.TryGetValue(p.Id, out var old))
                        {
                            double dt = (now - old.at).TotalSeconds;
                            if (dt > 0.2) cpu = Math.Max(0, (t - old.cpu).TotalSeconds / dt / _cores * 100);
                            else cpu = 0;
                        }
                        _prev[p.Id] = (now, t);
                    }
                    catch { }

                    if (!groups.TryGetValue(key, out var g))
                    {
                        g = new AppGroup { Key = key, Path = path };
                        var (tier, reason) = Catalog.Classify(key, path, win, mem);
                        g.Tier = tier; g.Reason = reason;
                        g.Name = FriendlyName(path, key);
                        groups[key] = g;
                    }
                    g.Pids.Add(p.Id);
                    g.MemMB += mem;
                    g.Cpu += cpu;
                    if (win) g.HasWindow = true;
                    if (p.Id == fgPid) g.Reason = g.Reason; // foreground handled by caller via IsForeground
                }
                catch { /* process exited or is inaccessible */ }
            }
        }

        foreach (var stale in _prev.Keys.Where(k => !alive.Contains(k)).ToList()) _prev.Remove(stale);

        foreach (var g in groups.Values)
        {
            // Window state may have changed the classification of unknown apps.
            if (g.Tier == Tier.Background || g.Tier == Tier.Review)
            {
                var (tier, reason) = Catalog.Classify(g.Key, g.Path, g.HasWindow, g.MemMB);
                g.Tier = tier; g.Reason = reason;
            }
            store.ApplyLearning(g);
            if (g.Pids.Contains(_self)) { g.Tier = Tier.Protected; g.Reason = "OptiGame+"; }
            if (gamePids != null && g.Pids.Any(gamePids.Contains)) { g.Tier = Tier.Keep; g.Reason = "Your running game"; }
            else if (g.Pids.Contains(fgPid) && g.Listable) { g.Tier = Tier.Keep; g.Reason = "In front of you right now"; }
            if (g.Tier == Tier.Protected) result.ProtectedCount += g.Pids.Count;
            result.Groups.Add(g);
        }
        return result;
    }

    string FriendlyName(string path, string key)
    {
        if (_desc.TryGetValue(path, out var d)) return d;
        string name = key;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            bool genericProduct = string.IsNullOrWhiteSpace(vi.ProductName) || vi.ProductName.Contains("Operating System");
            string? n = !genericProduct ? vi.ProductName : vi.FileDescription;
            if (!string.IsNullOrWhiteSpace(n) && n.Length < 40) name = n.Trim();
            else name = System.IO.Path.GetFileNameWithoutExtension(path);
        }
        catch { name = System.IO.Path.GetFileNameWithoutExtension(path); }
        _desc[path] = name;
        return name;
    }
}
