using GameOptimizer.Core.Hardware;
using GameOptimizer.Core.Library;
using GameOptimizer.Core.Optimize;
using GameOptimizer.Core.Recommend;
using GameOptimizer.Core.Safety;

const string Usage = """
GameOptimizer command line

  gameopt hw                         Hardware and the PC-wide recommended step
  gameopt scan [--offline]           Installed games, engines, recommended step and what can be done
  gameopt recommend [--game NAME] [--offline]
                                     Each game's recommended step and the reason for it
  gameopt preview [options]          Show every change without writing anything
  gameopt apply [options]            Back up, then write the changes
  gameopt restore [--run ID] [--game NAME]   Undo the latest (or a chosen) run
  gameopt restore --list             List backup runs
  gameopt step                       Show slider choices
  gameopt step --offset N            Shift every game N steps from its recommendation (-3 to 3)
  gameopt step --game NAME STEP|reset
  gameopt shared-cache [add DIR | remove DIR]
                                     Game Spec Check cache folders to reuse requirement data from

Options for preview and apply:
  --game NAME    Only games whose name contains NAME (repeatable). Default: all games.
  --step STEP    Use this step for every selected game instead of the slider choices.
                 Steps: low, medium, balanced, high, veryhigh, ultra (or 0-5).
  --offline      Use cached requirement data only.

Recommendations read each game's published requirements from Steam and PCGamingWiki
(only the Steam app id is sent) and cache them for a week.
""";

Console.OutputEncoding = System.Text.Encoding.UTF8;
var argv = args.ToList();
if (argv.Count == 0 || argv[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
var cmd = argv[0].ToLowerInvariant();
argv.RemoveAt(0);
if (argv.Remove("--offline")) RequirementSource.AllowNetwork = false;

try
{
    return cmd switch
    {
        "hw" => Hw(),
        "scan" => await Scan(),
        "recommend" => await Recommend(argv),
        "preview" => await Optimize(argv, write: false),
        "apply" => await Optimize(argv, write: true),
        "restore" => Restore(argv),
        "step" => Step(argv),
        "shared-cache" => SharedCache(argv),
        _ => Fail($"Unknown command '{cmd}'.\n\n{Usage}"),
    };
}
catch (ArgumentException e) { return Fail(e.Message); }

static int Fail(string msg) { Console.Error.WriteLine(msg); return 2; }

static int Hw()
{
    var hw = HardwareDetector.Detect();
    Console.WriteLine(hw);
    if (hw.Model.Length > 0) Console.WriteLine($"Model: {hw.Model}{(hw.IsLaptop ? " (laptop)" : "")}");
    if (hw.OtherGpus.Count > 0) Console.WriteLine($"Also found: {string.Join(", ", hw.OtherGpus)}");
    var gpu = GpuMatcher.Identify(hw.Gpu);
    Console.WriteLine($"Graphics card score: {(gpu is null ? "not in the table" : $"{gpu.Score} ({gpu.Name})")}");
    Console.WriteLine($"Frame cap: {hw.FpsCap} fps");
    Console.WriteLine($"PC-wide step (used when a game has no requirement data): {hw.Recommended.Label()}");
    foreach (var r in hw.Reasons) Console.WriteLine($"  - {r}");
    return 0;
}

/// <summary>Everything a command needs: hardware, games, rules, prefs and per-game recommendations.</summary>
static async Task<Session> Load(List<string>? filters = null, bool quiet = false)
{
    var s = new Session { Hw = HardwareDetector.Detect(), Scan = LibraryScanner.Scan(), Prefs = Prefs.Load() };
    s.Prefs.ApplySharedCaches();
    var errors = new List<string>();
    s.Rules = RuleSet.Load(errors);
    foreach (var n in s.Scan.Notes.Concat(errors)) Console.WriteLine($"Note: {n}");
    s.Games = filters is { Count: > 0 } ? Select(s.Scan, filters) : s.Scan.Games;

    // Only games the tool can change need a recommendation; that keeps lookups to a minimum.
    var supported = s.Games.Where(g => Planner.Plan(g, s.Hw, s.Hw.Recommended, s.Rules).Status != PlanStatus.Skipped).ToList();
    IProgress<(int done, int total)>? progress = quiet ? null : new Progress<(int done, int total)>(p =>
    {
        Console.Error.Write($"\rReading requirements {p.done}/{p.total}   ");
        if (p.done == p.total) Console.Error.WriteLine();
    });
    s.Recs = await Recommender.RecommendAllAsync(s.Hw, supported, progress);
    return s;
}

static async Task<int> Scan()
{
    var s = await Load();
    Console.WriteLine($"{s.Games.Count} games. Slider: {Prefs.OffsetLabel(s.Prefs.GlobalOffset)}\n");
    foreach (var g in s.Games)
    {
        var plan = Planner.Plan(g, s.Hw, s.StepFor(g), s.Rules);
        var engine = g.Engine + (g.EngineDetail is null ? "" : $" ({g.EngineDetail})");
        var step = plan.Status == PlanStatus.Skipped ? "" : s.StepFor(g).Label() + (s.Prefs.PerGame.ContainsKey(g.Id) ? "*" : "");
        Console.WriteLine($"{Trim(g.Name, 40),-40} {Trim(engine, 34),-34} {step,-10} {plan.StatusText}{(plan.RuleId is null ? "" : $"  [rule: {plan.RuleId}]")}");
    }
    Console.WriteLine("\n* = set by hand for that game. 'gameopt recommend' shows why each step was picked.");
    return 0;
}

static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

static async Task<int> Recommend(List<string> argv)
{
    var filters = new List<string>();
    for (int i = 0; i < argv.Count; i++)
        if (argv[i] == "--game" && i + 1 < argv.Count) filters.Add(argv[++i]);
        else throw new ArgumentException($"Unexpected argument '{argv[i]}'.");
    var s = await Load(filters);
    foreach (var g in s.Games)
    {
        if (!s.Recs.TryGetValue(g.Id, out var r)) continue;
        Console.WriteLine($"{Trim(g.Name, 40),-40} {r.Step.Label(),-10} {r.Reason}");
        if (filters.Count > 0)
            foreach (var l in GameLinks.For(g)) Console.WriteLine($"{"",-51}{l.Label}: {l.Url}");
    }
    return 0;
}

static (List<string> games, QualityStep? step) ParseOptions(List<string> argv)
{
    var games = new List<string>();
    QualityStep? step = null;
    for (int i = 0; i < argv.Count; i++)
    {
        switch (argv[i])
        {
            case "--game" when i + 1 < argv.Count: games.Add(argv[++i]); break;
            case "--step" when i + 1 < argv.Count:
                if (!QualitySteps.TryParse(argv[++i], out var s)) throw new ArgumentException($"Unknown step '{argv[i]}'.");
                step = s; break;
            default: throw new ArgumentException($"Unexpected argument '{argv[i]}'.");
        }
    }
    return (games, step);
}

static List<GameInfo> Select(ScanResult scan, List<string> filters)
{
    var hits = scan.Games.Where(g => filters.Any(f => g.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || g.Id.Equals(f, StringComparison.OrdinalIgnoreCase))).ToList();
    if (hits.Count == 0) throw new ArgumentException($"No installed game matches {string.Join(", ", filters)}.");
    return hits;
}

static async Task<int> Optimize(List<string> argv, bool write)
{
    var (filters, forced) = ParseOptions(argv);
    var s = await Load(filters);
    var images = ProcessGuard.RunningImages();

    var plans = new List<GamePlan>();
    foreach (var g in s.Games)
    {
        var plan = Planner.Plan(g, s.Hw, forced ?? s.StepFor(g), s.Rules);
        if (plan.HasChanges && ProcessGuard.RunningFrom(g.InstallDir, images) is not null) plan.Status = PlanStatus.Running;
        plans.Add(plan);
        if (filters.Count == 0 && plan.Status is PlanStatus.Skipped or PlanStatus.NothingToChange) continue;
        Print(plan, s.Recs.GetValueOrDefault(g.Id));
    }
    if (filters.Count == 0)
    {
        var quiet = plans.Count(p => p.Status is PlanStatus.Skipped or PlanStatus.NothingToChange);
        Console.WriteLine($"{quiet} other game(s) need nothing or were skipped. Use 'gameopt scan' to see why.");
    }

    var ready = plans.Where(p => p.Status == PlanStatus.Ready).ToList();
    if (!write)
    {
        Console.WriteLine($"\nPreview only. {ready.Count} game(s) ready, {ready.Sum(p => p.ChangeCount)} change(s). Run 'gameopt apply' with the same options to write them.");
        return 0;
    }
    if (ready.Count == 0) { Console.WriteLine("\nNothing to apply."); return 0; }
    var res = Applier.Apply(ready, forced is null ? "Slider choices" : $"Step {forced.Value.Label()}");
    Console.WriteLine();
    foreach (var a in res.Applied) Console.WriteLine($"Applied   {a}");
    foreach (var r in res.Refused) Console.WriteLine($"Refused   {r}");
    foreach (var e in res.Errors) Console.WriteLine($"Error     {e}");
    if (res.RunId is not null) Console.WriteLine($"\nBackup run {res.RunId}. Undo with: gameopt restore --run {res.RunId}");
    return res.Errors.Count > 0 ? 1 : 0;
}

static void Print(GamePlan p, GameRecommendation? rec)
{
    Console.WriteLine($"\n== {p.Game.Name} ({p.Game.Engine}, step {p.Step.Label()}): {p.StatusText}");
    if (rec is not null) Console.WriteLine($"   recommended {rec.Step.Label()}: {rec.Reason}");
    foreach (var l in GameLinks.For(p.Game)) Console.WriteLine($"   {l.Label}: {l.Url}");
    foreach (var f in p.Files)
    {
        Console.WriteLine($"   {f.Path}");
        foreach (var c in f.Changes) Console.WriteLine($"      {c}");
    }
    if (p.Registry.Count > 0)
    {
        Console.WriteLine($"   HKCU\\{p.Registry[0].SubKey}");
        foreach (var r in p.Registry) Console.WriteLine($"      {r.Display}");
    }
    foreach (var s in p.Skipped) Console.WriteLine($"   skipped: {s}");
    foreach (var w in p.Warnings) Console.WriteLine($"   warning: {w}");
}

static int Restore(List<string> argv)
{
    var runs = Applier.Runs();
    if (argv.Contains("--list"))
    {
        if (runs.Count == 0) Console.WriteLine("No backups yet.");
        foreach (var r in runs)
            Console.WriteLine($"{r.Id}  {r.Created:g}  {r.Summary}  ({string.Join(", ", r.Entries.Select(e => e.GameName).Distinct())})");
        return 0;
    }
    string? runId = null, game = null;
    for (int i = 0; i < argv.Count; i++)
    {
        if (argv[i] == "--run" && i + 1 < argv.Count) runId = argv[++i];
        else if (argv[i] == "--game" && i + 1 < argv.Count) game = argv[++i];
        else throw new ArgumentException($"Unexpected argument '{argv[i]}'.");
    }
    var run = runId is null ? runs.FirstOrDefault() : runs.FirstOrDefault(r => r.Id == runId);
    if (run is null) return Fail(runId is null ? "No backups to restore." : $"No backup run '{runId}'. See 'gameopt restore --list'.");
    var res = Applier.Restore(run, game);
    foreach (var a in res.Applied) Console.WriteLine($"Restored  {a}");
    foreach (var r in res.Refused) Console.WriteLine($"Refused   {r}");
    foreach (var e in res.Errors) Console.WriteLine($"Note      {e}");
    if (res.Applied.Count + res.Refused.Count == 0) Console.WriteLine("Nothing in that run matched.");
    return res.Refused.Count > 0 ? 1 : 0;
}

static int Step(List<string> argv)
{
    var prefs = Prefs.Load();
    if (argv.Count == 0)
    {
        Console.WriteLine($"All games: {Prefs.OffsetLabel(prefs.GlobalOffset)}");
        foreach (var (id, s) in prefs.PerGame) Console.WriteLine($"  {id}: {s.Label()} (set by hand)");
        return 0;
    }
    if (argv is ["--offset", var o])
    {
        if (!int.TryParse(o, out var n) || Math.Abs(n) > Prefs.MaxOffset) throw new ArgumentException($"Offset must be -{Prefs.MaxOffset} to {Prefs.MaxOffset}.");
        prefs.GlobalOffset = n;
    }
    else if (argv is ["--game", var name, var val])
    {
        var game = Select(LibraryScanner.Scan(), new() { name });
        if (game.Count > 1) throw new ArgumentException($"'{name}' matches {game.Count} games: {string.Join(", ", game.Select(x => x.Name))}.");
        if (val.Equals("reset", StringComparison.OrdinalIgnoreCase)) prefs.PerGame.Remove(game[0].Id);
        else if (QualitySteps.TryParse(val, out var s)) prefs.PerGame[game[0].Id] = s;
        else throw new ArgumentException($"Unknown step '{val}'.");
    }
    else throw new ArgumentException("Use: gameopt step --offset N, or gameopt step --game NAME STEP|reset");
    prefs.Save();
    Console.WriteLine("Saved.");
    return 0;
}

static int SharedCache(List<string> argv)
{
    var prefs = Prefs.Load();
    if (argv is ["add", var dir])
    {
        var full = Path.GetFullPath(dir);
        if (!Directory.Exists(full)) throw new ArgumentException($"{full} does not exist.");
        if (!prefs.SharedCacheDirs.Contains(full, StringComparer.OrdinalIgnoreCase)) prefs.SharedCacheDirs.Add(full);
        prefs.Save();
    }
    else if (argv is ["remove", var d])
    {
        prefs.SharedCacheDirs.RemoveAll(x => x.Equals(Path.GetFullPath(d), StringComparison.OrdinalIgnoreCase));
        prefs.Save();
    }
    else if (argv.Count > 0) throw new ArgumentException("Use: gameopt shared-cache [add DIR | remove DIR]");
    prefs.ApplySharedCaches();
    Console.WriteLine("Requirement data is read from:");
    Console.WriteLine($"  {RequirementSource.CacheDir} (own cache)");
    foreach (var d in RequirementSource.SharedCacheDirs)
        Console.WriteLine($"  {d}{(Directory.Exists(d) ? "" : " (not found)")} (Game Spec Check, read only)");
    return 0;
}

sealed class Session
{
    public required HardwareInfo Hw { get; init; }
    public required ScanResult Scan { get; init; }
    public required Prefs Prefs { get; init; }
    public IReadOnlyList<GameRule> Rules { get; set; } = Array.Empty<GameRule>();
    public List<GameInfo> Games { get; set; } = new();
    public Dictionary<string, GameRecommendation> Recs { get; set; } = new();

    public QualityStep Recommended(GameInfo g) => Recs.TryGetValue(g.Id, out var r) ? r.Step : Hw.Recommended;
    public QualityStep StepFor(GameInfo g) => Prefs.StepFor(g.Id, Recommended(g));
}
