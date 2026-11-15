using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GameOptimizer.Core.Hardware;
using GameOptimizer.Core.Library;
using GameOptimizer.Core.Optimize;

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

public sealed class Relay(Action<object?> run, Func<object?, bool>? can = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => can?.Invoke(p) ?? true;
    public void Execute(object? p) => run(p);
}

/// <summary>One game in the list, with its own slider.</summary>
public sealed class GameRow : Observable
{
    readonly MainViewModel _owner;
    int _step;
    bool _isCustom;
    GamePlan? _plan;

    public GameRow(MainViewModel owner, GameInfo game, int step, bool isCustom)
    { _owner = owner; Game = game; _step = step; _isCustom = isCustom; }

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
            Raise(nameof(StepLabel));
            IsCustom = true;
            _owner.OnRowStepChanged(this);
        }
    }

    /// <summary>Moves the slider without marking the game custom (the global slider moved).</summary>
    public void FollowGlobal(int step)
    {
        if (Set(ref _step, step, nameof(Step))) Raise(nameof(StepLabel));
    }

    public string StepLabel => ((QualityStep)_step).Label();

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
    int _globalStep = (int)QualityStep.Balanced;
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
        set { Set(ref _hw, value); Raise(nameof(HardwareText)); Raise(nameof(RecommendedText)); Raise(nameof(RecommendedStep)); }
    }

    public string HardwareText => _hw is null ? "" :
        $"{_hw.Gpu} ({_hw.GpuVramGb:0.#} GB)  ·  {_hw.Cpu}  ·  {_hw.RamGb:0.#} GB RAM  ·  {_hw.NativeWidth}x{_hw.NativeHeight} at {_hw.MaxRefreshHz} Hz";

    public string RecommendedText => _hw is null ? "" : $"Recommended for this PC: {_hw.Recommended.Label()}. Frames are capped at {_hw.FpsCap} fps.";
    public int RecommendedStep => (int)(_hw?.Recommended ?? QualityStep.Balanced);

    public int GlobalStep
    {
        get => _globalStep;
        set
        {
            if (!Set(ref _globalStep, value)) return;
            Raise(nameof(GlobalLabel));
            Prefs.GlobalStep = (QualityStep)value;
            Prefs.Save();
            foreach (var r in Games.Where(r => !r.IsCustom)) r.FollowGlobal(value);
            GlobalChanged?.Invoke();
        }
    }

    public string GlobalLabel => ((QualityStep)_globalStep).Label() + (_globalStep == RecommendedStep ? " (recommended)" : "");

    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    public bool OnlySupported { get => _onlySupported; set => Set(ref _onlySupported, value); }

    public void Load(HardwareInfo hw, ScanResult scan, IReadOnlyList<GameRule> rules)
    {
        Prefs = GameOptimizer.Core.Optimize.Prefs.Load();
        Hardware = hw;
        Rules = rules;
        _globalStep = (int)(Prefs.GlobalStep ?? hw.Recommended);
        Raise(nameof(GlobalStep)); Raise(nameof(GlobalLabel));
        Games.Clear();
        foreach (var g in scan.Games)
        {
            bool custom = Prefs.PerGame.TryGetValue(g.Id, out var s);
            Games.Add(new GameRow(this, g, (int)(custom ? s : (QualityStep)_globalStep), custom));
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
        row.FollowGlobal(_globalStep);
        RowChanged?.Invoke(row);
    }
}
