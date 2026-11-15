using GameOptimizer.Core;
using GameOptimizer.Core.Hardware;
using GameOptimizer.Core.Library;
using GameOptimizer.Core.Optimize;
using GameOptimizer.Core.Safety;

const string Usage = """
GameOptimizer command line

  gameopt hw                         Show the hardware and the recommended slider step
  gameopt scan                       List installed games, engines and what can be optimized
  gameopt preview [options]          Show every change without writing anything
  gameopt apply [options]            Back up, then write the changes
  gameopt restore [--run ID] [--game NAME]   Undo the latest (or a chosen) run
  gameopt restore --list             List backup runs
  gameopt step                       Show slider choices
  gameopt step --global STEP|recommended
  gameopt step --game NAME STEP|reset

Options for preview and apply:
  --game NAME    Only games whose name contains NAME (repeatable). Default: all games.
  --step STEP    Use this step instead of the saved slider choices.
                 Steps: low, medium, balanced, high, veryhigh, ultra (or 0-5).
""";

Console.OutputEncoding = System.Text.Encoding.UTF8;
var argv = args.ToList();
if (argv.Count == 0 || argv[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
var cmd = argv[0].ToLowerInvariant();
argv.RemoveAt(0);

try
{
    return cmd switch
    {
        "hw" => Hw(),
        "scan" => Scan(),
        "preview" => Optimize(argv, write: false),
        "apply" => Optimize(argv, write: true),
        "restore" => Restore(argv),
        "step" => Step(argv),
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
    Console.WriteLine($"Frame cap: {hw.FpsCap} fps");
    Console.WriteLine($"Recommended step: {hw.Recommended.Label()} (score {hw.Score:0.00})");
    foreach (var r in hw.Reasons) Console.WriteLine($"  - {r}");
    return 0;
}

static int Scan()
{
    var hw = HardwareDetector.Detect();
    var scan = LibraryScanner.Scan();
    var errors = new List<string>();
    var rules = RuleSet.Load(errors);
    var prefs = Prefs.Load();
    foreach (var n in scan.Notes.Concat(errors)) Console.WriteLine($"Note: {n}");
    Console.WriteLine($"{scan.Games.Count} games. Recommended step: {hw.Recommended.Label()}\n");
    foreach (var g in scan.Games)
    {
        var step = prefs.StepFor(g.Id, hw.Recommended);
        var plan = Planner.Plan(g, hw, step, rules);
        var engine = g.Engine + (g.EngineDetail is null ? "" : $" ({g.EngineDetail})");
        Console.WriteLine($"{g.Name,-45} {engine,-38} {plan.StatusText}{(plan.RuleId is null ? "" : $"  [rule: {plan.RuleId}]")}");
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
    if (filters.Count == 0) return scan.Games;
    var hits = scan.Games.Where(g => filters.Any(f => g.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || g.Id.Equals(f, StringComparison.OrdinalIgnoreCase))).ToList();
    if (hits.Count == 0) throw new ArgumentException($"No installed game matches {string.Join(", ", filters)}.");
    return hits;
}

static int Optimize(List<string> argv, bool write)
{
    var (filters, forced) = ParseOptions(argv);
    var hw = HardwareDetector.Detect();
    var scan = LibraryScanner.Scan();
    var errors = new List<string>();
    var rules = RuleSet.Load(errors);
    var prefs = Prefs.Load();
    foreach (var e in errors) Console.WriteLine($"Note: {e}");
    var images = ProcessGuard.RunningImages();

    var plans = new List<GamePlan>();
    foreach (var g in Select(scan, filters))
    {
        var plan = Planner.Plan(g, hw, forced ?? prefs.StepFor(g.Id, hw.Recommended), rules);
        if (plan.HasChanges && ProcessGuard.RunningFrom(g.InstallDir, images) is not null) plan.Status = PlanStatus.Running;
        plans.Add(plan);
        if (filters.Count == 0 && plan.Status is PlanStatus.Skipped or PlanStatus.NothingToChange) continue;
        Print(plan);
    }
    if (filters.Count == 0)
    {
        var quiet = plans.Where(p => p.Status is PlanStatus.Skipped or PlanStatus.NothingToChange).ToList();
        Console.WriteLine($"{quiet.Count} other game(s) need nothing or were skipped. Use 'gameopt scan' to see why.");
    }

    var ready = plans.Where(p => p.Status == PlanStatus.Ready).ToList();
    if (!write)
    {
        Console.WriteLine($"\nPreview only. {ready.Count} game(s) ready, {ready.Sum(p => p.ChangeCount)} change(s). Run 'gameopt apply' with the same options to write them.");
        return 0;
    }
    if (ready.Count == 0) { Console.WriteLine("\nNothing to apply."); return 0; }
    var res = Applier.Apply(ready, forced is null ? "Saved slider choices" : $"Step {forced.Value.Label()}");
    Console.WriteLine();
    foreach (var a in res.Applied) Console.WriteLine($"Applied   {a}");
    foreach (var r in res.Refused) Console.WriteLine($"Refused   {r}");
    foreach (var e in res.Errors) Console.WriteLine($"Error     {e}");
    if (res.RunId is not null) Console.WriteLine($"\nBackup run {res.RunId}. Undo with: gameopt restore --run {res.RunId}");
    return res.Errors.Count > 0 ? 1 : 0;
}

static void Print(GamePlan p)
{
    Console.WriteLine($"\n== {p.Game.Name} ({p.Game.Engine}, step {p.Step.Label()}): {p.StatusText}");
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
    var hw = HardwareDetector.Detect();
    if (argv.Count == 0)
    {
        Console.WriteLine($"Global: {(prefs.GlobalStep?.Label() ?? $"recommended ({hw.Recommended.Label()})")}");
        foreach (var (id, s) in prefs.PerGame) Console.WriteLine($"  {id}: {s.Label()} (custom)");
        return 0;
    }
    if (argv is ["--global", var g])
    {
        if (g.Equals("recommended", StringComparison.OrdinalIgnoreCase)) prefs.GlobalStep = null;
        else if (QualitySteps.TryParse(g, out var s)) prefs.GlobalStep = s;
        else throw new ArgumentException($"Unknown step '{g}'.");
    }
    else if (argv is ["--game", var name, var val])
    {
        var game = Select(LibraryScanner.Scan(), new() { name });
        if (game.Count > 1) throw new ArgumentException($"'{name}' matches {game.Count} games: {string.Join(", ", game.Select(x => x.Name))}.");
        if (val.Equals("reset", StringComparison.OrdinalIgnoreCase)) prefs.PerGame.Remove(game[0].Id);
        else if (QualitySteps.TryParse(val, out var s)) prefs.PerGame[game[0].Id] = s;
        else throw new ArgumentException($"Unknown step '{val}'.");
    }
    else throw new ArgumentException("Use: gameopt step --global STEP|recommended, or gameopt step --game NAME STEP|reset");
    prefs.Save();
    Console.WriteLine("Saved.");
    return 0;
}
