using System.Collections.ObjectModel;
using System.ComponentModel;
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

/// <summary>One game in the list, with its own slider and its own recommended step.</summary>
public sealed class GameRow : Observable
{
    readonly MainViewModel _owner;
    int _step;
    bool _isCustom;
    GamePlan? _plan;
    GameRecommendation? _rec;

    public GameRow(MainViewModel owner, GameInfo game, int step, bool isCustom, QualityStep pcStep)
    {
        _owner = owner; Game = game; _step = step; _isCustom = isCustom;
        Recommended = pcStep;
    }

    public GameInfo Game { get; }
    public string Name => Game.Name;
    public string EngineText => Game.Engine == GameEngine.Unknown ? "Unknown engine" : Game.Engine.ToString();

    /// <summary>Bound to the row slider. A change made by the user marks the game as custom.</summary>
    public int Step
    {
        get => _step;
        set
        {
            if (!Set(ref _step, value)) return;
            Raise(nameof(StepLabel)); Raise(nameof(AtRecommended));
            IsCustom = true;
            _owner.OnRowStepChanged(this);
        }
    }

    /// <summary>Moves the slider without marking the game custom (the global slider or the recommendation moved).</summary>
    public void Follow(int offset)
    {
        if (Set(ref _step, (int)Prefs.Shift(Recommended, offset), nameof(Step)))
        { Raise(nameof(StepLabel)); Raise(nameof(AtRecommended)); }
    }

    public string StepLabel => ((QualityStep)_step).Label();

    public QualityStep Recommended { get; private set; }

    public void SetRecommendation(GameRecommendation rec)
    {
        _rec = rec;
        Recommended = rec.Step;
        Raise(nameof(Recommended)); Raise(nameof(RecText)); Raise(nameof(RecReason)); Raise(nameof(AtRecommended));
    }

    public string RecText => _rec is null ? "Recommended: checking..." : $"Recommended: {Recommended.Label()}";
    public string RecReason => _rec?.Reason ?? "Looking up this game's published requirements";
    public bool AtRecommended => _step == (int)Recommended;

    public bool IsCustom { get => _isCustom; set => Set(ref _isCustom, value); }

    public GamePlan? Plan
    {
        get => _plan;
        set
        {
            Set(ref _plan, value);
            Raise(nameof(StatusText)); Raise(nameof(CanOptimize)); Raise(nameof(HasWarning)); Raise(nameof(IsSupported));
        }
    }

    public string StatusText => _plan?.StatusText ?? "Checking...";
    public bool CanOptimize => _plan?.Status == PlanStatus.Ready;
    public bool IsSupported => _plan is not null && _plan.Status != PlanStatus.Skipped;
    public bool HasWarning => _plan?.Warnings.Count > 0;
}

public sealed class MainViewModel : Observable
{
    HardwareInfo? _hw;
    int _offset;
    bool _busy = true;
    string _status = "Reading hardware and scanning games...";
    bool _onlySupported = true;

    public Prefs Prefs { get; private set; } = new();
    public IReadOnlyList<GameRule> Rules { get; set; } = Array.Empty<GameRule>();
    public ObservableCollection<GameRow> Games { get; } = new();
    public event Action<GameRow>? RowChanged;
    public event Action? GlobalChanged;

    public HardwareInfo? Hardware
    {
        get => _hw;
        set { Set(ref _hw, value); Raise(nameof(HardwareText)); Raise(nameof(HintText)); }
    }

    public string HardwareText => _hw is null ? "" :
        $"{_hw.Gpu} ({_hw.GpuVramGb:0.#} GB)  ·  {_hw.Cpu}  ·  {_hw.RamGb:0.#} GB RAM  ·  {_hw.NativeWidth}x{_hw.NativeHeight} at {_hw.MaxRefreshHz} Hz";

    public string HintText => _hw is null ? "" :
        $"Each game starts at the step its published requirements suggest for this PC. Frames are capped at {_hw.FpsCap} fps.";

    /// <summary>-3 (more performance) to +3 (more quality), applied to every game you have not set by hand.</summary>
    public int GlobalOffset
    {
        get => _offset;
        set
        {
            if (!Set(ref _offset, value)) return;
            Raise(nameof(GlobalLabel));
            Prefs.GlobalOffset = value;
            Prefs.Save();
            foreach (var r in Games.Where(r => !r.IsCustom)) r.Follow(value);
            GlobalChanged?.Invoke();
        }
    }

    public string GlobalLabel => Prefs.OffsetLabel(_offset);

    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool OnlySupported { get => _onlySupported; set => Set(ref _onlySupported, value); }

    public void Load(HardwareInfo hw, ScanResult scan, IReadOnlyList<GameRule> rules)
    {
        Prefs = GameOptimizer.Core.Optimize.Prefs.Load();
        Prefs.ApplySharedCaches();
        Hardware = hw;
        Rules = rules;
        _offset = Prefs.GlobalOffset;
        Raise(nameof(GlobalOffset)); Raise(nameof(GlobalLabel));
        Games.Clear();
        foreach (var g in scan.Games)
        {
            bool custom = Prefs.PerGame.TryGetValue(g.Id, out var s);
            Games.Add(new GameRow(this, g, (int)(custom ? s : Prefs.Shift(hw.Recommended, _offset)), custom, hw.Recommended));
        }
    }

    /// <summary>Per-game recommendations arrived: rows that follow the global slider move to them.</summary>
    public void SetRecommendations(IReadOnlyDictionary<string, GameRecommendation> recs)
    {
        foreach (var r in Games)
        {
            if (!recs.TryGetValue(r.Game.Id, out var rec)) continue;
            r.SetRecommendation(rec);
            if (!r.IsCustom) r.Follow(_offset);
        }
    }

    internal void OnRowStepChanged(GameRow row)
    {
        Prefs.PerGame[row.Game.Id] = (QualityStep)row.Step;
        Prefs.Save();
        RowChanged?.Invoke(row);
    }

    public void ResetRow(GameRow row)
    {
        row.IsCustom = false;
        Prefs.PerGame.Remove(row.Game.Id);
        Prefs.Save();
        row.Follow(_offset);
        RowChanged?.Invoke(row);
    }
}
