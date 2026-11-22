using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using OptiGame.Core;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Live;
using OptiGame.Core.Updates;
using OptiGame.Core.Vendors;

namespace OptiGame.App.ViewModels;

/// <summary>Everything the dashboard meters show; replaced wholesale on each refresh.</summary>
public sealed class Dash
{
    public string RamTitle { get; set; } = "–"; public string RamSub { get; set; } = ""; public int RamPct { get; set; } public int RamLevel { get; set; }
    public string CpuTitle { get; set; } = "–"; public string CpuSub { get; set; } = ""; public int CpuPct { get; set; } public int CpuLevel { get; set; }
    public bool GpuVisible { get; set; } public string GpuTitle { get; set; } = "–"; public string GpuSub { get; set; } = ""; public int GpuPct { get; set; } public int GpuLevel { get; set; }
    public string PowerTitle { get; set; } = "–"; public string PowerSub { get; set; } = ""; public int PowerLevel { get; set; } public string PowerGlyph { get; set; } = "";
}

public sealed class KeepItem
{
    public string Key { get; set; } = ""; public string Name { get; set; } = ""; public bool Learned { get; set; }
}

/// <summary>
/// The live side: detects a running game, boosts and restores, lists apps to close, gives advice. It shares the
/// hardware profile and the game list with the optimizer side (<see cref="MainViewModel"/>).
/// </summary>
public sealed class AppVm : Observable
{
    public LiveStore Store { get; }
    public LiveSettings Settings => Store.Data.Settings;
    public Booster Booster { get; }
    public bool IsAdmin { get; } = Booster.IsAdmin();

    HardwareInfo _profile = new();
    /// <summary>The one hardware profile: shown here and used by the recommendations.</summary>
    public HardwareInfo Profile { get => _profile; private set { if (Set(ref _profile, value)) RaiseProfile(); } }

    readonly Scanner _scanner = new();
    readonly GameDetector _detector = new();
    readonly DispatcherTimer _timer;
    ProcessScan? _scan; GpuStats? _gpu; MemStats _mem = new(); PowerInfo _power = new();
    GameHit? _game; string? _gamePath; int _misses; bool _ticking;
    IReadOnlyCollection<string> _libraryDirs = Array.Empty<string>(), _userDirs = Array.Empty<string>();

    public event Action<string, string>? ToastRequested;
    public event Action<int>? NavigateRequested;
    public bool WindowVisible { get; set; }

    public ObservableCollection<AppGroup> Apps { get; } = new();
    public ListCollectionView AppsView { get; }
    public ObservableCollection<Tip> Tips { get; } = new();
    public ObservableCollection<KeepItem> KeepItems { get; } = new();
    public ObservableCollection<VendorApp> VendorApps { get; } = new();
    string _tipSig = "";

    public AppVm(LiveStore store)
    {
        Store = store;
        Booster = new Booster(store);
        HardwareDetector.ReadTopology(_profile);
        _profile.RamGb = Math.Round(LiveStats.Mem().TotalMB / 1024, 1);
        _sww = LiveStore.GetStartWithWindows();

        AppsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Apps);
        AppsView.SortDescriptions.Add(new SortDescription(nameof(AppGroup.TierOrder), ListSortDirection.Ascending));
        AppsView.SortDescriptions.Add(new SortDescription(nameof(AppGroup.MemMB), ListSortDirection.Descending));
        AppsView.IsLiveSorting = true;
        AppsView.LiveSortingProperties.Add(nameof(AppGroup.TierOrder));
        AppsView.LiveSortingProperties.Add(nameof(AppGroup.MemMB));

        RebuildKeepItems();
        Booster.Changed += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateHero);

        Settings.PropertyChanged += (_, _) => UpdateHero();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await TickAsync();
        UpdateHero();
        RefreshVendors();
    }

    public void Start() { _timer.Start(); _ = TickAsync(); }

    // ------------------------------------------------------------ shared hardware and game list
    /// <summary>The optimizer side finished loading: adopt its (full) hardware profile and game list.</summary>
    public void AdoptSession(HardwareInfo hw, IEnumerable<GameInfo> games)
    {
        Profile = hw;
        var list = games.ToList();
        _libraryDirs = list.Where(g => g.Store != "Manual" && !g.Sources.Contains("Added by you")).Select(g => g.InstallDir).ToList();
        _userDirs = list.Where(g => g.Store == "Manual").Select(g => g.InstallDir).ToList();
        RefreshVendors();
    }

    void RaiseProfile()
    {
        Raise(nameof(ProfileLine)); Raise(nameof(UseGpuText)); Raise(nameof(RamLine)); Raise(nameof(GpuLine)); Raise(nameof(TuneNote)); Raise(nameof(ModelLine));
    }

    // ------------------------------------------------------------ vendor apps
    VendorApp? _vendor;
    /// <summary>The laptop maker's own app (or the one the user chose): what the GPU-hot tip points at.</summary>
    public VendorApp? PrimaryVendor { get => _vendor; private set { if (Set(ref _vendor, value)) { Raise(nameof(HasVendor)); Raise(nameof(VendorOpenText)); Raise(nameof(VendorTipText)); Raise(nameof(VendorHeadline)); } } }
    public bool HasVendor => _vendor is not null;
    public string VendorOpenText => _vendor is null ? "" : $"Open {_vendor.Name}";
    public string VendorTipText => _vendor?.Tip ?? "";
    public string VendorHeadline => _vendor is null
        ? $"No laptop app was found for {(Profile.ModelName.Length > 0 ? Profile.ModelName : "this PC")}."
        : $"{_vendor.Name} ({_vendor.Source})";
    public string ModelLine => Profile.ModelName;

    /// <summary>Looks for vendor apps in the background (registry only, no PowerShell).</summary>
    public void RefreshVendors()
    {
        var hw = Profile; var path = Store.Data.VendorAppPath; var name = Store.Data.VendorAppName;
        _ = Task.Run(() => VendorCatalog.DetectHere(hw, path, name)).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                VendorApps.Clear();
                foreach (var v in t.Result) VendorApps.Add(v);
                PrimaryVendor = t.Result.FirstOrDefault(v => v.UserPicked) ?? t.Result.FirstOrDefault(v => v.Kind == "maker");
                _tipSig = ""; UpdateTips();
            });
        });
    }

    public async Task OpenVendorAsync(VendorApp? app = null)
    {
        app ??= PrimaryVendor;
        if (app is null) return;
        var err = await VendorCatalog.LaunchAsync(app);
        Message = err.Length == 0 ? $"Opened {app.Name}." : err;
    }

    public void ChooseVendorApp(string path)
    {
        Store.Data.VendorAppPath = path;
        Store.Data.VendorAppName = System.IO.Path.GetFileNameWithoutExtension(path);
        Store.Save();
        RefreshVendors();
        Message = $"OptiGame+ will open {Store.Data.VendorAppName} from now on.";
    }

    public void ForgetVendorApp()
    {
        Store.Data.VendorAppPath = null; Store.Data.VendorAppName = null; Store.Save();
        RefreshVendors();
    }

    // ------------------------------------------------------------ header / hero
    string _heroTitle = "Looking for games…", _heroDetail = "", _heroGlyph = "", _boostText = "Boost now";
    bool _boostBusy;
    public string HeroTitle { get => _heroTitle; set => Set(ref _heroTitle, value); }
    public string HeroDetail { get => _heroDetail; set => Set(ref _heroDetail, value); }
    public string HeroGlyph { get => _heroGlyph; set => Set(ref _heroGlyph, value); }
    public string BoostText { get => _boostText; set => Set(ref _boostText, value); }
    public bool BoostBusy { get => _boostBusy; set { if (Set(ref _boostBusy, value)) Raise(nameof(BoostEnabled)); } }
    public bool BoostEnabled => !_boostBusy;

    Dash _dash = new();
    public Dash Dash { get => _dash; private set => Set(ref _dash, value); }

    string _message = "";
    public string Message { get => _message; set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrEmpty(_message);
    public void DismissMessage() => Message = "";

    string _lifetime = "";
    public string Lifetime { get => _lifetime; set => Set(ref _lifetime, value); }

    public string ProfileLine => $"{Profile.ModelName}" + (Profile.DGpu != null ? $" · {ShortGpu(Profile.DGpu)}" : "");
    static string ShortGpu(string n) => n.Replace("NVIDIA ", "").Replace("GeForce ", "").Replace(" Laptop GPU", "").Trim();

    /// <summary>Full path of the exe of the game that is running now (confident or fullscreen guess), or null.</summary>
    public string? RunningPath => _game?.Path;
    public GameHit? CurrentGame => _game;

    void UpdateHero()
    {
        bool active = Booster.Active;
        var g = _game;
        bool confident = g is { Confident: true } || (g != null && Settings.TreatFullscreenAsGame);
        if (g != null && confident)
        {
            HeroGlyph = "";
            HeroTitle = $"{g.Name} is running";
            HeroDetail = active ? "Boost is active. " + (Booster.Last?.Summary() ?? "") + RestoreHint()
                : Settings.AutoBoost ? "Detected. Boosting…" : "Auto-boost is off. Press Boost to clear resources for this game.";
        }
        else if (g != null)
        {
            HeroGlyph = "";
            HeroTitle = $"{g.Name} looks like a game";
            HeroDetail = "It is running fullscreen. Boost it now, or turn on “Treat fullscreen apps as games” in Settings to do this automatically.";
        }
        else if (active)
        {
            HeroGlyph = "";
            HeroTitle = "Boost is active";
            HeroDetail = (Booster.Last?.Summary() ?? "") + RestoreHint();
        }
        else
        {
            HeroGlyph = "";
            HeroTitle = "No game running";
            HeroDetail = Settings.AutoBoost ? "OptiGame+ will boost automatically as soon as a game starts." : "Auto-boost is off. Press Boost to clear resources manually.";
        }
        BoostText = active ? "Restore" : "Boost now";
        Lifetime = Store.Data.BoostCount == 0 ? "" : $"{Store.Data.BoostCount} boost{(Store.Data.BoostCount == 1 ? "" : "s")} so far · about {Advisor.Fmt(Store.Data.TotalFreedMB)} freed in total";
        Raise(nameof(BoostActive)); Raise(nameof(RowBoostText));
    }

    public bool BoostActive => Booster.Active;
    public string RowBoostText => Booster.Active ? "Restore" : "Boost";
    string RestoreHint() => Booster.Active ? " Everything is put back when the game closes." : "";

    // ------------------------------------------------------------ main loop
    async Task TickAsync()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            var lib = _libraryDirs; var usr = _userDirs;
            var hit = await Task.Run(() => _detector.Detect(Store.Data, lib, usr));
            await OnGameAsync(hit);
            if (WindowVisible) await RefreshAsync();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally { _ticking = false; }
    }

    public async Task RefreshAsync()
    {
        var scanT = Task.Run(() => _scanner.Scan(Store, _game?.Pids));
        var gpuT = Task.Run(LiveStats.Gpu);
        _scan = await scanT; _gpu = await gpuT;
        _mem = LiveStats.Mem(); _power = LiveStats.Power();
        double cpu = LiveStats.CpuLoad();
        UpdateDash(cpu);
        MergeApps(_scan);
        UpdateTips();
        UpdateHero();
    }

    void UpdateDash(double cpu)
    {
        var d = new Dash();
        d.RamPct = _mem.UsedPct; d.RamLevel = d.RamPct >= 88 ? 2 : d.RamPct >= 75 ? 1 : 0;
        d.RamTitle = $"{_mem.UsedMB / 1024:0.0} of {_mem.TotalMB / 1024:0.#} GB";
        d.RamSub = $"{_mem.AvailMB / 1024:0.0} GB available";
        d.CpuPct = (int)Math.Round(cpu); d.CpuLevel = cpu >= 95 ? 2 : cpu >= 85 ? 1 : 0;
        d.CpuTitle = $"{cpu:0}%";
        d.CpuSub = Profile.Hybrid ? $"{Profile.PCores}P + {Profile.ECores}E cores" : $"{Profile.LogicalCpus} threads";
        if (_gpu is { } g)
        {
            d.GpuVisible = true; d.GpuPct = g.UtilPct; d.GpuLevel = g.TempC >= 90 ? 2 : g.TempC >= 83 ? 1 : 0;
            d.GpuTitle = $"{g.UtilPct}%";
            d.GpuSub = $"{g.TempC} °C · {g.MemUsedMB / 1024:0.0}/{g.MemTotalMB / 1024:0.#} GB VRAM";
        }
        if (_power.HasBattery)
        {
            d.PowerGlyph = _power.OnAc ? "" : "";
            d.PowerTitle = _power.OnAc ? $"Plugged in · {_power.Percent}%" : $"On battery · {_power.Percent}%";
            d.PowerLevel = _power.OnAc ? 0 : (_game != null || Booster.Active ? 2 : 1);
        }
        else { d.PowerGlyph = ""; d.PowerTitle = "Desktop power"; }
        d.PowerSub = _power.PlanName;
        Dash = d;
    }

    void UpdateTips()
    {
        var tips = Advisor.Build(new AdvisorInput
        {
            Hw = Profile, Mem = _mem, Power = _power, Gpu = _gpu, Scan = _scan, Game = _game, BoostActive = Booster.Active,
            Vendor = PrimaryVendor,
            OpenVendor = () => _ = OpenVendorAsync(),
            GoToApps = () => NavigateRequested?.Invoke(1),
            FixGpu = g => FixGpu(new[] { g.Path }, g.Name),
            FixPlan = () => _ = ApplyPowerPlanNowAsync()
        });
        string sig = string.Join("|", tips.Select(t => t.Title)) + "|" + PrimaryVendor?.Name;
        if (sig == _tipSig) return;       // don't rebuild under the user's cursor
        _tipSig = sig;
        Tips.Clear();
        foreach (var t in tips) Tips.Add(t);
    }

    // ------------------------------------------------------------ apps list
    void MergeApps(ProcessScan scan)
    {
        var listable = scan.Groups.Where(g => g.Listable).ToDictionary(g => g.Key);
        foreach (var gone in Apps.Where(a => !listable.ContainsKey(a.Key)).ToList()) { gone.PropertyChanged -= OnAppChanged; Apps.Remove(gone); }
        var existing = Apps.ToDictionary(a => a.Key);
        foreach (var n in listable.Values)
        {
            if (existing.TryGetValue(n.Key, out var a))
            {
                var oldTier = a.Tier;
                a.Pids = n.Pids; a.HasWindow = n.HasWindow; a.Path = n.Path; a.MemMB = n.MemMB; a.Cpu = n.Cpu;
                a.Tier = n.Tier; a.Reason = n.Reason; a.Learned = n.Learned; a.Name = n.Name;
                if (oldTier != Tier.Suggested && n.Tier == Tier.Suggested) a.IsSelected = true;
                a.RaiseTier(); a.RaiseDetail();
            }
            else
            {
                n.IsSelected = n.Tier == Tier.Suggested;
                n.PropertyChanged += OnAppChanged;
                Apps.Add(n);
            }
        }
        ProtectedSummary = $"{scan.ProtectedCount} Windows, driver and launcher processes are protected and never touched.";
        UpdateSelection();
    }

    void OnAppChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppGroup.IsSelected) or nameof(AppGroup.MemMB)) UpdateSelection();
    }

    string _protectedSummary = "", _selectionText = "", _closeText = "Close selected";
    int _selCount; bool _hasApps;
    public string ProtectedSummary { get => _protectedSummary; set => Set(ref _protectedSummary, value); }
    public string SelectionText { get => _selectionText; set => Set(ref _selectionText, value); }
    public string CloseText { get => _closeText; set => Set(ref _closeText, value); }
    public int SelectedCount { get => _selCount; set { if (Set(ref _selCount, value)) Raise(nameof(CanClose)); } }
    public bool HasApps { get => _hasApps; set => Set(ref _hasApps, value); }
    public bool CanClose => _selCount > 0 && !_boostBusy;

    void UpdateSelection()
    {
        var sel = Apps.Where(a => a.IsSelected).ToList();
        SelectedCount = sel.Count;
        HasApps = Apps.Count > 0;
        double mb = sel.Sum(a => a.MemMB);
        SelectionText = Apps.Count == 0 ? "Nothing to clear right now." :
            sel.Count == 0 ? $"{Apps.Count} app{(Apps.Count == 1 ? "" : "s")} listed. Tick the ones to close." :
            $"{sel.Count} selected · about {Advisor.Fmt(mb)} will be freed";
        CloseText = sel.Count == 0 ? "Close selected" : $"Close {sel.Count} selected";
        Raise(nameof(CanClose));
    }

    public void SelectRecommended() { foreach (var a in Apps) a.IsSelected = a.Tier == Tier.Suggested; }
    public void SelectNone() { foreach (var a in Apps) a.IsSelected = false; }

    public void KeepApp(AppGroup a)
    {
        Store.SetKeep(a, true);
        Apps.Remove(a);
        RebuildKeepItems();
        UpdateSelection();
        Message = $"{a.Name} is on your keep list and will not be suggested again. You can undo this in Settings.";
    }

    /// <summary>Which listed apps are Review-tier (may hold unsaved work) among the selection.</summary>
    public string[] SelectedReviewNames() => Apps.Where(a => a.IsSelected && a.Tier == Tier.Review).Select(a => a.Name).ToArray();

    public async Task CloseSelectedAsync()
    {
        var chosen = Apps.Where(a => a.IsSelected).ToList();
        if (chosen.Count == 0 || BoostBusy) return;
        BoostBusy = true; Raise(nameof(CanClose));
        try
        {
            Store.RecordChoices(Apps.ToList());
            var scan = _scan ?? await Task.Run(() => _scanner.Scan(Store, _game?.Pids));
            var plan = SafePlan(scan, _game, false, chosen);
            plan.Close.AddRange(chosen);
            var res = await Booster.BoostAsync(plan);
            string msg = res.Summary();
            if (res.Stubborn.Count > 0)
                msg += $" {string.Join(", ", res.Stubborn)} did not close politely (it may be asking to save). Close it yourself, or enable “Force-close apps that ignore the request” in Settings.";
            Message = msg;
            await RefreshAsync();
        }
        finally { BoostBusy = false; Raise(nameof(CanClose)); UpdateHero(); }
    }

    // ------------------------------------------------------------ boosting
    BoostPlan SafePlan(ProcessScan scan, GameHit? game, bool applyPlan, ICollection<AppGroup>? closing = null)
    {
        var plan = new BoostPlan { Game = game, ApplyPowerPlan = applyPlan };
        var gamePids = new HashSet<int>(game?.Pids ?? new());
        foreach (var g in scan.Groups)
        {
            if (g.Tier is not (Tier.Suggested or Tier.Review or Tier.Background)) continue;
            if (g.Pids.Any(gamePids.Contains)) continue;
            if (closing != null && closing.Any(c => c.Key == g.Key)) continue;
            plan.Trim.Add(g); plan.Throttle.Add(g);
        }
        return plan;
    }

    /// <summary>The Boost / Restore button.</summary>
    public async Task BoostOrRestoreAsync()
    {
        if (BoostBusy) return;
        if (Booster.Active)
        {
            var reopened = Booster.Restore(Settings.ReopenAfterGame);
            Message = reopened.Count > 0 ? $"Restored settings and re-opened {string.Join(", ", reopened)}." : "Restored your normal settings.";
            return;
        }
        BoostBusy = true;
        try
        {
            var scan = await Task.Run(() => _scanner.Scan(Store, _game?.Pids));
            _scan = scan;
            var res = await Booster.BoostAsync(SafePlan(scan, _game, true));
            int closable = scan.Groups.Count(g => g.Tier == Tier.Suggested);
            Message = res.Summary() + (closable > 0 ? $" {closable} more app{(closable == 1 ? "" : "s")} can be closed from the Apps page." : "");
            await RefreshAsync();
        }
        finally { BoostBusy = false; UpdateHero(); }
    }

    async Task OnGameAsync(GameHit? hit)
    {
        bool changed = _game?.Path != hit?.Path;
        _game = hit;
        bool qualifies = hit != null && (hit.Confident || Settings.TreatFullscreenAsGame);
        if (hit is { Confident: true }) RememberGame(hit);

        if (qualifies)
        {
            _misses = 0;
            if (_gamePath != hit!.Path)
            {
                _gamePath = hit.Path;
                if (Settings.AutoBoost && !Booster.Active) await AutoBoostAsync(hit);
            }
        }
        else if (_gamePath != null && ++_misses >= 2)
        {
            _gamePath = null;
            if (Booster.Active)
            {
                var name = Booster.GameName ?? "Your game";
                var reopened = Booster.Restore(Settings.ReopenAfterGame);
                string text = "Priorities and power settings are back to normal." + (reopened.Count > 0 ? $" Re-opened {string.Join(", ", reopened)}." : "");
                Toast($"{name} closed", text);
                Message = text;
            }
        }
        if (changed) Raise(nameof(RunningPath));
        UpdateHero();
    }

    async Task AutoBoostAsync(GameHit hit)
    {
        BoostBusy = true;
        try
        {
            var scan = await Task.Run(() => _scanner.Scan(Store, _game?.Pids));
            _scan = scan;
            // Auto-boost only does reversible things; the power plan is never switched here.
            var res = await Booster.BoostAsync(SafePlan(scan, hit, false));
            var closable = scan.Groups.Where(g => g.Tier == Tier.Suggested).ToList();
            string text = res.Summary();
            if (closable.Count > 0) text += $" {closable.Count} more app{(closable.Count == 1 ? "" : "s")} (about {Advisor.Fmt(closable.Sum(c => c.MemMB))}) could be closed. Open OptiGame+ to review.";
            Toast($"Boosting {hit.Name}", text);
        }
        finally { BoostBusy = false; }
    }

    async Task ApplyPowerPlanNowAsync()
    {
        var cur = Native.GetActiveScheme();
        if (cur == null || cur == Booster.HighPerf || cur == Booster.Ultimate) return;
        // Reuse the boost pipeline so the plan is restored automatically later; no trimming/closing is added here.
        var plan = new BoostPlan { Game = _game, ApplyPowerPlan = true };
        var res = await Booster.BoostAsync(plan);
        Message = res.PlanNote != null ? "Switched to High performance. OptiGame+ will switch back when you press Restore or the game closes." : "Could not change the power plan.";
        await RefreshAsync();
    }

    void Toast(string title, string text)
    {
        if (Settings.Notifications) ToastRequested?.Invoke(title, text);
    }

    // ------------------------------------------------------------ games the live side remembers
    void RememberGame(GameHit hit)
    {
        if (GameDetector.IsUnder(hit.Path, Roots(_libraryDirs)) || GameDetector.IsUnder(hit.Path, Roots(_userDirs))) return;   // already in the shared list
        if (Store.Data.Games.Any(g => g.Path.Equals(hit.Path, StringComparison.OrdinalIgnoreCase))) return;
        Store.Data.Games.Add(new SavedGame { Name = hit.Name, Path = hit.Path });
        Store.Save();
    }

    static List<string> Roots(IEnumerable<string> dirs) => dirs.Select(d => d.TrimEnd('\\') + "\\").ToList();

    // ------------------------------------------------------------ GPU preference
    /// <summary>The button text: "Use RTX GPU" on a GeForce RTX laptop, "Use NVIDIA GPU" or "Use dedicated GPU" otherwise.</summary>
    public string UseGpuText => Profile.DGpu is { } d
        ? (d.Contains("RTX", StringComparison.OrdinalIgnoreCase) ? "Use RTX GPU" : d.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "Use NVIDIA GPU" : "Use dedicated GPU")
        : "Use dedicated GPU";
    public string DGpuShort => Profile.DGpu == null ? "dedicated GPU" : ShortGpu(Profile.DGpu);

    public void FixGpu(IEnumerable<string> exePaths, string name)
    {
        var list = exePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) { Message = $"No program was found for {name} yet. Start it once, then try again."; return; }
        bool ok = list.All(GpuPref.SetHighPerformance);
        Message = ok
            ? $"{name} is now set to use your {DGpuShort}. It takes effect the next time the game starts."
            : "Could not change the Windows graphics setting.";
        _tipSig = ""; UpdateTips();
    }

    // ------------------------------------------------------------ updates
    UpdateInfo? _update;
    string _updateStatus = "";
    bool _updateBusy;
    public string VersionText => $"OptiGame+ {UpdateService.Current}";
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public bool UpdateAvailable => _update is not null;
    public bool UpdateBusy { get => _updateBusy; private set { if (Set(ref _updateBusy, value)) Raise(nameof(CanCheckUpdate)); } }
    public bool CanCheckUpdate => !_updateBusy;
    public string InstallUpdateText => _update is null ? "" : UpdateService.CanInstall(_update) && UpdateService.RunsFromInstall()
        ? $"Install {_update.Version}" : $"Open the {_update.Version} download page";

    /// <summary>Asks GitHub for a newer release: automatically at most once a day (when allowed), or whenever the user clicks.</summary>
    public async Task CheckForUpdateAsync(bool manual)
    {
        if (UpdateBusy) return;
        if (!manual)
        {
            if (!Settings.CheckForUpdates) return;
            if (Store.Data.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
        }
        UpdateBusy = true;
        UpdateStatus = "Checking for updates...";
        try
        {
            var (info, status) = await UpdateService.CheckAsync();
            Store.Data.LastUpdateCheckUtc = DateTime.UtcNow; Store.Save();
            _update = info; UpdateStatus = status;
            Raise(nameof(UpdateAvailable)); Raise(nameof(InstallUpdateText));
            if (info is not null && !manual) Message = $"OptiGame+ {info.Version} is available. Open Settings > Updates to install it.";
        }
        finally { UpdateBusy = false; }
    }

    /// <summary>Downloads, verifies and runs the setup, then exits so it can replace the files. Falls back to the release page.</summary>
    public async Task InstallUpdateAsync(Action exitApp)
    {
        if (_update is not { } u || UpdateBusy) return;
        if (!UpdateService.CanInstall(u) || !UpdateService.RunsFromInstall())
        {
            Process.Start(new ProcessStartInfo(string.IsNullOrEmpty(u.PageUrl) ? UpdateService.ReleasesPage : u.PageUrl) { UseShellExecute = true });
            return;
        }
        UpdateBusy = true;
        try
        {
            var progress = new Progress<double>(p => UpdateStatus = $"Downloading {u.Version}... {p:P0}");
            UpdateStatus = $"Downloading {u.Version}...";
            var file = await UpdateService.DownloadAsync(u, UpdateService.UpdateDir, progress);
            UpdateStatus = "Verified. Installing and restarting...";
            if (UpdateService.RunSetup(file) is null) { UpdateStatus = "Could not start the installer."; return; }
            exitApp();
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or IOException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            UpdateStatus = "The update failed: " + e.Message;
        }
        finally { UpdateBusy = false; }
    }

    // ------------------------------------------------------------ settings helpers
    bool _sww;
    public bool StartWithWindows { get => _sww; set { if (Set(ref _sww, value)) LiveStore.SetStartWithWindows(value); } }
    public string RamLine => $"{Profile.RamGb:0.#} GB memory";
    public string GpuLine => string.Join("  +  ", new[] { Profile.Gpu }.Concat(Profile.OtherGpus).Where(g => g.Length > 0 && g != "Unknown GPU"));
    public string TuneNote
    {
        get
        {
            var notes = new List<string>();
            if (Profile.RamGb is > 0 and <= 16.5) notes.Add($"With {Profile.RamGb:0.#} GB of memory, free RAM matters most: OptiGame+ watches for pressure and flags what is safe to close.");
            if (Profile.Hybrid) notes.Add($"Your CPU has {Profile.PCores} performance and {Profile.ECores} efficiency cores: background apps are moved to efficiency mode so the game keeps the fast cores.");
            if (Profile.HybridGraphics) notes.Add("This laptop has two graphics chips: OptiGame+ can pin each game to your dedicated GPU.");
            if (Profile.IsLaptop) notes.Add("As a laptop, it also warns about running on battery and a hot GPU.");
            return string.Join("\n", notes);
        }
    }
    public string AdminText => IsAdmin ? "Running as administrator: deep memory clean is available." : "Running as a normal user. Deep memory clean needs administrator rights.";

    public void RebuildKeepItems()
    {
        KeepItems.Clear();
        foreach (var kv in Store.Data.Prefs.Where(p => p.Value.Keep || p.Value.Skipped >= LiveStore.LearnThreshold))
            KeepItems.Add(new KeepItem { Key = kv.Key, Name = string.IsNullOrEmpty(kv.Value.Name) ? kv.Key : kv.Value.Name, Learned = !kv.Value.Keep });
        Raise(nameof(LearnedSummary));
    }
    public string LearnedSummary => KeepItems.Count == 0 ? "Nothing learned yet. OptiGame+ notices which apps you keep or close." : $"{KeepItems.Count} app{(KeepItems.Count == 1 ? "" : "s")} on your keep list.";

    public void RemoveKeep(KeepItem k)
    {
        if (Store.Data.Prefs.TryGetValue(k.Key, out var p)) { p.Keep = false; p.Skipped = 0; }
        Store.Save(); RebuildKeepItems();
    }

    public void ResetLearning() { Store.ResetLearning(); RebuildKeepItems(); Message = "Learning reset. OptiGame+ will start fresh."; }

    public void Shutdown()
    {
        _timer.Stop();
        if (Booster.Active) Booster.Restore(false);
    }

    public void RequestToast(string t, string s) => Toast(t, s);
    public void Navigate(int page) => NavigateRequested?.Invoke(page);
}
