using System.Globalization;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Recommend;

namespace OptiGame.Core.Optimize;

/// <summary>
/// Everything one game's plan depends on, resolved from preferences. The GUI and the CLI both build
/// plans from this, so the same choices give the same result.
/// </summary>
public sealed class OptimizeRequest
{
    public QualityStep Step { get; init; }
    public string StepSource { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public required DisplayInfo Display { get; init; }
    public FpsMode FpsMode { get; init; } = FpsMode.Auto;
    /// <summary>The cap in fps for Auto and Custom; null for Keep and Uncapped.</summary>
    public double? FpsCap { get; init; }
    /// <summary>An optional advisory FPS target. Independent of the cap used when applying settings.</summary>
    public double? RecommendationTargetFps { get; init; }
    public RecommendationPriority RecommendationPriority { get; init; }
    /// <summary>Target resolution; null = keep the game's.</summary>
    public (int w, int h)? Resolution { get; init; }
    public DisplayModeChoice DisplayMode { get; init; } = DisplayModeChoice.Recommended;
    public VSyncChoice VSync { get; init; } = VSyncChoice.Off;
    /// <summary>Categories to keep as they are.</summary>
    public HashSet<string> Preserve { get; init; } = new();
    /// <summary>Setting ids kept for this game every time.</summary>
    public HashSet<string> Excluded { get; init; } = new();
    /// <summary>Setting ids left out of this one operation only.</summary>
    public HashSet<string> TemporaryExcluded { get; init; } = new();
    public List<string> Notes { get; init; } = new();

    /// <summary>The fps text used in previews ("162", "no cap").</summary>
    public string FpsText => FpsMode switch
    {
        FpsMode.Keep => "kept", FpsMode.Uncapped => "no cap",
        _ => FpsCap?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?",
    };

    /// <summary>The old fixed behaviour: native resolution, recommended display mode, VSync off, automatic cap.</summary>
    public static OptimizeRequest Default(HardwareInfo hw, QualityStep step) => new()
    {
        Step = step, StepSource = "given", ProfileName = "Balanced", Display = hw.Primary,
        FpsMode = FpsMode.Auto, FpsCap = hw.FpsCap, Resolution = (hw.NativeWidth, hw.NativeHeight),
        DisplayMode = DisplayModeChoice.Recommended, VSync = VSyncChoice.Off,
    };

    /// <summary>The display a game uses: its own choice, then the gaming display, then the main display.</summary>
    public static DisplayInfo DisplayFor(Prefs prefs, string gameId, HardwareInfo hw, List<string>? notes = null)
    {
        var wanted = prefs.GameOrNull(gameId)?.Display ?? prefs.FindProfile(prefs.GameOrNull(gameId)?.ProfileId)?.Display
                     ?? prefs.DefaultProfile.Display ?? prefs.GamingDisplay;
        if (wanted is null) return hw.Primary;
        var d = hw.FindDisplay(wanted);
        if (d is null) notes?.Add($"The chosen display {wanted} is not connected; using the main display instead.");
        return d ?? hw.Primary;
    }

    /// <summary>Target resolution for a game on its display (null = keep).</summary>
    public static (int w, int h)? ResolutionFor(Prefs prefs, string gameId, DisplayInfo display, List<string>? notes = null)
    {
        var r = prefs.Pick(gameId, s => s.Resolution) ?? new ResolutionPref();
        switch (r.Mode)
        {
            case ResolutionMode.Keep: return null;
            case ResolutionMode.Current: return (display.CurrentWidth, display.CurrentHeight);
            case ResolutionMode.Custom when r.Width > 0 && r.Height > 0:
                if (display.Supports(r.Width, r.Height)) return (r.Width, r.Height);
                notes?.Add($"{r.Width}x{r.Height} is not a mode of {display.DeviceName}; resolution kept.");
                return null;
            default: return display.Native;
        }
    }

    public static OptimizeRequest Resolve(Prefs prefs, GameInfo game, HardwareInfo hw, QualityStep recommended,
        IEnumerable<string>? temporaryExcluded = null, QualityStep? forcedStep = null, double? recommendationTargetFps = null,
        RecommendationPriority recommendationPriority = RecommendationPriority.Balanced)
    {
        var notes = new List<string>();
        var display = DisplayFor(prefs, game.Id, hw, notes);
        var (step, source) = forcedStep is { } f ? (f, "chosen for this run") : prefs.StepWithSource(game.Id, recommended);
        var fps = prefs.Pick(game.Id, s => s.Fps) ?? new FpsPolicy();
        double? cap = fps.Mode switch
        {
            FpsMode.Auto => display.AutoFpsCap,
            FpsMode.Custom when fps.Value is > 0 => fps.Value,
            _ => null,
        };
        var mode = fps.Mode == FpsMode.Custom && cap is null ? FpsMode.Keep : fps.Mode;
        if (fps.Mode == FpsMode.Custom && cap is null) notes.Add("Custom frame cap has no value; cap kept.");
        var g = prefs.GameOrNull(game.Id);
        return new OptimizeRequest
        {
            Step = step, StepSource = source, ProfileName = prefs.ProfileFor(game.Id).Name, Display = display,
            RecommendationPriority = recommendationPriority,
            FpsMode = mode, FpsCap = cap, RecommendationTargetFps = recommendationTargetFps,
            Resolution = ResolutionFor(prefs, game.Id, display, notes),
            DisplayMode = prefs.PickValue(game.Id, s => s.DisplayMode),
            VSync = prefs.PickValue(game.Id, s => s.VSync),
            Preserve = (prefs.Pick(game.Id, s => s.Preserve) ?? new()).ToHashSet(),
            Excluded = (g?.Excluded ?? new()).ToHashSet(),
            TemporaryExcluded = (temporaryExcluded ?? Array.Empty<string>()).ToHashSet(),
            Notes = notes,
        };
    }

    /// <summary>Null when the change goes ahead, otherwise why it is kept.</summary>
    public string? WhyKept(string id, string category)
    {
        if (TemporaryExcluded.Contains(id)) return "unticked for this run";
        if (Excluded.Contains(id)) return "kept for this game";
        if (Preserve.Contains(category)) return $"kept by profile {ProfileName}";
        return null;
    }
}
