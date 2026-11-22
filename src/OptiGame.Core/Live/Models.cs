namespace OptiGame.Core.Live;

/// <summary>How OptiGame+ treats a process group.</summary>
public enum Tier
{
    Protected = 0,   // never touched
    Keep = 1,        // voice chat, overlays, dev tools, or learned "keep"
    Suggested = 2,   // safe to close, pre-ticked
    Review = 3,      // may hold work, unticked
    Background = 4   // unlisted background processes: only trimmed / throttled
}

/// <summary>All processes of one executable, shown as a single row.</summary>
public sealed class AppGroup : Observable
{
    public string Key { get; init; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public List<int> Pids { get; set; } = new();
    public Tier Tier { get; set; }
    public string Reason { get; set; } = "";
    public bool Learned { get; set; }
    public bool HasWindow { get; set; }

    private double _memMB; public double MemMB { get => _memMB; set { if (Set(ref _memMB, value)) { Raise(nameof(MemText)); } } }
    private double _cpu; public double Cpu { get => _cpu; set { if (Set(ref _cpu, value)) Raise(nameof(CpuText)); } }
    private bool _sel; public bool IsSelected { get => _sel; set => Set(ref _sel, value); }

    public int ProcessCount => Pids.Count;
    public string MemText => MemMB >= 1024 ? $"{MemMB / 1024:0.0} GB" : $"{MemMB:0} MB";
    public string CpuText => Cpu < 0.1 ? "–" : $"{Cpu:0.#}%";
    public string TierText => Tier switch { Tier.Suggested => "Safe to close", Tier.Review => "Review first", Tier.Keep => "Keeping", _ => "" };
    public int TierOrder => (int)Tier;
    public bool Listable => Tier is Tier.Suggested or Tier.Review;
    public string Detail => ProcessCount > 1 ? $"{Reason} · {ProcessCount} processes" : Reason;

    public void RaiseTier() { Raise(nameof(TierText)); Raise(nameof(Detail)); Raise(nameof(Learned)); Raise(nameof(TierOrder)); }
    public void RaiseDetail() { Raise(nameof(Detail)); Raise(nameof(ProcessCount)); }
}

public sealed class ProcessScan
{
    public List<AppGroup> Groups { get; } = new();
    public int ProtectedCount { get; set; }
}

public sealed class GameHit
{
    public string Exe { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public List<int> Pids { get; set; } = new();
    public bool Confident { get; set; }
    public string Why { get; set; } = "";
    public int Score { get; set; }          // 3 = on your list, 2 = known game / Unreal shipping build, 1 = lives in a game library folder
    public long MemBytes { get; set; }
}

/// <summary>An executable on the user's own list (carried over from GameClear, or remembered when seen running).</summary>
public sealed class SavedGame
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Manual { get; set; }
}

public sealed class Pref
{
    public int Closed { get; set; }
    public int Skipped { get; set; }
    public bool Keep { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ClosedApp
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class LiveSettings : Observable
{
    private bool _auto = true, _reopen = true, _force = false, _throttle = true, _trim = true,
        _plan = true, _prio = true, _fs = false, _tray = true, _notify = true, _purge = false, _updates = true;

    public bool AutoBoost { get => _auto; set => Set(ref _auto, value); }
    public bool ReopenAfterGame { get => _reopen; set => Set(ref _reopen, value); }
    public bool ForceClose { get => _force; set => Set(ref _force, value); }
    public bool ThrottleBackground { get => _throttle; set => Set(ref _throttle, value); }
    public bool TrimMemory { get => _trim; set => Set(ref _trim, value); }
    public bool SwitchPowerPlan { get => _plan; set => Set(ref _plan, value); }
    public bool RaiseGamePriority { get => _prio; set => Set(ref _prio, value); }
    public bool TreatFullscreenAsGame { get => _fs; set => Set(ref _fs, value); }
    public bool KeepInTray { get => _tray; set => Set(ref _tray, value); }
    public bool Notifications { get => _notify; set => Set(ref _notify, value); }
    public bool PurgeStandby { get => _purge; set => Set(ref _purge, value); }
    /// <summary>Ask GitHub's releases API for a newer version. Nothing about the PC is sent.</summary>
    public bool CheckForUpdates { get => _updates; set => Set(ref _updates, value); }
}

public sealed class LiveData
{
    public LiveSettings Settings { get; set; } = new();
    public Dictionary<string, Pref> Prefs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Executables on the user's own game list (score 3 in detection).</summary>
    public List<SavedGame> Games { get; set; } = new();
    public Guid? PendingPowerRestore { get; set; }
    public List<ClosedApp> LastClosed { get; set; } = new();
    public int BoostCount { get; set; }
    public double TotalFreedMB { get; set; }

    /// <summary>The vendor app the user picked by hand when the brand was not recognised (an .exe or .lnk path).</summary>
    public string? VendorAppPath { get; set; }
    public string? VendorAppName { get; set; }
    /// <summary>Game Spec Check, when the user chose where it is.</summary>
    public string? SpecCheckPath { get; set; }
    /// <summary>When the updater last asked GitHub (at most once a day automatically).</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }
}

public enum Severity { Info, Good, Caution, Critical }

public sealed class Tip
{
    public string Glyph { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? ActionText { get; set; }
    public Action? Action { get; set; }
    public Severity Severity { get; set; } = Severity.Info;
    public bool HasAction => Action != null;
}

public sealed class BoostResult
{
    public double FreedMB { get; set; }
    public int Trimmed { get; set; }
    public int Throttled { get; set; }
    public List<string> Closed { get; } = new();
    public List<string> Stubborn { get; } = new();
    public string? PlanNote { get; set; }
    public bool PriorityRaised { get; set; }
    public bool StandbyPurged { get; set; }

    public string Summary()
    {
        var parts = new List<string>();
        if (FreedMB >= 1) parts.Add($"freed about {(FreedMB >= 1024 ? $"{FreedMB / 1024:0.0} GB" : $"{FreedMB:0} MB")}");
        if (Closed.Count > 0) parts.Add($"closed {Closed.Count} app{(Closed.Count == 1 ? "" : "s")}");
        if (Throttled > 0) parts.Add($"moved {Throttled} background app{(Throttled == 1 ? "" : "s")} to efficiency mode");
        if (PriorityRaised) parts.Add("raised game priority");
        if (PlanNote != null) parts.Add(PlanNote);
        if (StandbyPurged) parts.Add("purged standby memory");
        return parts.Count == 0 ? "Nothing needed clearing." : char.ToUpper(parts[0][0]) + parts[0][1..] + (parts.Count > 1 ? ", " + string.Join(", ", parts.GetRange(1, parts.Count - 1)) : "") + ".";
    }
}
