using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using GameOptimizer.Core.Hardware;
using GameOptimizer.Core.Library;
using GameOptimizer.Core.Optimize;
using GameOptimizer.Core.Recommend;

namespace GameOptimizer.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>An item in a choice list: what is shown, and the key stored.</summary>
public sealed record Choice(string Label, string Key)
{
    public override string ToString() => Label;
}

/// <summary>One game in the list: its slider, its own settings panel and its current plan.</summary>
public sealed class GameRow : Observable
{
    readonly MainViewModel _vm;
    int _step;
    bool _selected, _expanded;
    GamePlan? _plan;
    GameRecommendation? _rec;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public GameRow(MainViewModel vm, GameInfo game) { _vm = vm; Game = game; Links = GameLinks.For(game); }

    public GameInfo Game { get; }
    public string Name => Game.Name;
    public string EngineText => (Game.Engine == GameEngine.Unknown ? "Unknown engine" : Game.Engine.ToString()) + "  ·  " + Game.SourcesText;
    public List<GameLink> Links { get; }
    Prefs P => _vm.Session!.Prefs;
    GamePrefs? GP => P.GameOrNull(Game.Id);

    public bool IsSelected { get => _selected; set { if (Set(ref _selected, value)) _vm.SelectionChanged(); } }
    public bool IsExpanded { get => _expanded; set { if (Set(ref _expanded, value) && value) RaiseSettings(); } }

    // ------------------------------------------------------------------ quality slider

    public int Step
    {
        get => _step;
        set
        {
            if (!Set(ref _step, value)) return;
            P.Game(Game.Id).Step = (QualityStep)value;
            P.Save();
            Raise(nameof(StepLabel)); Raise(nameof(AtRecommended)); Raise(nameof(IsCustom)); Raise(nameof(StepSource));
            _vm.RowChanged(this);
        }
    }

    /// <summary>Moves the slider to what the preferences give, without marking anything custom.</summary>
    public void SyncStep()
    {
        if (Set(ref _step, (int)P.StepFor(Game.Id, Recommended), nameof(Step)))
        { Raise(nameof(StepLabel)); Raise(nameof(AtRecommended)); }
        Raise(nameof(IsCustom)); Raise(nameof(StepSource));
    }

    public string StepLabel => ((QualityStep)_step).Label();
    public bool IsCustom => GP?.Step is not null;
    public string StepSource => P.StepWithSource(Game.Id, Recommended).source;
    public bool HasCustomChoices => GP is { IsEmpty: false };

    public QualityStep Recommended => _rec?.Step ?? _vm.Session?.Hw.Recommended ?? QualityStep.Balanced;
    public void SetRecommendation(GameRecommendation rec)
    {
        _rec = rec;
        Raise(nameof(Recommended)); Raise(nameof(RecText)); Raise(nameof(RecDetails)); Raise(nameof(BasisChip)); Raise(nameof(HasMeasured));
        SyncStep();
    }
    public string RecText => _rec is null ? "Recommended: checking..." : $"Recommended: {Recommended.Label()}";
    public string BasisChip => _rec?.Chip ?? "…";
    public string RecDetails => _rec?.Details ?? "Looking up this game's published requirements";
    public bool HasMeasured => _rec?.Measured is not null;
    public bool AtRecommended => _step == (int)Recommended;

    public void ResetStep()
    {
        if (GP is { } g) g.Step = null;
        P.Save();
        SyncStep();
        _vm.RowChanged(this);
    }

    // ------------------------------------------------------------------ plan

    public GamePlan? Plan
    {
        get => _plan;
        set
        {
            Set(ref _plan, value);
            Raise(nameof(StatusText)); Raise(nameof(CanOptimize)); Raise(nameof(HasWarning)); Raise(nameof(IsSupported));
            Raise(nameof(HasActions)); Raise(nameof(CanOpenFolder)); Raise(nameof(CanAddRule)); Raise(nameof(CanLaunch)); Raise(nameof(WarningText));
            Raise(nameof(HasNotControlled)); Raise(nameof(NotControlledText)); Raise(nameof(EffectiveText));
        }
    }

    public string StatusText => _plan?.StatusText ?? "Checking...";
    public bool CanOptimize => _plan?.Status == PlanStatus.Ready;
    public bool IsSupported => _plan is not null && _plan.Status != PlanStatus.Skipped;
    public bool HasWarning => _plan?.Warnings.Count > 0;
    public string WarningText => _plan is null ? "" : string.Join("\n", _plan.Warnings);
    public bool HasNotControlled => _plan?.NotControlled.Count > 0;
    public string NotControlledText => _plan is null ? "" : "Not controlled for this game: " + string.Join(" ", _plan.NotControlled);
    public bool HasActions => _plan?.Actions.Count > 0;
    public bool CanOpenFolder => _plan?.Actions.Contains(PlanAction.OpenSettingsFolder) == true;
    public bool CanAddRule => _plan?.Actions.Contains(PlanAction.AddRule) == true;
    public bool CanLaunch => _plan?.Actions.Contains(PlanAction.LaunchOnceAndRescan) == true && Game.SteamAppId is not null;

    // ------------------------------------------------------------------ per-game settings panel

    void Change(Action<GamePrefs> edit)
    {
        edit(P.Game(Game.Id));
        P.Save();
        RaiseSettings();
        SyncStep();
        _vm.RowChanged(this);
    }

    void RaiseSettings()
    {
        foreach (var n in new[] { nameof(ProfileKey), nameof(FpsKey), nameof(CustomFps), nameof(DisplayKey), nameof(ResolutionKey), nameof(ResolutionChoices),
                                  nameof(DisplayModeKey), nameof(VSyncKey), nameof(KeepResolution), nameof(KeepDisplayMode), nameof(KeepVSync), nameof(KeepFps),
                                  nameof(KeepRenderScale), nameof(KeepTexture), nameof(KeepQuality), nameof(SteamAppId), nameof(AppIdSource), nameof(EffectiveText),
                                  nameof(ExcludedText), nameof(HasExclusions), nameof(HasCustomChoices), nameof(FpsChoices) })
            Raise(n);
    }

    public List<Choice> ProfileChoices => _vm.ProfileChoices;
    public string ProfileKey
    {
        get => GP?.ProfileId ?? "";
        set => Change(g => g.ProfileId = string.IsNullOrEmpty(value) ? null : value);
    }

    DisplayInfo EffectiveDisplay => OptimizeRequest.DisplayFor(P, Game.Id, _vm.Session!.Hw);

    public List<Choice> FpsChoices
    {
        get
        {
            var auto = EffectiveDisplay.AutoFpsCap;
            var list = new List<Choice>
            {
                new("Profile's choice", ""), new("Keep the game's", "keep"),
                new($"Automatic ({auto} fps: active {DisplayMath.Hz(EffectiveDisplay.CurrentHz)} Hz minus 3)", "auto"),
                new("No cap, where supported", "uncapped"),
            };
            list.AddRange(FpsPolicy.Presets.Select(v => new Choice($"{v} fps", v.ToString(Inv))));
            list.Add(new Choice("Custom...", "custom"));
            return list;
        }
    }

    public string FpsKey
    {
        get => GP?.Fps switch
        {
            null => "",
            { Mode: FpsMode.Keep } => "keep",
            { Mode: FpsMode.Auto } => "auto",
            { Mode: FpsMode.Uncapped } => "uncapped",
            { Value: { } v } when FpsPolicy.Presets.Contains(v) => v.ToString(Inv),
            _ => "custom",
        };
        set => Change(g => g.Fps = value switch
        {
            "" => null,
            "keep" => new FpsPolicy { Mode = FpsMode.Keep },
            "auto" => new FpsPolicy { Mode = FpsMode.Auto },
            "uncapped" => new FpsPolicy { Mode = FpsMode.Uncapped },
            "custom" => new FpsPolicy { Mode = FpsMode.Custom, Value = g.Fps?.Value ?? EffectiveDisplay.AutoFpsCap },
            _ => new FpsPolicy { Mode = FpsMode.Custom, Value = double.Parse(value, Inv) },
        });
    }

    public string CustomFps
    {
        get => GP?.Fps is { Mode: FpsMode.Custom, Value: { } v } ? v.ToString("0.##", Inv) : "";
        set
        {
            if (double.TryParse(value, NumberStyles.Float, Inv, out var f) && f is >= 10 and <= 1000)
                Change(g => g.Fps = new FpsPolicy { Mode = FpsMode.Custom, Value = f });
        }
    }

    public List<Choice> DisplayChoices => _vm.DisplayChoices;
    public string DisplayKey { get => GP?.Display ?? ""; set => Change(g => g.Display = string.IsNullOrEmpty(value) ? null : value); }

    public List<Choice> ResolutionChoices
    {
        get
        {
            var d = EffectiveDisplay;
            var list = new List<Choice>
            {
                new("Profile's choice", ""), new("Keep the game's", "keep"),
                new($"Native ({d.Native.w}x{d.Native.h})", "native"), new($"Current ({d.CurrentWidth}x{d.CurrentHeight})", "current"),
            };
            list.AddRange(d.Resolutions.Select(r => new Choice($"{r.w}x{r.h}", $"{r.w}x{r.h}")));
            return list;
        }
    }

    public string ResolutionKey
    {
        get => GP?.Resolution switch
        {
            null => "",
            { Mode: ResolutionMode.Custom } r => $"{r.Width}x{r.Height}",
            var r => r.Mode.ToString().ToLowerInvariant(),
        };
        set => Change(g =>
        {
            if (string.IsNullOrEmpty(value)) { g.Resolution = null; return; }
            SettingsParser.Apply(g, "resolution=" + value);
        });
    }

    public List<Choice> DisplayModeChoices { get; } = new()
    {
        new("Profile's choice", ""), new("Keep the game's", "Keep"), new("Engine's usual fullscreen", "Recommended"),
        new("Exclusive fullscreen", "Fullscreen"), new("Borderless", "Borderless"), new("Windowed", "Windowed"),
    };
    public string DisplayModeKey
    {
        get => GP?.DisplayMode?.ToString() ?? "";
        set => Change(g => g.DisplayMode = string.IsNullOrEmpty(value) ? null : Enum.Parse<DisplayModeChoice>(value));
    }

    public List<Choice> VSyncChoices { get; } = new() { new("Profile's choice", ""), new("Keep the game's", "Keep"), new("On", "On"), new("Off", "Off") };
    public string VSyncKey
    {
        get => GP?.VSync?.ToString() ?? "";
        set => Change(g => g.VSync = string.IsNullOrEmpty(value) ? null : Enum.Parse<VSyncChoice>(value));
    }

    bool Keeps(string cat) => (P.Pick(Game.Id, s => s.Preserve) ?? new()).Contains(cat);
    void SetKeep(string cat, bool on) => Change(g =>
    {
        var list = (P.Pick(Game.Id, s => s.Preserve) ?? new()).ToList();
        if (on && !list.Contains(cat)) list.Add(cat);
        if (!on) list.Remove(cat);
        g.Preserve = list;
    });
    public bool KeepResolution { get => Keeps(Categories.Resolution); set => SetKeep(Categories.Resolution, value); }
    public bool KeepDisplayMode { get => Keeps(Categories.DisplayMode); set => SetKeep(Categories.DisplayMode, value); }
    public bool KeepVSync { get => Keeps(Categories.VSync); set => SetKeep(Categories.VSync, value); }
    public bool KeepFps { get => Keeps(Categories.FpsCap); set => SetKeep(Categories.FpsCap, value); }
    public bool KeepRenderScale { get => Keeps(Categories.RenderScale); set => SetKeep(Categories.RenderScale, value); }
    public bool KeepTexture { get => Keeps(Categories.Texture); set => SetKeep(Categories.Texture, value); }
    public bool KeepQuality { get => Keeps(Categories.Quality); set => SetKeep(Categories.Quality, value); }

    public bool HasExclusions => GP?.Excluded.Count > 0;
    public string ExcludedText => GP is { Excluded.Count: > 0 } g ? "Always kept for this game: " + string.Join(", ", g.Excluded) : "";
    public void ClearExclusions() => Change(g => g.Excluded.Clear());

    public string SteamAppId
    {
        get => GP?.SteamAppId ?? "";
        set
        {
            var v = value.Trim();
            if (v.Length > 0 && !v.All(char.IsAsciiDigit)) return;
            Change(g => g.SteamAppId = v.Length == 0 ? null : v);
            _vm.RecommendationNeeded(this);
        }
    }
    public string AppIdSource => $"Used for requirement lookups: {_vm.Session!.AppIdFor(Game) ?? "none"} ({_vm.Session.AppIdSource(Game)})";
    public bool CanLinkAppId => Game.SteamAppId is null;

    public string EffectiveText
    {
        get
        {
            var r = _vm.Session!.RequestFor(Game);
            return $"Profile {r.ProfileName} · display {r.Display.ActiveModeText} · frame cap {r.FpsText} · " +
                   $"resolution {(r.Resolution is var (w, h) ? $"{w}x{h}" : "kept")} · display mode {r.DisplayMode.ToString().ToLowerInvariant()} · VSync {r.VSync.ToString().ToLowerInvariant()}";
        }
    }

    public void ResetAllChoices()
    {
        P.Games.Remove(Game.Id);
        P.Save();
        RaiseSettings();
        SyncStep();
        _vm.RowChanged(this);
    }
}

public sealed class MainViewModel : Observable
{
    int _offset;
    bool _busy = true;
    string _status = "Reading hardware and scanning games...";
    string _search = "";
    string _storeFilter = "All stores", _engineFilter = "All engines", _statusFilter = "Games it can change";
    bool _onlyCustom, _onlyPending;
    string _defaultProfile = "balanced", _gamingDisplay = "";

    public OptimizerSession? Session { get; private set; }
    public ObservableCollection<GameRow> Games { get; } = new();
    public event Action<GameRow>? RowChangedEvent;
    public event Action? GlobalChanged;
    public event Action? FilterChanged;
    public event Action<GameRow>? RecommendationNeededEvent;

    public List<Choice> ProfileChoices { get; private set; } = new();
    public List<Choice> DisplayChoices { get; private set; } = new();
    public List<Choice> DefaultProfileChoices { get; private set; } = new();
    public List<Choice> GamingDisplayChoices { get; private set; } = new();

    public string HardwareText => Session is null ? "" : Session.HardwareSummary;
    public string DisplayText => Session is null ? "" :
        "Displays: " + string.Join("   ", Session.Hw.Displays.Select(d =>
            $"{d.DeviceName.Replace(@"\\.\", "")}{(d.IsPrimary ? " (main)" : "")} active {d.ActiveModeText}" +
            (d.MaxHz > d.CurrentHz + 0.5 ? $" (supports up to {DisplayMath.Hz(d.MaxHz)} Hz)" : "")));

    public int GlobalOffset
    {
        get => _offset;
        set
        {
            if (!Set(ref _offset, value) || Session is null) return;
            Raise(nameof(GlobalLabel));
            Session.Prefs.GlobalOffset = value;
            Session.Prefs.Save();
            foreach (var r in Games) r.SyncStep();
            GlobalChanged?.Invoke();
        }
    }
    public string GlobalLabel => Prefs.OffsetLabel(_offset);

    public string DefaultProfile
    {
        get => _defaultProfile;
        set
        {
            if (!Set(ref _defaultProfile, value) || Session is null || string.IsNullOrEmpty(value)) return;
            Session.Prefs.DefaultProfileId = value;
            Session.Prefs.Save();
            Raise(nameof(DefaultProfileText));
            foreach (var r in Games) r.SyncStep();
            GlobalChanged?.Invoke();
        }
    }
    public string DefaultProfileText => Session?.Prefs.DefaultProfile.Description ?? "";

    public string GamingDisplay
    {
        get => _gamingDisplay;
        set
        {
            if (!Set(ref _gamingDisplay, value) || Session is null) return;
            Session.Prefs.GamingDisplay = string.IsNullOrEmpty(value) ? null : value;
            Session.Prefs.Save();
            foreach (var r in Games) r.SyncStep();
            GlobalChanged?.Invoke();
        }
    }

    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    // ------------------------------------------------------------------ filters and selection
    public string Search { get => _search; set { if (Set(ref _search, value)) FilterChanged?.Invoke(); } }
    public List<string> StoreFilters { get; } = new() { "All stores", "Steam", "Epic", "GOG", "Added by you" };
    public string StoreFilter { get => _storeFilter; set { if (Set(ref _storeFilter, value)) FilterChanged?.Invoke(); } }
    public List<string> EngineFilters { get; } = new() { "All engines" };
    public string EngineFilter { get => _engineFilter; set { if (Set(ref _engineFilter, value)) FilterChanged?.Invoke(); } }
    public List<string> StatusFilters { get; } = new() { "Games it can change", "All games", "Changes waiting", "Already set", "Skipped", "Running" };
    public string StatusFilter { get => _statusFilter; set { if (Set(ref _statusFilter, value)) FilterChanged?.Invoke(); } }
    public bool OnlyCustom { get => _onlyCustom; set { if (Set(ref _onlyCustom, value)) FilterChanged?.Invoke(); } }
    public bool OnlyPending { get => _onlyPending; set { if (Set(ref _onlyPending, value)) FilterChanged?.Invoke(); } }

    public bool Matches(GameRow r)
    {
        if (_search.Length > 0 && !r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)) return false;
        if (_storeFilter != "All stores" && !r.Game.Sources.Contains(_storeFilter) && r.Game.Store != _storeFilter) return false;
        if (_engineFilter != "All engines" && r.Game.Engine.ToString() != _engineFilter) return false;
        if (_onlyCustom && !r.HasCustomChoices) return false;
        if (_onlyPending && !r.CanOptimize) return false;
        var st = r.Plan?.Status;
        return _statusFilter switch
        {
            "Games it can change" => r.Plan is null || st != PlanStatus.Skipped,
            "Changes waiting" => st == PlanStatus.Ready,
            "Already set" => st == PlanStatus.NothingToChange,
            "Skipped" => st == PlanStatus.Skipped,
            "Running" => st == PlanStatus.Running,
            _ => true,
        };
    }

    public int SelectedCount => Games.Count(g => g.IsSelected);
    public string SelectionText => SelectedCount == 0 ? "No games selected" : $"{SelectedCount} selected";
    public int SupportedCount => Games.Count(g => g.IsSupported);
    public string OptimizeAllText => $"Optimize all supported ({SupportedCount})";
    public string SelectedButtonsText => $"selected ({SelectedCount})";
    public void SelectionChanged() { Raise(nameof(SelectedCount)); Raise(nameof(SelectionText)); Raise(nameof(SelectedButtonsText)); }
    public void CountsChanged() { Raise(nameof(SupportedCount)); Raise(nameof(OptimizeAllText)); }

    // ------------------------------------------------------------------ loading

    public void Load(OptimizerSession s)
    {
        Session = s;
        _offset = s.Prefs.GlobalOffset;
        _defaultProfile = s.Prefs.DefaultProfile.Id;
        _gamingDisplay = s.Prefs.GamingDisplay ?? "";
        RebuildChoices();
        EngineFilters.Clear();
        EngineFilters.Add("All engines");
        EngineFilters.AddRange(s.Scan.Games.Select(g => g.Engine.ToString()).Distinct().Order());
        Games.Clear();
        foreach (var g in s.Scan.Games)
        {
            var row = new GameRow(this, g);
            row.SyncStep();
            Games.Add(row);
        }
        foreach (var n in new[] { nameof(GlobalOffset), nameof(GlobalLabel), nameof(DefaultProfile), nameof(DefaultProfileText), nameof(GamingDisplay),
                                  nameof(HardwareText), nameof(DisplayText), nameof(EngineFilters) })
            Raise(n);
        SelectionChanged();
    }

    public void RebuildChoices()
    {
        if (Session is null) return;
        var p = Session.Prefs;
        ProfileChoices = new List<Choice> { new($"Default ({p.DefaultProfile.Name})", "") }
            .Concat(p.AllProfiles.Select(x => new Choice(x.Name, x.Id))).ToList();
        DefaultProfileChoices = p.AllProfiles.Select(x => new Choice(x.Name, x.Id)).ToList();
        DisplayChoices = new List<Choice> { new("Gaming display", "") }
            .Concat(Session.Hw.Displays.Select(d => new Choice(d.Label, d.DeviceName))).ToList();
        GamingDisplayChoices = new List<Choice> { new($"Main display ({Session.Hw.Primary.ActiveModeText})", "") }
            .Concat(Session.Hw.Displays.Where(d => !d.IsPrimary).Select(d => new Choice(d.Label, d.DeviceName))).ToList();
        if (p.GamingDisplay is { } g && Session.Hw.FindDisplay(g) is null)
            GamingDisplayChoices.Add(new Choice($"{g} (not connected; main display used)", g));
        foreach (var n in new[] { nameof(ProfileChoices), nameof(DefaultProfileChoices), nameof(DisplayChoices), nameof(GamingDisplayChoices), nameof(DisplayText), nameof(DefaultProfileText) })
            Raise(n);
    }

    internal void RowChanged(GameRow row) => RowChangedEvent?.Invoke(row);
    internal void RecommendationNeeded(GameRow row) => RecommendationNeededEvent?.Invoke(row);
}
