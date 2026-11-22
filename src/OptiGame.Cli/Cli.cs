using OptiGame.Core;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Optimize;
using OptiGame.Core.Recommend;
using OptiGame.Core.Safety;

namespace OptiGame.Cli;

/// <summary>Command-line front end. Every command goes through OptimizerSession, like the app.</summary>
public static partial class Cli
{
    public const string Usage = """
OptiGame+ command line (optigame-cli.exe; gameopt.exe is the same program)

Look around
  optigame-cli hw                          Hardware, displays (active and supported modes), PC-wide step
  optigame-cli scan [--offline]            Every game: engine, sources, step, status
  optigame-cli recommend [--game NAME] [--details] [--offline]
                                      Each game's recommended step, its basis and caveats
  optigame-cli why --game NAME             Why a game is skipped and what you can do

Optimize
  optigame-cli preview [options]           Show every change; nothing is written
  optigame-cli apply [options]             Back up, then write the changes
    --game NAME      Only games whose name contains NAME (repeatable). Default: all games.
    --step STEP      Use this quality step instead of the slider choices (low..ultra or 0-5).
    --exclude ID     Leave this setting id out of this run only (repeatable; ids are shown in preview).
    --offline        Use cached requirement data only.

Undo
  optigame-cli history [--run ID]          Optimize and restore runs; with --run, every change in it
  optigame-cli restore [--run ID] [--game NAME] [--full] [--apply]
                                      Preview (default) or apply a restore. Without --full only the
                                      optimizer's own changes are undone and later edits are kept.

Choices (saved; the app shows the same)
  optigame-cli step --offset N             Shift every game N steps from its recommendation (-3 to 3)
  optigame-cli step --game NAME STEP|reset Set or clear a game's hand-set step
  optigame-cli game show NAME
  optigame-cli game set NAME key=value...  Per-game overrides (see settings below), plus profile=ID|inherit
                                      and steamappid=N|none (links a Steam app id for lookups)
  optigame-cli game keep NAME ID...        Always keep these setting ids for the game
  optigame-cli game unkeep NAME ID...|--all
  optigame-cli profile list | show ID | create NAME [--from ID] | rename ID NAME | delete ID
  optigame-cli profile set ID key=value... | default ID | assign NAME ID|none
  optigame-cli display list | gameopt display use \\.\DISPLAYn|main
                                      The gaming display (Windows settings are never changed)

Laptop and tools
  optigame-cli vendor [list]              Laptop maker apps found on this PC (MSI Center, Armoury Crate...)
  optigame-cli vendor open [NAME]         Open one (launch only; nothing in the app is changed)
  optigame-cli speccheck [--game NAME]    Find Game Spec Check; with --game, open it for that game

Library
  optigame-cli library list | add PATH [--name NAME] | remove PATH
                                      Manually added game folders (removing never deletes files)

Rules
  optigame-cli rule list | validate [FILE...]
  optigame-cli rule snapshot --target PATH|HKCU/... [--format F] [--out FILE]
  optigame-cli rule diff BEFORE.json AFTER.json
  optigame-cli rule wizard --game NAME --target PATH|HKCU/... [--format F]
                                      Guided: snapshot, change one setting in the game, snapshot again,
                                      pick keys and values, validate, save a local rule, preview it

Benchmarks (local only)
  optigame-cli bench import FILE --game NAME [--label before|after|TEXT] [--notes TEXT]
  optigame-cli bench list [--game NAME] | show ID | compare ID_A ID_B | prefer ID | delete ID

Preferences
  optigame-cli prefs export FILE [--no-rules]
  optigame-cli prefs import FILE [--replace] [--take-imported] [--map OLD=NEW]... [--apply]
                                      Shows what would change; --apply writes it
  optigame-cli shared-cache [add DIR | remove DIR]

Global option: --data-dir FOLDER keeps every setting, backup and cache in FOLDER (default %LOCALAPPDATA%\GameOpt).

Settings for profiles and games (key=value):
""" + SettingsParser.Help + """

Recommendations read published requirements from Steam and PCGamingWiki (only the Steam app id is
sent) and cache them for a week. Measured benchmarks are shown beside them and never change them.
""";

    public static async Task<int> Run(string[] args)
    {
        var argv = args.ToList();
        if (argv.Count == 0 || argv[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
        var cmd = argv[0].ToLowerInvariant();
        argv.RemoveAt(0);
        if (argv.Remove("--offline")) RequirementSource.AllowNetwork = false;
        if (Opt(argv, "--data-dir") is [var dd, ..]) AppPaths.Override = Path.GetFullPath(dd);
        try
        {
            return cmd switch
            {
                "hw" => Hw(),
                "scan" => await Scan(),
                "recommend" => await Recommend(argv),
                "why" => Why(argv),
                "preview" => await Optimize(argv, write: false),
                "apply" => await Optimize(argv, write: true),
                "history" => History(argv),
                "restore" => Restore(argv),
                "step" => Step(argv),
                "game" => Game(argv),
                "profile" => Profile(argv),
                "display" => Display(argv),
                "library" => Library(argv),
                "rule" => await Rule(argv),
                "bench" => Bench(argv),
                "prefs" => PrefsCmd(argv),
                "shared-cache" => SharedCache(argv),
                "vendor" => await Vendor(argv),
                "speccheck" => await SpecCheck(argv),
                _ => Fail($"Unknown command '{cmd}'. Run 'optigame-cli help'."),
            };
        }
        catch (ArgumentException e) { return Fail(e.Message); }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException or NotSupportedException)
        { return Fail(e.Message); }
    }

    internal static int Fail(string msg) { Console.Error.WriteLine(msg); return 2; }

    internal static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>Pulls "--name value" pairs out of argv; returns all values for repeatable options.</summary>
    internal static List<string> Opt(List<string> argv, string name)
    {
        var vals = new List<string>();
        for (int i = 0; i < argv.Count; i++)
            if (argv[i] == name && i + 1 < argv.Count) { vals.Add(argv[i + 1]); argv.RemoveRange(i, 2); i--; }
        return vals;
    }

    internal static bool Flag(List<string> argv, string name) => argv.Remove(name);

    internal static void NoMore(List<string> argv)
    {
        if (argv.Count > 0) throw new ArgumentException($"Unexpected argument '{argv[0]}'. Run 'optigame-cli help'.");
    }

    internal static OptimizerSession Load(bool quietNotes = false)
    {
        var s = OptimizerSession.Load();
        if (!quietNotes) foreach (var n in s.Notes) Console.WriteLine($"Note: {n}");
        return s;
    }

    internal static async Task Recs(OptimizerSession s, IEnumerable<GameInfo>? games = null)
    {
        var progress = new Progress<(int done, int total)>(p =>
        {
            if (Console.IsErrorRedirected) return;
            Console.Error.Write($"\rReading requirements {p.done}/{p.total}   ");
            if (p.done == p.total) Console.Error.WriteLine();
        });
        await s.LoadRecommendationsAsync(games, progress);
    }

    internal static GameInfo One(OptimizerSession s, string name)
    {
        var exact = s.Find(name);
        if (exact is not null) return exact;
        var m = s.Matching(name);
        if (m.Count == 0) throw new ArgumentException($"No game matches '{name}'.");
        throw new ArgumentException($"'{name}' matches {m.Count} games: {string.Join(", ", m.Take(8).Select(g => g.Name))}. Be more specific.");
    }

    static int Hw()
    {
        var hw = HardwareDetector.Detect();
        Console.WriteLine($"{hw.Cpu} ({hw.CpuCores}C/{hw.CpuThreads}T), {hw.RamGb:0.#} GB RAM, {hw.Gpu} ({hw.GpuVramGb:0.#} GB)");
        if (hw.Model.Length > 0) Console.WriteLine($"Model: {hw.Model}{(hw.IsLaptop ? " (laptop)" : "")}");
        if (hw.OtherGpus.Count > 0) Console.WriteLine($"Also found: {string.Join(", ", hw.OtherGpus)}");
        var gpu = GpuMatcher.Identify(hw.Gpu);
        Console.WriteLine($"Graphics card score: {(gpu is null ? "not in the table" : $"{gpu.Score} ({gpu.Name})")}");
        Console.WriteLine("Displays:");
        PrintDisplays(hw, Prefs.Load());
        Console.WriteLine($"PC-wide step (used when a game has no requirement data): {hw.Recommended.Label()}");
        foreach (var r in hw.Reasons) Console.WriteLine($"  - {r}");
        return 0;
    }

    internal static void PrintDisplays(HardwareInfo hw, Prefs prefs)
    {
        if (hw.Displays.Count == 0) Console.WriteLine("  none could be read; using 1920x1080 at 60 Hz");
        foreach (var d in hw.Displays)
        {
            bool gaming = prefs.GamingDisplay is null ? d.IsPrimary : d.DeviceName.Equals(prefs.GamingDisplay, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  {d.DeviceName} {d.FriendlyName}{(d.IsPrimary ? " (main)" : "")}{(gaming ? " [gaming display]" : "")}");
            Console.WriteLine($"     active: {d.ActiveModeText}; supports up to {DisplayMath.Hz(d.MaxHz)} Hz" +
                              $" ({DisplayMath.Hz(d.MaxHzAtCurrentResolution)} Hz at this resolution); native {d.Native.w}x{d.Native.h}");
            Console.WriteLine($"     automatic frame cap: {d.AutoFpsCap} fps (active {DisplayMath.Hz(d.CurrentHz)} Hz minus 3)");
        }
        if (prefs.GamingDisplay is { } g && hw.FindDisplay(g) is null) Console.WriteLine($"  Gaming display {g} is not connected; the main display is used.");
    }

    static async Task<int> Scan()
    {
        var s = Load();
        await Recs(s);
        var images = ProcessGuard.RunningImages();
        Console.WriteLine($"{s.Scan.Games.Count} games. All games slider: {Prefs.OffsetLabel(s.Prefs.GlobalOffset)}. Default profile: {s.Prefs.DefaultProfile.Name}\n");
        foreach (var g in s.Scan.Games)
        {
            var plan = s.PlanFor(g, runningImages: images);
            var engine = g.Engine + (g.EngineDetail is null ? "" : $" ({g.EngineDetail})");
            var gp = s.Prefs.GameOrNull(g.Id);
            var step = plan.Status == PlanStatus.Skipped ? "" : plan.Step.Label() + (gp?.Step is not null ? "*" : "");
            var basis = plan.Status != PlanStatus.Skipped && s.Recs.TryGetValue(g.Id, out var r) ? $"[{r.Chip}]" : "";
            Console.WriteLine($"{Trim(g.Name, 36),-36} {Trim(g.SourcesText, 12),-12} {Trim(engine, 28),-28} {step,-10} {basis,-13} {plan.StatusText}");
        }
        Console.WriteLine("\n* = step set by hand. [basis] = what the recommendation rests on ('gameopt recommend --details').");
        return 0;
    }

    static async Task<int> Recommend(List<string> argv)
    {
        var filters = Opt(argv, "--game");
        bool details = Flag(argv, "--details");
        NoMore(argv);
        var s = Load();
        var games = filters.Count == 0 ? s.Scan.Games : filters.SelectMany(f => s.Matching(f)).Distinct().ToList();
        if (games.Count == 0) throw new ArgumentException($"No game matches {string.Join(", ", filters)}.");
        await Recs(s, games);
        foreach (var g in games)
        {
            if (!s.Recs.TryGetValue(g.Id, out var r)) continue;
            Console.WriteLine($"{Trim(g.Name, 36),-36} {r.Step.Label(),-10} [{r.Chip}] {r.Reason}");
            if (details || filters.Count > 0)
            {
                Console.WriteLine($"   Steam app id: {s.AppIdFor(g) ?? "none"} ({s.AppIdSource(g)})");
                foreach (var line in r.Details.Split('\n').Skip(1)) Console.WriteLine("   " + line);
                if (filters.Count > 0) foreach (var l in GameLinks.For(g)) Console.WriteLine($"   {l.Label}: {l.Url}");
            }
        }
        return 0;
    }

    static int Why(List<string> argv)
    {
        var name = Opt(argv, "--game").FirstOrDefault() ?? throw new ArgumentException("Use: gameopt why --game NAME");
        NoMore(argv);
        var s = Load();
        var g = One(s, name);
        var plan = s.PlanFor(g, runningImages: ProcessGuard.RunningImages());
        Console.WriteLine($"{g.Name}: {plan.StatusText}");
        Console.WriteLine($"  Engine: {g.Engine}{(g.EngineDetail is null ? "" : $" ({g.EngineDetail})")}; found by {g.SourcesText}; folder {g.InstallDir}");
        if (plan.RuleId is not null) Console.WriteLine($"  Rule: {plan.RuleId}");
        foreach (var x in plan.Skipped) Console.WriteLine($"  - {x}");
        foreach (var x in plan.NotControlled) Console.WriteLine($"  - Not controlled: {x}");
        foreach (var a in plan.Actions)
            Console.WriteLine("  You can: " + a switch
            {
                PlanAction.OpenSettingsFolder => $"open the settings folder {plan.SettingsFolder}",
                PlanAction.AddRule => $"add a rule for it: gameopt rule wizard --game \"{g.Name}\" --target <settings file>",
                PlanAction.LaunchOnceAndRescan => g.SteamAppId is not null ? $"start it once (steam://rungameid/{g.SteamAppId}), close it, then rescan" : "start it once, close it, then rescan",
                _ => "close the game, then try again",
            });
        return 0;
    }

    static async Task<int> Optimize(List<string> argv, bool write)
    {
        var filters = Opt(argv, "--game");
        var excl = Opt(argv, "--exclude");
        QualityStep? forced = null;
        foreach (var st in Opt(argv, "--step"))
            forced = QualitySteps.TryParse(st, out var q) ? q : throw new ArgumentException($"Unknown step '{st}'.");
        NoMore(argv);
        var s = Load();
        var games = filters.Count == 0 ? s.Scan.Games : filters.SelectMany(f => s.Matching(f)).Distinct().ToList();
        if (games.Count == 0) throw new ArgumentException($"No installed game matches {string.Join(", ", filters)}.");
        await Recs(s, games);
        var images = ProcessGuard.RunningImages();
        var plans = games.Select(g => s.PlanFor(g, excl, forced, images)).ToList();
        foreach (var p in plans)
        {
            if (filters.Count == 0 && p.Status is PlanStatus.Skipped or PlanStatus.NothingToChange) continue;
            Print(p, s.Recs.GetValueOrDefault(p.Game.Id));
        }
        if (filters.Count == 0)
        {
            int quiet = plans.Count(p => p.Status is PlanStatus.Skipped or PlanStatus.NothingToChange);
            Console.WriteLine($"\n{quiet} other game(s) need nothing or were skipped. 'gameopt scan' lists them; 'gameopt why --game NAME' explains one.");
        }
        var ready = plans.Where(p => p.Status == PlanStatus.Ready).ToList();
        var scope = filters.Count == 0 ? $"all {games.Count} games" : $"{games.Count} matching game(s)";
        if (!write)
        {
            Console.WriteLine($"\nPreview of {scope}: {ready.Count} ready, {ready.Sum(p => p.ChangeCount)} change(s). Nothing was written. 'gameopt apply' with the same options writes them.");
            return 0;
        }
        if (ready.Count == 0) { Console.WriteLine("\nNothing to apply."); return 0; }
        var res = Applier.Apply(ready, forced is null ? $"Slider and profile choices ({scope})" : $"Step {forced.Value.Label()} ({scope})");
        Console.WriteLine();
        foreach (var a in res.Applied) Console.WriteLine($"Applied   {a}");
        foreach (var r in res.Refused) Console.WriteLine($"Refused   {r}");
        foreach (var e in res.Errors) Console.WriteLine($"Error     {e}");
        if (res.RunId is not null) Console.WriteLine($"\nBackup run {res.RunId}. Undo with: gameopt restore --run {res.RunId} --apply");
        return res.Errors.Count > 0 ? 1 : 0;
    }

    internal static void Print(GamePlan p, GameRecommendation? rec)
    {
        var req = p.Request;
        Console.WriteLine($"\n== {p.Game.Name} ({p.Game.Engine}): {p.StatusText}");
        if (req is not null)
            Console.WriteLine($"   step {p.Step.Label()} ({req.StepSource}), profile {req.ProfileName}, display {req.Display.Label}, " +
                              $"frame cap {req.FpsText}, resolution {(req.Resolution is var (w, h) ? $"{w}x{h}" : "kept")}, " +
                              $"display mode {req.DisplayMode.ToString().ToLowerInvariant()}, VSync {req.VSync.ToString().ToLowerInvariant()}");
        if (rec is not null)
        {
            Console.WriteLine($"   recommended {rec.Step.Label()} [{rec.Chip}]: {rec.Reason}");
            if (rec.Measured is not null) Console.WriteLine($"   measured (preferred benchmark): {rec.Measured}");
        }
        foreach (var f in p.Files)
        {
            Console.WriteLine($"   {f.Path}");
            foreach (var c in f.Changes) Console.WriteLine($"      {c}   [{c.Id}]");
        }
        if (p.Registry.Count > 0)
        {
            Console.WriteLine($"   HKCU\\{p.Registry[0].SubKey}");
            foreach (var r in p.Registry) Console.WriteLine($"      {r.Display}   [{r.Id}]");
        }
        foreach (var x in p.Excluded) Console.WriteLine($"   kept: {x.Setting} {x.Old ?? "(not set)"} (would be {x.New}; {x.Why})   [{x.Id}]");
        foreach (var x in p.NotControlled) Console.WriteLine($"   not controlled: {x}");
        foreach (var x in p.Skipped) Console.WriteLine($"   skipped: {x}");
        foreach (var w in p.Warnings) Console.WriteLine($"   warning: {w}");
    }
}
