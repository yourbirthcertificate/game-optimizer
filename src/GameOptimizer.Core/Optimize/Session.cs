using System.Globalization;
using GameOptimizer.Core.Bench;
using GameOptimizer.Core.Hardware;
using GameOptimizer.Core.Library;
using GameOptimizer.Core.Recommend;
using GameOptimizer.Core.Safety;

namespace GameOptimizer.Core.Optimize;

/// <summary>
/// One loaded state of the tool: hardware, games, rules, preferences and recommendations. The CLI and
/// the app both go through it, so the same choices give the same plans.
/// </summary>
public sealed class OptimizerSession
{
    public required HardwareInfo Hw { get; set; }
    public required ScanResult Scan { get; set; }
    public required Prefs Prefs { get; set; }
    public List<GameRule> Rules { get; set; } = new();
    public List<string> Notes { get; } = new();
    public Dictionary<string, GameRecommendation> Recs { get; } = new();

    public static OptimizerSession Load(HardwareInfo? hw = null)
    {
        var prefs = Prefs.Load();
        prefs.ApplySharedCaches();
        var s = new OptimizerSession { Hw = hw ?? HardwareDetector.Detect(), Scan = LibraryScanner.Scan(prefs.ManualGames), Prefs = prefs };
        var errors = new List<string>();
        s.Rules = RuleSet.Load(errors);
        s.Notes.AddRange(s.Scan.Notes);
        s.Notes.AddRange(errors);
        return s;
    }

    public void Rescan()
    {
        Scan = LibraryScanner.Scan(Prefs.ManualGames);
        var errors = new List<string>();
        Rules = RuleSet.Load(errors);
        Notes.Clear();
        Notes.AddRange(Scan.Notes);
        Notes.AddRange(errors);
    }

    public void RefreshDisplays() => Hw.Displays = DisplayDetector.Detect();

    public GameInfo? Find(string idOrName) =>
        Scan.Games.FirstOrDefault(g => g.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
        ?? Scan.Games.FirstOrDefault(g => g.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
        ?? (Scan.Games.Where(g => g.Name.Contains(idOrName, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } one ? one[0] : null);

    public List<GameInfo> Matching(string text) =>
        Scan.Games.Where(g => g.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || g.Id.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Steam app id used for requirement lookups: the one you linked, else the store's own.</summary>
    public string? AppIdFor(GameInfo g) => Prefs.GameOrNull(g.Id)?.SteamAppId ?? g.SteamAppId;
    public string AppIdSource(GameInfo g) => Prefs.GameOrNull(g.Id)?.SteamAppId is not null ? "linked by you" : g.SteamAppId is not null ? "from Steam" : "none";

    public QualityStep Recommended(GameInfo g) => Recs.TryGetValue(g.Id, out var r) ? r.Step : Hw.Recommended;

    public (int w, int h) TargetResolution(GameInfo g)
    {
        var d = OptimizeRequest.DisplayFor(Prefs, g.Id, Hw);
        return OptimizeRequest.ResolutionFor(Prefs, g.Id, d) ?? (d.CurrentWidth, d.CurrentHeight);
    }

    public OptimizeRequest RequestFor(GameInfo g, IEnumerable<string>? temporaryExcluded = null, QualityStep? forcedStep = null) =>
        OptimizeRequest.Resolve(Prefs, g, Hw, Recommended(g), temporaryExcluded, forcedStep);

    public GamePlan PlanFor(GameInfo g, IEnumerable<string>? temporaryExcluded = null, QualityStep? forcedStep = null, IReadOnlyList<string>? runningImages = null)
    {
        GamePlan plan;
        var req = RequestFor(g, temporaryExcluded, forcedStep);
        try { plan = Planner.Plan(g, Hw, req, Rules); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            plan = new GamePlan { Game = g, Step = req.Step, Request = req, Status = PlanStatus.Skipped };
            plan.Skip(SkipKind.LocationNotFound, "Could not read settings: " + e.Message);
        }
        if (runningImages is not null && ProcessGuard.RunningFrom(g.InstallDir, runningImages) is not null)
        {
            if (plan.HasChanges) plan.Status = PlanStatus.Running;
            plan.Actions.Add(PlanAction.CloseGame);
        }
        return plan;
    }

    /// <summary>Only games the tool can change get a lookup; that keeps requests to a minimum.</summary>
    public async Task LoadRecommendationsAsync(IEnumerable<GameInfo>? games = null, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var list = (games ?? Scan.Games).Where(g => PlanFor(g).Status != PlanStatus.Skipped).ToList();
        var recs = await Recommender.RecommendAllAsync(Hw, list, progress, ct, AppIdFor, TargetResolution);
        foreach (var (id, r) in recs)
        {
            if (BenchmarkStore.PreferredFor(id) is { } b)
                r.Measured = $"{b.Summary} ({b.Label}, {b.ProfileName ?? "?"} / {b.Step ?? "?"}, {b.Imported:d})";
            Recs[id] = r;
        }
    }

    public string HardwareSummary =>
        $"{Hw.Gpu} ({Hw.GpuVramGb.ToString("0.#", CultureInfo.InvariantCulture)} GB), {Hw.Cpu}, {Hw.RamGb.ToString("0.#", CultureInfo.InvariantCulture)} GB RAM";
}

/// <summary>"key=value" choices shared by the CLI for profiles and games.</summary>
public static class SettingsParser
{
    public const string Help = """
          quality=offset:N | fixed:STEP | inherit   (N from -3 to 3; STEP low..ultra)
          fps=keep | auto | uncapped | N | inherit  (N in fps, e.g. 60 or 59.94)
          resolution=keep | native | current | WxH | inherit
          displaymode=keep | recommended | fullscreen | borderless | windowed | inherit
          vsync=keep | on | off | inherit
          display=\\.\DISPLAYn | inherit           (never changes Windows settings)
          preserve=resolution,displayMode,vsync,fpsCap,renderScale,texture,quality | none | inherit
        """;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Apply(SettingsPrefs s, string assignment)
    {
        int eq = assignment.IndexOf('=');
        if (eq <= 0) throw new ArgumentException($"'{assignment}' should look like key=value.");
        var key = assignment[..eq].Trim().ToLowerInvariant();
        var val = assignment[(eq + 1)..].Trim();
        bool inherit = val.Equals("inherit", StringComparison.OrdinalIgnoreCase);
        switch (key)
        {
            case "quality":
                if (inherit) { s.Quality = null; break; }
                if (val.StartsWith("offset:", StringComparison.OrdinalIgnoreCase) && int.TryParse(val[7..], out var off) && Math.Abs(off) <= Prefs.MaxOffset)
                    s.Quality = new QualityPref { Mode = QualityMode.Offset, Offset = off };
                else if (val.StartsWith("fixed:", StringComparison.OrdinalIgnoreCase) && QualitySteps.TryParse(val[6..], out var st))
                    s.Quality = new QualityPref { Mode = QualityMode.Fixed, Step = st };
                else throw new ArgumentException("quality must be offset:N (-3 to 3), fixed:STEP or inherit.");
                break;
            case "fps":
                if (inherit) { s.Fps = null; break; }
                s.Fps = val.ToLowerInvariant() switch
                {
                    "keep" => new FpsPolicy { Mode = FpsMode.Keep },
                    "auto" => new FpsPolicy { Mode = FpsMode.Auto },
                    "uncapped" or "none" or "off" => new FpsPolicy { Mode = FpsMode.Uncapped },
                    _ when double.TryParse(val, NumberStyles.Float, Inv, out var f) && f is >= 10 and <= 1000 => new FpsPolicy { Mode = FpsMode.Custom, Value = f },
                    _ => throw new ArgumentException("fps must be keep, auto, uncapped, a number from 10 to 1000, or inherit."),
                };
                break;
            case "resolution":
                if (inherit) { s.Resolution = null; break; }
                s.Resolution = val.ToLowerInvariant() switch
                {
                    "keep" => new ResolutionPref { Mode = ResolutionMode.Keep },
                    "native" => new ResolutionPref { Mode = ResolutionMode.Native },
                    "current" => new ResolutionPref { Mode = ResolutionMode.Current },
                    _ when val.Split('x', 'X') is [var a, var b] && int.TryParse(a, out var w) && int.TryParse(b, out var h) && w > 0 && h > 0
                        => new ResolutionPref { Mode = ResolutionMode.Custom, Width = w, Height = h },
                    _ => throw new ArgumentException("resolution must be keep, native, current, WxH or inherit."),
                };
                break;
            case "displaymode":
                if (inherit) { s.DisplayMode = null; break; }
                s.DisplayMode = Enum.TryParse<DisplayModeChoice>(val, true, out var dm) ? dm
                    : throw new ArgumentException("displaymode must be keep, recommended, fullscreen, borderless, windowed or inherit.");
                break;
            case "vsync":
                if (inherit) { s.VSync = null; break; }
                s.VSync = Enum.TryParse<VSyncChoice>(val, true, out var vs) ? vs : throw new ArgumentException("vsync must be keep, on, off or inherit.");
                break;
            case "display":
                s.Display = inherit || val.Length == 0 ? null : val;
                break;
            case "preserve":
                if (inherit) { s.Preserve = null; break; }
                if (val.Equals("none", StringComparison.OrdinalIgnoreCase)) { s.Preserve = new(); break; }
                var cats = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                var bad = cats.Where(c => !Categories.Keepable.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                if (bad.Count > 0) throw new ArgumentException($"Unknown categories: {string.Join(", ", bad)}. Use {string.Join(", ", Categories.Keepable)}.");
                s.Preserve = cats.Select(c => Categories.Keepable.First(k => k.Equals(c, StringComparison.OrdinalIgnoreCase))).ToList();
                break;
            default:
                throw new ArgumentException($"Unknown setting '{key}'. Settings:\n{Help}");
        }
    }

    public static IEnumerable<string> Describe(SettingsPrefs s, DisplayInfo display)
    {
        if (s.Quality is { } q) yield return "Quality: " + (q.Mode == QualityMode.Fixed ? $"fixed at {q.Step.Label()}" : q.Offset == 0 ? "each game's recommendation" : $"recommendation {q.Offset:+0;-0} step(s)");
        if (s.Fps is { } f) yield return "Frame cap: " + f.Describe(display);
        if (s.Resolution is { } r) yield return "Resolution: " + (r.Mode == ResolutionMode.Custom ? $"{r.Width}x{r.Height}" : r.Mode switch
        {
            ResolutionMode.Keep => "keep the game's", ResolutionMode.Current => "the display's current resolution", _ => "the display's native resolution",
        });
        if (s.DisplayMode is { } dm) yield return "Display mode: " + dm switch
        {
            DisplayModeChoice.Keep => "keep the game's", DisplayModeChoice.Recommended => "the engine's usual fullscreen (Unreal exclusive, Unity borderless)",
            _ => dm.ToString().ToLowerInvariant(),
        };
        if (s.VSync is { } v) yield return "VSync: " + (v == VSyncChoice.Keep ? "keep the game's" : v.ToString().ToLowerInvariant());
        if (s.Display is { } d) yield return "Display: " + d;
        if (s.Preserve is { Count: > 0 } p) yield return "Always keep: " + string.Join(", ", p.Select(Categories.Label));
    }
}
