using OptiGame.Core;
using System.Globalization;
using OptiGame.Core.Bench;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Optimize;
using OptiGame.Core.Recommend;
using OptiGame.Core.Rules;
using OptiGame.Core.Safety;

namespace OptiGame.Cli;

public static partial class Cli
{
    // ------------------------------------------------------------------ history and restore

    static int History(List<string> argv)
    {
        var runId = Opt(argv, "--run").FirstOrDefault();
        NoMore(argv);
        var runs = Applier.Runs();
        if (runId is null)
        {
            if (runs.Count == 0) Console.WriteLine("No runs yet.");
            foreach (var r in runs)
                Console.WriteLine($"{r.Id}  {r.Created:g}  {(r.Kind == "restore" ? "[before a restore] " : "")}{r.Summary}  ({string.Join(", ", r.GameNames)})");
            return 0;
        }
        var run = runs.FirstOrDefault(r => r.Id == runId) ?? throw new ArgumentException($"No run '{runId}'.");
        Console.WriteLine($"{run.Id}  {run.Created:g}  {run.Summary}");
        foreach (var e in run.Entries)
        {
            Console.WriteLine($"  {e.GameName}: {(e.Kind == "file" ? e.Path : $@"HKCU\{e.SubKey}\{e.ValueName}")}");
            foreach (var c in e.Changes) Console.WriteLine($"     {c.Setting}: {c.OldRaw ?? "(not set)"} -> {c.NewRaw}");
            if (e.Kind == "registry") Console.WriteLine($"     {e.Setting}: {(e.ValueExisted ? e.ValueData?.ToString() : "(not set)")} -> {e.WrittenData?.ToString() ?? "?"}");
            if (e.Kind == "file" && e.Changes.Count == 0) Console.WriteLine("     (older run: individual keys were not recorded)");
        }
        return 0;
    }

    static int Restore(List<string> argv)
    {
        if (Flag(argv, "--list")) return History(argv);
        var runId = Opt(argv, "--run").FirstOrDefault();
        var game = Opt(argv, "--game").FirstOrDefault();
        bool full = Flag(argv, "--full"), apply = Flag(argv, "--apply");
        NoMore(argv);
        var runs = Applier.Runs();
        var run = runId is null ? runs.FirstOrDefault(r => r.Kind == "optimize") ?? runs.FirstOrDefault() : runs.FirstOrDefault(r => r.Id == runId);
        if (run is null) return Fail(runId is null ? "No backups to restore." : $"No run '{runId}'. See 'gameopt history'.");
        var plan = Applier.PlanRestore(run, game);
        if (plan.Items.Count == 0) { Console.WriteLine("Nothing in that run matched."); return 0; }
        var mode = full ? RestoreMode.FullBackup : RestoreMode.UndoChanges;

        Console.WriteLine($"Restore of run {run.Id} ({run.Created:g}, {run.Summary}){(game is null ? "" : $" for {game}")}");
        Console.WriteLine(full ? "Mode: full backup. Each file is replaced by its backup; anything changed since is lost (it is saved first)."
                               : "Mode: undo the optimizer's changes only. Settings changed again since are left alone.");
        foreach (var item in plan.Items)
        {
            Console.WriteLine($"\n  {item.Entry.GameName}: {item.Title}{(item.ChangedSinceOptimize ? "  (changed since the optimize)" : "")}");
            if (full)
            {
                if (item.BackupBytes is null && item.Entry.Kind == "file") Console.WriteLine($"     cannot restore: {item.WhyNoUndo}");
                else Console.WriteLine(item.Entry.Kind == "file" ? "     whole file put back from the backup" : $"     {item.Keys[0].Setting} put back to {item.Keys[0].Original ?? "(removed)"}");
                continue;
            }
            if (!item.CanUndoChanges) { Console.WriteLine($"     kept: {item.WhyNoUndo}. A full restore (--full) can replace it."); continue; }
            if (item.Keys.Count == 0) Console.WriteLine("     unchanged since the optimize: the backup is put back exactly");
            foreach (var k in item.Keys) Console.WriteLine($"     {k.Text}");
        }
        if (!full && plan.AnyConflicts)
            Console.WriteLine("\nConflicts above are left as they are. Use --full to put the whole backup back instead (preview it first).");
        if (!apply)
        {
            Console.WriteLine("\nPreview only. Add --apply to restore. The current state is backed up first, so the restore can be undone.");
            return 0;
        }
        var res = Applier.Restore(plan, mode);
        Console.WriteLine();
        foreach (var a in res.Applied) Console.WriteLine($"Restored  {a}");
        foreach (var r in res.Refused) Console.WriteLine($"Not done  {r}");
        foreach (var e in res.Errors) Console.WriteLine($"Error     {e}");
        if (res.RunId is not null) Console.WriteLine($"\nThe state before this restore is run {res.RunId} ('gameopt restore --run {res.RunId} --apply' undoes it).");
        if (res.Applied.Count + res.Refused.Count + res.Errors.Count == 0) Console.WriteLine("Nothing needed restoring.");
        return res.Refused.Count + res.Errors.Count > 0 ? 1 : 0;
    }

    // ------------------------------------------------------------------ choices

    static int Step(List<string> argv)
    {
        var prefs = Prefs.Load();
        if (argv.Count == 0)
        {
            Console.WriteLine($"All games: {Prefs.OffsetLabel(prefs.GlobalOffset)}; default profile {prefs.DefaultProfile.Name}");
            foreach (var (id, g) in prefs.Games.Where(kv => kv.Value.Step is not null)) Console.WriteLine($"  {id}: {g.Step!.Value.Label()} (set by hand)");
            return 0;
        }
        if (argv is ["--offset", var o])
        {
            if (!int.TryParse(o, out var n) || Math.Abs(n) > Prefs.MaxOffset) throw new ArgumentException($"Offset must be -{Prefs.MaxOffset} to {Prefs.MaxOffset}.");
            prefs.GlobalOffset = n;
        }
        else if (argv is ["--game", var name, var val])
        {
            var s = Load(quietNotes: true);
            var g = One(s, name);
            if (val.Equals("reset", StringComparison.OrdinalIgnoreCase)) prefs.Game(g.Id).Step = null;
            else prefs.Game(g.Id).Step = QualitySteps.TryParse(val, out var st) ? st : throw new ArgumentException($"Unknown step '{val}'.");
        }
        else throw new ArgumentException("Use: gameopt step --offset N, or gameopt step --game NAME STEP|reset");
        prefs.Save();
        Console.WriteLine("Saved.");
        return 0;
    }

    static int Game(List<string> argv)
    {
        if (argv.Count < 2) throw new ArgumentException("Use: gameopt game show|set|keep|unkeep NAME ...");
        var sub = argv[0].ToLowerInvariant();
        var s = Load(quietNotes: true);
        var g = One(s, argv[1]);
        var rest = argv.Skip(2).ToList();
        var gp = s.Prefs.Game(g.Id);
        switch (sub)
        {
            case "show":
                var req = s.RequestFor(g);
                Console.WriteLine($"{g.Name} ({g.Id}), found by {g.SourcesText}");
                Console.WriteLine($"  Profile: {s.Prefs.ProfileFor(g.Id).Name}{(gp.ProfileId is null ? " (default)" : "")}");
                Console.WriteLine($"  Step: {req.Step.Label()} ({req.StepSource})");
                Console.WriteLine($"  Display: {req.Display.Label}; frame cap {req.FpsText}; resolution {(req.Resolution is var (w, h) ? $"{w}x{h}" : "kept")}; " +
                                  $"display mode {req.DisplayMode}; VSync {req.VSync}");
                foreach (var d in SettingsParser.Describe(gp, req.Display)) Console.WriteLine($"  Own setting: {d}");
                if (gp.Excluded.Count > 0) Console.WriteLine($"  Always kept: {string.Join(", ", gp.Excluded)}");
                Console.WriteLine($"  Steam app id for lookups: {s.AppIdFor(g) ?? "none"} ({s.AppIdSource(g)})");
                return 0;
            case "set":
                if (rest.Count == 0) throw new ArgumentException("Give at least one key=value. Settings:\n" + SettingsParser.Help);
                foreach (var a in rest)
                {
                    if (a.StartsWith("profile=", StringComparison.OrdinalIgnoreCase))
                    {
                        var id = a[8..];
                        gp.ProfileId = id is "inherit" or "none" or "" ? null : s.Prefs.FindProfile(id)?.Id ?? throw new ArgumentException($"No profile '{id}'.");
                    }
                    else if (a.StartsWith("steamappid=", StringComparison.OrdinalIgnoreCase))
                    {
                        var id = a[11..];
                        gp.SteamAppId = id is "none" or "" ? null : id.All(char.IsAsciiDigit) ? id : throw new ArgumentException("steamappid must be a number or none.");
                    }
                    else SettingsParser.Apply(gp, a);
                }
                CheckDisplay(s, gp.Display);
                break;
            case "keep":
                if (rest.Count == 0) throw new ArgumentException("Give setting ids (shown in brackets by 'gameopt preview --game NAME').");
                foreach (var id in rest) if (!gp.Excluded.Contains(id)) gp.Excluded.Add(id);
                break;
            case "unkeep":
                if (rest is ["--all"]) gp.Excluded.Clear();
                else foreach (var id in rest) gp.Excluded.Remove(id);
                break;
            default: throw new ArgumentException("Use: gameopt game show|set|keep|unkeep NAME ...");
        }
        s.Prefs.Save();
        Console.WriteLine("Saved.");
        return 0;
    }

    static void CheckDisplay(OptimizerSession s, string? device)
    {
        if (device is not null && s.Hw.FindDisplay(device) is null)
            Console.WriteLine($"Note: {device} is not connected now; the main display is used until it is.");
    }

    static int Profile(List<string> argv)
    {
        var prefs = Prefs.Load();
        var sub = argv.Count > 0 ? argv[0].ToLowerInvariant() : "list";
        var rest = argv.Skip(1).ToList();
        var display = HardwareDetector.Detect().Primary;
        switch (sub)
        {
            case "list":
                foreach (var p in prefs.AllProfiles)
                    Console.WriteLine($"{p.Id,-16} {p.Name,-16} {(p.BuiltIn ? "built-in" : "yours"),-9} {(p.Id == prefs.DefaultProfileId ? "default" : "")}");
                return 0;
            case "show":
                var pr = prefs.FindProfile(rest.FirstOrDefault()) ?? throw new ArgumentException("Use: gameopt profile show ID");
                Console.WriteLine($"{pr.Name} ({pr.Id}){(pr.BuiltIn ? ", built-in" : "")}");
                if (pr.Description.Length > 0) Console.WriteLine("  " + pr.Description);
                foreach (var d in SettingsParser.Describe(pr, display)) Console.WriteLine("  " + d);
                Console.WriteLine($"  Used by {prefs.Games.Count(g => g.Value.ProfileId == pr.Id)} game(s){(pr.Id == prefs.DefaultProfileId ? " and every game without its own profile" : "")}.");
                return 0;
            case "create":
                var from = Opt(rest, "--from").FirstOrDefault();
                var name = string.Join(' ', rest);
                var basis = from is null ? null : prefs.FindProfile(from) ?? throw new ArgumentException($"No profile '{from}'.");
                var created = prefs.CreateProfile(name, basis);
                Console.WriteLine($"Created profile {created.Name} ({created.Id}).");
                break;
            case "rename":
                if (rest.Count < 2) throw new ArgumentException("Use: gameopt profile rename ID NEW NAME");
                prefs.RenameProfile(rest[0], string.Join(' ', rest.Skip(1)));
                break;
            case "delete":
                int n = prefs.DeleteProfile(rest.FirstOrDefault() ?? throw new ArgumentException("Use: gameopt profile delete ID"));
                Console.WriteLine($"Deleted.{(n > 0 ? $" {n} game(s) now use the default profile." : "")}");
                break;
            case "set":
                var id = rest.FirstOrDefault() ?? throw new ArgumentException("Use: gameopt profile set ID key=value...");
                var target = prefs.Profiles.FirstOrDefault(p => p.Id == id)
                             ?? throw new ArgumentException(prefs.FindProfile(id) is null ? $"No profile '{id}'." : "Built-in profiles cannot be changed; 'gameopt profile create NAME --from " + id + "' makes an editable copy.");
                foreach (var a in rest.Skip(1)) SettingsParser.Apply(target, a);
                break;
            case "default":
                var dp = prefs.FindProfile(rest.FirstOrDefault()) ?? throw new ArgumentException("Use: gameopt profile default ID");
                prefs.DefaultProfileId = dp.Id;
                break;
            case "assign":
                if (rest.Count < 2) throw new ArgumentException("Use: gameopt profile assign GAME ID|none");
                var s = Load(quietNotes: true);
                var g = One(s, rest[0]);
                var pid = rest[1];
                s.Prefs.Game(g.Id).ProfileId = pid == "none" ? null : s.Prefs.FindProfile(pid)?.Id ?? throw new ArgumentException($"No profile '{pid}'.");
                s.Prefs.Save();
                Console.WriteLine("Saved.");
                return 0;
            default: throw new ArgumentException("Use: gameopt profile list|show|create|rename|delete|set|default|assign");
        }
        prefs.Save();
        Console.WriteLine("Saved.");
        return 0;
    }

    static int Display(List<string> argv)
    {
        var prefs = Prefs.Load();
        var hw = HardwareDetector.Detect();
        if (argv.Count == 0 || argv[0] == "list") { PrintDisplays(hw, prefs); return 0; }
        if (argv is ["use", var dev])
        {
            if (dev is "main" or "primary") prefs.GamingDisplay = null;
            else prefs.GamingDisplay = hw.FindDisplay(dev)?.DeviceName ?? throw new ArgumentException($"{dev} is not connected. 'gameopt display list' shows the names.");
            prefs.Save();
            Console.WriteLine("Saved. Windows display settings were not changed.");
            return 0;
        }
        throw new ArgumentException("Use: gameopt display list | gameopt display use \\\\.\\DISPLAYn|main");
    }

    static int Library(List<string> argv)
    {
        var prefs = Prefs.Load();
        var sub = argv.Count > 0 ? argv[0].ToLowerInvariant() : "list";
        var rest = argv.Skip(1).ToList();
        switch (sub)
        {
            case "list":
                if (prefs.ManualGames.Count == 0) Console.WriteLine("No folders added. 'gameopt library add PATH' adds one.");
                foreach (var m in prefs.ManualGames) Console.WriteLine($"{m.Path}{(m.Name is null ? "" : $"  ({m.Name})")}{(Directory.Exists(m.Path) ? "" : "  [not reachable]")}");
                return 0;
            case "add":
                var name = Opt(rest, "--name").FirstOrDefault();
                var path = Path.GetFullPath(rest.FirstOrDefault() ?? throw new ArgumentException("Use: gameopt library add PATH [--name NAME]"));
                if (!Directory.Exists(path)) throw new ArgumentException($"{path} does not exist.");
                if (prefs.ManualGames.Any(m => LibraryScanner.NormalizeDir(m.Path) == LibraryScanner.NormalizeDir(path))) throw new ArgumentException("That folder is already added.");
                prefs.ManualGames.Add(new ManualGame { Path = path, Name = name });
                prefs.Save();
                var g = new GameInfo { Id = LibraryScanner.ManualId(path), Name = name ?? Path.GetFileName(path), InstallDir = path, Store = "Manual" };
                EngineDetector.Detect(g);
                Console.WriteLine($"Added {g.Name} ({g.Engine}). If a launcher also lists this folder, the two entries are merged.");
                return 0;
            case "remove":
                var p = rest.FirstOrDefault() ?? throw new ArgumentException("Use: gameopt library remove PATH");
                int n = prefs.ManualGames.RemoveAll(m => LibraryScanner.NormalizeDir(m.Path) == LibraryScanner.NormalizeDir(p));
                if (n == 0) throw new ArgumentException($"{p} is not in the list.");
                prefs.Save();
                Console.WriteLine("Removed from the list. No files were deleted.");
                return 0;
            default: throw new ArgumentException("Use: gameopt library list|add|remove");
        }
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

    // ------------------------------------------------------------------ rules

    static async Task<int> Rule(List<string> argv)
    {
        var sub = argv.Count > 0 ? argv[0].ToLowerInvariant() : "list";
        var rest = argv.Skip(1).ToList();
        switch (sub)
        {
            case "list":
                var errors = new List<string>();
                foreach (var r in RuleSet.Load(errors))
                    Console.WriteLine($"{r.Id,-24} {Trim(r.Name, 30),-30} {(r.IsUserRule ? "yours" : "built-in"),-9} {(r.Verified is null ? "" : "verified " + r.Verified)}");
                foreach (var e in errors) Console.WriteLine("Problem: " + e);
                return errors.Count > 0 ? 1 : 0;
            case "validate":
                var files = rest.Count > 0 ? rest : Directory.Exists(RuleSet.UserRulesDir) ? Directory.EnumerateFiles(RuleSet.UserRulesDir, "*.json").ToList() : new();
                if (files.Count == 0) { Console.WriteLine("No rule files given and none in " + RuleSet.UserRulesDir); return 0; }
                int bad = 0;
                foreach (var f in files)
                {
                    var rule = RuleSet.TryParse(File.ReadAllText(f), out var problems);
                    var errs = problems.Count(p => p.Severity == RuleProblemSeverity.Error);
                    bad += errs > 0 || rule is null ? 1 : 0;
                    Console.WriteLine($"{f}: {(errs > 0 || rule is null ? "INVALID" : problems.Count > 0 ? "valid, with warnings" : "valid")}");
                    foreach (var p in problems) Console.WriteLine("   " + p);
                }
                return bad > 0 ? 1 : 0;
            case "snapshot":
                var target = Opt(rest, "--target").FirstOrDefault() ?? throw new ArgumentException("Use: gameopt rule snapshot --target PATH [--format F] [--out FILE]");
                var fmt = Opt(rest, "--format").FirstOrDefault() ?? Snapshot.GuessFormat(target) ?? throw new ArgumentException("Cannot tell the format; add --format " + string.Join("|", Snapshot.Formats));
                var outFile = Opt(rest, "--out").FirstOrDefault();
                NoMore(rest);
                var snap = Snapshot.Capture(fmt, target);
                if (outFile is not null) { File.WriteAllText(outFile, snap.ToJson()); Console.WriteLine($"Saved {snap.Values.Count} values to {outFile}."); }
                else foreach (var (k, v) in snap.Values.OrderBy(x => x.Key)) Console.WriteLine($"{k} = {v.Display} ({v.Type})");
                foreach (var u in snap.Unreadable) Console.WriteLine("not usable: " + u);
                return 0;
            case "diff":
                if (rest.Count != 2) throw new ArgumentException("Use: gameopt rule diff BEFORE.json AFTER.json");
                var diffs = Snapshot.Diff(Snapshot.FromJson(File.ReadAllText(rest[0])), Snapshot.FromJson(File.ReadAllText(rest[1])));
                if (diffs.Count == 0) Console.WriteLine("No differences.");
                foreach (var d in diffs) Console.WriteLine($"{d.Kind,-8} {d}");
                return 0;
            case "wizard":
                return await Wizard(rest);
            default: throw new ArgumentException("Use: gameopt rule list|validate|snapshot|diff|wizard");
        }
    }

    static async Task<int> Wizard(List<string> rest)
    {
        var name = Opt(rest, "--game").FirstOrDefault() ?? throw new ArgumentException("Use: gameopt rule wizard --game NAME --target PATH [--format F]");
        var target = Opt(rest, "--target").FirstOrDefault() ?? throw new ArgumentException("Give --target: the settings file, or HKCU/Software/... for registry settings.");
        var fmt = Opt(rest, "--format").FirstOrDefault() ?? Snapshot.GuessFormat(target) ?? throw new ArgumentException("Cannot tell the format; add --format " + string.Join("|", Snapshot.Formats));
        NoMore(rest);
        var s = Load(quietNotes: true);
        var g = One(s, name);
        Console.WriteLine($"Rule wizard for {g.Name}. Reading only; nothing is written to the game.\n");
        var before = Snapshot.Capture(fmt, target);
        Console.WriteLine($"Step 1 done: {before.Values.Count} values read from {target}.");
        foreach (var u in before.Unreadable.Take(5)) Console.WriteLine("  not usable: " + u);
        Console.WriteLine("\nStep 2: start the game, change ONE setting in its own menu, save, and close the game.");
        Console.Write("Press Enter when the game is closed...");
        Console.ReadLine();
        if (ProcessGuard.RunningFrom(g.InstallDir) is { } exe) { Console.WriteLine($"{Path.GetFileName(exe)} is still running. Close it and run the wizard again."); return 1; }
        var after = Snapshot.Capture(fmt, target);
        var diffs = Snapshot.Diff(before, after);
        if (diffs.Count == 0) { Console.WriteLine("Nothing changed in that file. The setting may be stored somewhere else."); return 1; }
        Console.WriteLine($"\nStep 3: {diffs.Count} difference(s):");
        for (int i = 0; i < diffs.Count; i++) Console.WriteLine($"  {i + 1}. {diffs[i]}");
        Console.Write("\nWhich numbers belong to the setting you changed (e.g. 1,3)? ");
        var picks = (Console.ReadLine() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n >= 1 && n <= diffs.Count).Distinct().ToList();
        if (picks.Count == 0) { Console.WriteLine("Nothing chosen; no rule written."); return 1; }

        var keys = new List<DraftKey>();
        foreach (var n in picks)
        {
            var d = diffs[n - 1];
            if (d.After is null) { Console.WriteLine($"{d.Key} was removed; it cannot be set by a rule."); continue; }
            Console.WriteLine($"\n{d.Key} ({d.After.Type}): was {d.Before?.Display ?? "(none)"}, now {d.After.Display}");
            Console.Write("  Label (Enter to skip): ");
            var label = Console.ReadLine();
            Console.Write($"  Category ({string.Join(", ", Categories.Keepable)}, Enter for none): ");
            var cat = Console.ReadLine();
            Console.WriteLine("  Values: one value used at every step, or six separated by '|' for Low|Medium|Balanced|High|Very high|Ultra.");
            Console.WriteLine("  Two snapshots only show two values; the tool will not guess the other steps.");
            Console.Write($"  Values (Enter = '{d.After.Display}' at every step): ");
            var vals = Console.ReadLine();
            var list = string.IsNullOrWhiteSpace(vals) ? new List<string> { d.After.Display } : vals.Split('|').Select(v => v.Trim()).ToList();
            keys.Add(new DraftKey { Key = d.Key, Type = d.After.Type, Label = string.IsNullOrWhiteSpace(label) ? null : label, Category = string.IsNullOrWhiteSpace(cat) ? null : cat.Trim(), Values = list });
        }
        Console.Write("\nYour name for the rule's author field (Enter to skip): ");
        var author = Console.ReadLine();
        Console.Write("Game version, if you know it (Enter to skip): ");
        var ver = Console.ReadLine();
        string json; GameRule rule; List<RuleProblem> problems;
        try { (json, rule, problems) = RuleBuilder.Build(g, fmt, target, keys, string.IsNullOrWhiteSpace(author) ? null : author, string.IsNullOrWhiteSpace(ver) ? null : ver); }
        catch (FormatException e) { Console.WriteLine("The rule is not valid:\n" + e.Message); return 1; }
        Console.WriteLine("\nStep 4: the rule\n" + json);
        foreach (var p in problems) Console.WriteLine(p);
        if (problems.Any(p => p.Severity == RuleProblemSeverity.Error)) { Console.WriteLine("Not saved because of the errors above."); return 1; }
        Console.Write("\nSave it to your rules folder? [y/N] ");
        if (!(Console.ReadLine() ?? "").Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Not saved."); return 0; }
        var path = RuleBuilder.Save(json, rule.Id, overwrite: false);
        Console.WriteLine($"Saved {path}.\n\nStep 5: preview with the new rule (nothing is written):");
        var s2 = Load(quietNotes: true);
        var g2 = One(s2, g.Id);
        await Recs(s2, new[] { g2 });
        Print(s2.PlanFor(g2, runningImages: ProcessGuard.RunningImages()), s2.Recs.GetValueOrDefault(g2.Id));
        return 0;
    }

    // ------------------------------------------------------------------ benchmarks

    static int Bench(List<string> argv)
    {
        var sub = argv.Count > 0 ? argv[0].ToLowerInvariant() : "list";
        var rest = argv.Skip(1).ToList();
        var inv = CultureInfo.InvariantCulture;
        switch (sub)
        {
            case "import":
                var gameName = Opt(rest, "--game").FirstOrDefault() ?? throw new ArgumentException("Use: gameopt bench import FILE --game NAME [--label before|after] [--notes TEXT]");
                var label = Opt(rest, "--label").FirstOrDefault() ?? "";
                var notes = Opt(rest, "--notes").FirstOrDefault();
                var file = rest.FirstOrDefault() ?? throw new ArgumentException("Give the CSV file.");
                var s = Load(quietNotes: true);
                var g = One(s, gameName);
                var req = s.RequestFor(g);
                var r = BenchmarkStore.Import(file, g.Id, g.Name, label, s.HardwareSummary, req.ProfileName, req.Step.Label(),
                    req.Resolution is var (w, h) ? $"{w}x{h}" : "kept", req.FpsText, notes);
                Console.WriteLine($"Imported {r.Id} ({r.SourceFormat}): {r.Summary}.");
                Console.WriteLine("Context recorded: the game's current profile, step, resolution and cap in GameOptimizer. Check they match what you ran.");
                return 0;
            case "list":
                var filter = Opt(rest, "--game").FirstOrDefault();
                var all = BenchmarkStore.All().Where(b => filter is null || b.GameName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (all.Count == 0) Console.WriteLine("No benchmarks. 'gameopt bench import FILE --game NAME' adds one.");
                foreach (var b in all) Console.WriteLine($"{b.Id}  {b.GameName,-28} {b.Label,-8} {b.Summary}{(b.Preferred ? "  [preferred]" : "")}");
                return 0;
            case "show":
                var one = BenchmarkStore.Find(rest.FirstOrDefault() ?? "") ?? throw new ArgumentException("Use: gameopt bench show ID");
                Console.WriteLine($"{one.GameName}, {one.Label}, imported {one.Imported:g} from {one.SourceFile} ({one.SourceFormat})");
                Console.WriteLine(string.Create(inv, $"  {one.Metrics.Frames} frames over {one.Metrics.DurationSeconds:0.#} s"));
                Console.WriteLine(string.Create(inv, $"  Average FPS {one.Metrics.AverageFps:0.#} (frames / time)"));
                Console.WriteLine(string.Create(inv, $"  1% low FPS {one.Metrics.OnePercentLowFps:0.#} (1000 / 99th percentile frame time of {one.Metrics.P99FrameTimeMs:0.##} ms)"));
                Console.WriteLine(string.Create(inv, $"  Worst frame {one.Metrics.MaxFrameTimeMs:0.##} ms"));
                Console.WriteLine($"  Settings: {one.ProfileName} / {one.Step}, {one.Resolution}, cap {one.FpsCap}; hardware {one.Hardware}");
                if (one.Notes is not null) Console.WriteLine($"  Notes: {one.Notes}");
                return 0;
            case "compare":
                if (rest.Count != 2) throw new ArgumentException("Use: gameopt bench compare ID_A ID_B");
                var a = BenchmarkStore.Find(rest[0]) ?? throw new ArgumentException($"No benchmark '{rest[0]}'.");
                var b2 = BenchmarkStore.Find(rest[1]) ?? throw new ArgumentException($"No benchmark '{rest[1]}'.");
                var c = BenchmarkStore.Compare(a, b2);
                Console.WriteLine($"{a.Label} ({a.Id}) -> {b2.Label} ({b2.Id}), {a.GameName}");
                foreach (var d in c.Differences) Console.WriteLine("  " + d);
                foreach (var w2 in c.Warnings) Console.WriteLine("  Note: " + w2);
                Console.WriteLine("These are measurements of two runs; they do not predict other scenes or settings.");
                return 0;
            case "prefer":
                BenchmarkStore.SetPreferred(rest.FirstOrDefault() ?? throw new ArgumentException("Use: gameopt bench prefer ID"));
                Console.WriteLine("Marked as preferred. It is shown beside the game's recommendation and does not change it.");
                return 0;
            case "delete":
                BenchmarkStore.Delete(rest.FirstOrDefault() ?? throw new ArgumentException("Use: gameopt bench delete ID"));
                Console.WriteLine("Deleted.");
                return 0;
            default: throw new ArgumentException("Use: gameopt bench import|list|show|compare|prefer|delete");
        }
    }

    // ------------------------------------------------------------------ preferences export/import

    static int PrefsCmd(List<string> argv)
    {
        var sub = argv.Count > 0 ? argv[0].ToLowerInvariant() : "";
        var rest = argv.Skip(1).ToList();
        switch (sub)
        {
            case "export":
                bool noRules = Flag(rest, "--no-rules");
                var file = rest.FirstOrDefault() ?? throw new ArgumentException("Use: gameopt prefs export FILE [--no-rules]");
                File.WriteAllText(file, PrefsTransfer.Export(Prefs.Load(), includeRules: !noRules));
                Console.WriteLine($"Exported to {file}. It holds profiles, choices, kept settings{(noRules ? "" : " and your rules")}; no saves, backups or benchmarks.");
                return 0;
            case "import":
                var mode = Flag(rest, "--replace") ? ImportMode.Replace : ImportMode.Merge;
                var policy = Flag(rest, "--take-imported") ? ConflictPolicy.TakeImported : ConflictPolicy.KeepMine;
                bool apply = Flag(rest, "--apply");
                var maps = Opt(rest, "--map").Select(m => m.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
                var path = rest.FirstOrDefault() ?? throw new ArgumentException("Use: gameopt prefs import FILE [--replace] [--take-imported] [--map OLD=NEW]... [--apply]");
                var hw = HardwareDetector.Detect();
                var plan = PrefsTransfer.Analyze(File.ReadAllText(path), Prefs.Load(), mode, policy, maps, displayExists: d => hw.FindDisplay(d) is not null);
                Console.WriteLine($"Import {(mode == ImportMode.Replace ? "replacing" : "merging into")} your preferences:");
                foreach (var x in plan.Errors) Console.WriteLine("  error: " + x);
                foreach (var x in plan.Additions) Console.WriteLine("  add: " + x);
                foreach (var x in plan.Replacements) Console.WriteLine("  replace: " + x);
                foreach (var x in plan.Conflicts) Console.WriteLine("  conflict: " + x);
                foreach (var x in plan.Skipped) Console.WriteLine("  skip: " + x);
                foreach (var x in plan.NeedsRemap) Console.WriteLine($"  needs a new location: {x} (add --map \"{x}=NEW PATH\")");
                if (plan.Additions.Count + plan.Replacements.Count + plan.Conflicts.Count == 0 && plan.Errors.Count == 0) Console.WriteLine("  nothing new");
                if (!plan.CanApply) { Console.WriteLine("Not applied: fix the items above first."); return 1; }
                if (!apply) { Console.WriteLine("Preview only. Add --apply to save it (your current file is kept as prefs.json.before-import)."); return 0; }
                PrefsTransfer.Apply(plan);
                Console.WriteLine("Imported.");
                return 0;
            default: throw new ArgumentException("Use: gameopt prefs export FILE | gameopt prefs import FILE ...");
        }
    }

    static async Task<int> Vendor(List<string> argv)
    {
        var sub = argv.Count > 0 ? argv[0].ToLowerInvariant() : "list";
        if (argv.Count > 0) argv.RemoveAt(0);
        var hw = OptiGame.Core.Hardware.HardwareDetector.Detect();
        var live = new OptiGame.Core.Live.LiveStore(AppPaths.DataDir);
        var apps = OptiGame.Core.Vendors.VendorCatalog.DetectHere(hw, live.Data.VendorAppPath, live.Data.VendorAppName);
        if (sub == "list")
        {
            Console.WriteLine($"This PC: {hw.ModelName}  (BIOS maker \"{hw.Manufacturer}\", model \"{hw.Product}\")");
            if (apps.Count == 0) Console.WriteLine("No vendor app found. Add one with the app's Settings > Choose your vendor app, or edit " + OptiGame.Core.Vendors.VendorCatalog.UserFile);
            foreach (var a in apps)
                Console.WriteLine($"  {a.Name,-26} {a.Source,-24} {(a.IsStoreApp ? a.PackageFamily + (a.AppId is null ? "" : "!" + a.AppId) : a.ExePath)}");
            return 0;
        }
        if (sub == "open")
        {
            var name = string.Join(' ', argv);
            var app = name.Length == 0 ? apps.FirstOrDefault(a => a.Kind == "maker" || a.UserPicked) : apps.FirstOrDefault(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (app is null) return Fail(name.Length == 0 ? "No vendor app was found." : $"No vendor app matching '{name}'. Run 'optigame-cli vendor list'.");
            var err = await OptiGame.Core.Vendors.VendorCatalog.LaunchAsync(app);
            Console.WriteLine(err.Length == 0 ? $"Opened {app.Name}." : err);
            return err.Length == 0 ? 0 : 1;
        }
        return Fail("Use: optigame-cli vendor [list] | optigame-cli vendor open [NAME]");
    }

    static async Task<int> SpecCheck(List<string> argv)
    {
        var name = Opt(argv, "--game").FirstOrDefault();
        NoMore(argv);
        var live = new OptiGame.Core.Live.LiveStore(AppPaths.DataDir);
        var install = OptiGame.Core.SpecCheck.SpecCheckLauncher.Locate(live.Data.SpecCheckPath);
        Console.WriteLine(install is null ? "Game Spec Check was not found." : $"Game Spec Check: {install.Path} (version {install.Version})");
        if (name is null || install is null) return install is null ? 1 : 0;
        var s = Load(quietNotes: true);
        var game = s.Find(name);
        if (game is null) return Fail($"No single game matches '{name}'.");
        var res = await OptiGame.Core.SpecCheck.SpecCheckLauncher.OpenForAsync(game, s.AppIdFor(game), live.Data.SpecCheckPath);
        Console.WriteLine(res.Message + (res.Url is null ? "" : "  " + res.Url));
        return res.Ok ? 0 : 1;
    }
}
