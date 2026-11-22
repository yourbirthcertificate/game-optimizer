using System.Text;
using System.Text.Json;
using OptiGame.Core.Bench;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Optimize;
using OptiGame.Core.Rules;
using OptiGame.Core.Safety;

namespace OptiGame.Tests;

/// <summary>Synthetic displays: a 165 Hz panel currently running at 60 Hz, and a 59.94 Hz TV.</summary>
public class DisplayTests
{
    internal static DisplayInfo Panel(double currentHz = 60, bool primary = true, string name = @"\\.\DISPLAY1") => new()
    {
        DeviceName = name, IsPrimary = primary, CurrentWidth = 2560, CurrentHeight = 1440, CurrentHz = currentHz,
        Modes = { new(2560, 1440, 60), new(2560, 1440, 120), new(2560, 1440, 165), new(1920, 1080, 165), new(1280, 720, 60) },
    };

    internal static DisplayInfo Tv() => new()
    {
        DeviceName = @"\\.\DISPLAY2", CurrentWidth = 3840, CurrentHeight = 2160, CurrentHz = 59.94,
        Modes = { new(3840, 2160, 59.94), new(3840, 2160, 60), new(1920, 1080, 120) },
    };

    [Fact]
    public void Automatic_cap_uses_the_active_rate_not_the_highest_supported()
    {
        var d = Panel(currentHz: 60);
        Assert.Equal(165, d.MaxHz);
        Assert.Equal(60, d.CurrentHz);
        Assert.Equal(57, d.AutoFpsCap);
        Assert.Equal(162, Panel(currentHz: 165).AutoFpsCap);
    }

    [Fact]
    public void Fractional_rates_are_kept_and_shown()
    {
        var tv = Tv();
        Assert.Equal(56, tv.AutoFpsCap);          // floor(59.94) - 3
        Assert.Equal("59.94", DisplayMath.Hz(tv.CurrentHz));
        Assert.Equal("165", DisplayMath.Hz(165.0004));
        Assert.Equal((3840, 2160), tv.Native);
    }

    [Fact]
    public void Gaming_display_and_per_game_override_are_used_and_a_missing_one_falls_back()
    {
        var hw = new HardwareInfo { Displays = { Panel(165), Tv() } };
        var prefs = new Prefs { GamingDisplay = @"\\.\DISPLAY2" };
        Assert.Equal(@"\\.\DISPLAY2", OptimizeRequest.DisplayFor(prefs, "steam:1", hw).DeviceName);
        prefs.Game("steam:1").Display = @"\\.\DISPLAY1";
        Assert.Equal(@"\\.\DISPLAY1", OptimizeRequest.DisplayFor(prefs, "steam:1", hw).DeviceName);
        prefs.Game("steam:1").Display = @"\\.\DISPLAY9";
        var notes = new List<string>();
        Assert.True(OptimizeRequest.DisplayFor(prefs, "steam:1", hw, notes).IsPrimary);
        Assert.Single(notes);
    }

    [Fact]
    public void Custom_resolution_must_be_a_mode_of_the_display()
    {
        var prefs = new Prefs();
        prefs.Game("g").Resolution = new ResolutionPref { Mode = ResolutionMode.Custom, Width = 1920, Height = 1080 };
        Assert.Equal((1920, 1080), OptimizeRequest.ResolutionFor(prefs, "g", Panel()));
        prefs.Game("g").Resolution = new ResolutionPref { Mode = ResolutionMode.Custom, Width = 1600, Height = 900 };
        var notes = new List<string>();
        Assert.Null(OptimizeRequest.ResolutionFor(prefs, "g", Panel(), notes));
        Assert.Single(notes);
        prefs.Game("g").Resolution = new ResolutionPref { Mode = ResolutionMode.Keep };
        Assert.Null(OptimizeRequest.ResolutionFor(prefs, "g", Panel()));
    }
}

public class PrefsTests
{
    static readonly GameInfo Game = new() { Id = "steam:1", Name = "Test", InstallDir = @"C:\x", Store = "Steam" };

    [Fact]
    public void Version1_file_migrates_hand_set_steps()
    {
        var p = Prefs.Parse("""{"GlobalOffset":-1,"PerGame":{"steam:1":5,"steam:2":"Low"},"SharedCacheDirs":["C:\\c"]}""", out var migrated);
        Assert.True(migrated);
        Assert.Equal(Prefs.CurrentVersion, p.Version);
        Assert.Equal(-1, p.GlobalOffset);
        Assert.Equal(QualityStep.Ultra, p.Game("steam:1").Step);
        Assert.Equal(QualityStep.Low, p.Game("steam:2").Step);
        Assert.Single(p.SharedCacheDirs);
        Assert.DoesNotContain("PerGame", p.ToJson());
    }

    [Fact]
    public void Quality_precedence_hand_set_then_game_profile_then_default_with_global_offset()
    {
        var p = new Prefs { GlobalOffset = 1 };
        Assert.Equal(QualityStep.VeryHigh, p.StepFor("steam:1", QualityStep.High));            // Balanced offset 0 + global 1
        p.Game("steam:1").ProfileId = "quiet";
        Assert.Equal(QualityStep.High, p.StepFor("steam:1", QualityStep.High));                 // quiet -1 + global 1
        var mine = p.CreateProfile("Locked");
        mine.Quality = new QualityPref { Mode = QualityMode.Fixed, Step = QualityStep.Medium };
        p.Game("steam:1").ProfileId = mine.Id;
        Assert.Equal(QualityStep.Medium, p.StepFor("steam:1", QualityStep.Ultra));              // fixed ignores offsets
        p.Game("steam:1").Step = QualityStep.Low;
        Assert.Equal(QualityStep.Low, p.StepFor("steam:1", QualityStep.Ultra));                 // hand-set wins
    }

    [Fact]
    public void Other_settings_come_from_game_then_its_profile_then_default_then_balanced()
    {
        var hw = new HardwareInfo { Displays = { DisplayTests.Panel(165) } };
        var p = new Prefs();
        var r = OptimizeRequest.Resolve(p, Game, hw, QualityStep.High);
        Assert.Equal(FpsMode.Auto, r.FpsMode);
        Assert.Equal(162, r.FpsCap);
        p.Game(Game.Id).ProfileId = "quiet";
        r = OptimizeRequest.Resolve(p, Game, hw, QualityStep.High);
        Assert.Equal(60, r.FpsCap);
        Assert.Equal(VSyncChoice.Keep, r.VSync);
        p.Game(Game.Id).Fps = new FpsPolicy { Mode = FpsMode.Custom, Value = 90 };
        r = OptimizeRequest.Resolve(p, Game, hw, QualityStep.High);
        Assert.Equal(90, r.FpsCap);
        p.Game(Game.Id).Fps = new FpsPolicy { Mode = FpsMode.Keep };
        Assert.Null(OptimizeRequest.Resolve(p, Game, hw, QualityStep.High).FpsCap);
    }

    [Fact]
    public void Profiles_can_be_created_duplicated_renamed_and_deleted_but_built_ins_are_fixed()
    {
        var p = new Prefs();
        var a = p.CreateProfile("My quiet", p.FindProfile("quiet"));
        Assert.Equal(60, a.Fps!.Value);
        var b = p.CreateProfile("My quiet");
        Assert.NotEqual(a.Id, b.Id);
        p.RenameProfile(a.Id, "Evening");
        Assert.Equal("Evening", p.FindProfile(a.Id)!.Name);
        p.Game("steam:1").ProfileId = a.Id;
        Assert.Equal(1, p.DeleteProfile(a.Id));
        Assert.Null(p.Game("steam:1").ProfileId);
        Assert.Throws<ArgumentException>(() => p.DeleteProfile("balanced"));
        Assert.Throws<ArgumentException>(() => p.RenameProfile("quiet", "x"));
    }

    [Fact]
    public void Settings_parser_reads_choices_and_rejects_bad_ones()
    {
        var s = new GamePrefs();
        SettingsParser.Apply(s, "fps=59.94");
        SettingsParser.Apply(s, "resolution=1920x1080");
        SettingsParser.Apply(s, "preserve=renderScale,texture");
        SettingsParser.Apply(s, "quality=offset:-2");
        Assert.Equal(59.94, s.Fps!.Value);
        Assert.Equal(ResolutionMode.Custom, s.Resolution!.Mode);
        Assert.Equal(new[] { "renderScale", "texture" }, s.Preserve);
        Assert.Equal(-2, s.Quality!.Offset);
        Assert.Throws<ArgumentException>(() => SettingsParser.Apply(s, "fps=fast"));
        Assert.Throws<ArgumentException>(() => SettingsParser.Apply(s, "preserve=everything"));
        SettingsParser.Apply(s, "fps=inherit");
        Assert.Null(s.Fps);
    }
}

/// <summary>A fake game with a rule-driven ini file in a temporary folder.</summary>
[Collection("data dir")]
public sealed class PlanAndRestoreTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "gameopt-x-" + Guid.NewGuid().ToString("N"));
    readonly string? _oldData = Environment.GetEnvironmentVariable("GAMEOPT_DATA_DIR");
    readonly GameInfo _game;
    readonly HardwareInfo _hw = new() { Displays = { DisplayTests.Panel(165) }, Gpu = "NVIDIA GeForce RTX 3080 Laptop GPU", GpuVramGb = 8 };
    const string Ini = "[Video]\r\nWidth=1280\r\nHeight=720\r\nVSync=True\r\nMaxFps=60\r\nShadows=1\r\nTextures=1\r\nRenderScale=70\r\n";

    public PlanAndRestoreTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game"));
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", Path.Combine(_root, "data"));
        _game = new GameInfo { Id = "test:1", Name = "Test Game", InstallDir = Path.Combine(_root, "game"), Store = "Test" };
        File.WriteAllText(Path.Combine(_root, "game", "settings.ini"), Ini);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", _oldData);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static GameRule Rule(string? uncapped = null) => RuleSet.TryParse($$"""
        {
          "id": "test-game", "name": "Test Game", "match": { "installDirs": ["game"] },
          {{(uncapped is null ? "" : $"\"fpsUncappedValue\": {uncapped},")}}
          "files": [ { "format": "ini", "path": "{install}/settings.ini", "set": {
            "Video/Width": "$width", "Video/Height": "$height", "Video/VSync": "$vsync", "Video/MaxFps": "$fpsCap",
            "Video/Shadows": [0, 1, 1, 2, 3, 3], "Video/Textures": "$ue.Texture", "Video/RenderScale": 100 },
            "categories": { "Video/RenderScale": "renderScale" } } ]
        }
        """, out var problems) ?? throw new Exception(string.Join("; ", problems));

    string FilePath => Path.Combine(_game.InstallDir, "settings.ini");

    GamePlan Plan(Prefs p, IEnumerable<string>? temp = null, string? uncapped = null) =>
        Planner.Plan(_game, _hw, OptimizeRequest.Resolve(p, _game, _hw, QualityStep.High, temp), new[] { Rule(uncapped) });

    static string Text(GamePlan p) => Encoding.UTF8.GetString(p.Files[0].Updated);

    [Fact]
    public void Fps_policies_auto_custom_keep_and_uncapped()
    {
        var p = new Prefs();
        Assert.Contains("MaxFps=162", Text(Plan(p)));
        p.Game(_game.Id).Fps = new FpsPolicy { Mode = FpsMode.Custom, Value = 90 };
        Assert.Contains("MaxFps=90", Text(Plan(p)));
        p.Game(_game.Id).Fps = new FpsPolicy { Mode = FpsMode.Keep };
        var keep = Plan(p);
        Assert.Contains("MaxFps=60", Text(keep));
        Assert.Contains(keep.Skipped, s => s.Contains("MaxFps"));
        p.Game(_game.Id).Fps = new FpsPolicy { Mode = FpsMode.Uncapped };
        Assert.Contains("MaxFps=60", Text(Plan(p)));                // the rule does not say how "no cap" is written
        Assert.Contains("MaxFps=0", Text(Plan(p, uncapped: "0")));  // ...unless it does
    }

    [Fact]
    public void Excluded_and_preserved_settings_never_reach_the_written_bytes()
    {
        var p = new Prefs();
        p.Game(_game.Id).Excluded.Add("rule:settings.ini:Video/VSync");
        p.Game(_game.Id).Preserve = new() { Categories.RenderScale };
        var plan = Plan(p, temp: new[] { "rule:settings.ini:Video/Shadows" });
        var text = Text(plan);
        Assert.Contains("VSync=True", text);
        Assert.Contains("RenderScale=70", text);
        Assert.Contains("Shadows=1", text);
        Assert.Contains("Width=2560", text);
        Assert.Equal(3, plan.Excluded.Count);
        Assert.DoesNotContain(plan.Files[0].Changes, c => c.Id.EndsWith("VSync") || c.Id.EndsWith("RenderScale") || c.Id.EndsWith("Shadows"));
        Assert.Contains(plan.Excluded, e => e.Why.StartsWith("kept by profile"));
        Assert.Contains(plan.Excluded, e => e.Why == "unticked for this run");
    }

    [Fact]
    public void Apply_only_writes_included_changes_and_the_stale_check_still_holds()
    {
        var p = new Prefs();
        p.Game(_game.Id).Excluded.Add("rule:settings.ini:Video/Width");
        var plan = Plan(p);
        var res = Applier.Apply(new[] { plan }, "test");
        Assert.Empty(res.Errors);
        var after = File.ReadAllText(FilePath);
        Assert.Contains("Width=1280", after);
        Assert.Contains("Height=1440", after);
        File.WriteAllText(FilePath, Ini);
        var stale = Plan(p);
        File.AppendAllText(FilePath, "Extra=1\r\n");
        Assert.Single(Applier.Apply(new[] { stale }, "test").Refused);
    }

    [Fact]
    public void Undo_keeps_unrelated_later_edits_and_reports_conflicts()
    {
        var res = Applier.Apply(new[] { Plan(new Prefs()) }, "test");
        Assert.NotNull(res.RunId);
        // Later edits: the game changes Width again (conflict) and adds an unrelated key.
        var text = File.ReadAllText(FilePath).Replace("Width=2560", "Width=1920") + "Gamma=2.2\r\n";
        File.WriteAllText(FilePath, text);

        var run = Applier.Runs().First(r => r.Id == res.RunId);
        var plan = Applier.PlanRestore(run);
        var item = Assert.Single(plan.Items);
        Assert.True(item.CanUndoChanges);
        Assert.Equal(1, item.ConflictCount);
        var undo = Applier.Restore(plan, RestoreMode.UndoChanges);
        Assert.Empty(undo.Errors);
        var restored = File.ReadAllText(FilePath);
        Assert.Contains("Width=1920", restored);   // conflict left alone
        Assert.Contains("Gamma=2.2", restored);    // unrelated edit kept
        Assert.Contains("Height=720", restored);   // optimizer change undone
        Assert.Contains("MaxFps=60", restored);
        Assert.NotNull(undo.RunId);                // the state before the restore was saved
        Assert.Equal("restore", Applier.Runs().First(r => r.Id == undo.RunId).Kind);
    }

    [Fact]
    public void Full_restore_replaces_everything_and_can_itself_be_undone()
    {
        var res = Applier.Apply(new[] { Plan(new Prefs()) }, "test");
        File.AppendAllText(FilePath, "Gamma=2.2\r\n");
        var run = Applier.Runs().First(r => r.Id == res.RunId);
        var back = Applier.Restore(Applier.PlanRestore(run), RestoreMode.FullBackup);
        Assert.Equal(Ini, File.ReadAllText(FilePath));
        var safety = Applier.Runs().First(r => r.Id == back.RunId);
        Applier.Restore(Applier.PlanRestore(safety), RestoreMode.FullBackup);
        Assert.Contains("Gamma=2.2", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Restore_refuses_when_the_file_changed_after_the_restore_preview()
    {
        var res = Applier.Apply(new[] { Plan(new Prefs()) }, "test");
        var plan = Applier.PlanRestore(Applier.Runs().First(r => r.Id == res.RunId));
        File.AppendAllText(FilePath, "Late=1\r\n");
        var r = Applier.Restore(plan, RestoreMode.UndoChanges);
        Assert.Single(r.Refused);
        Assert.Contains("Late=1", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Wizard_builds_a_valid_rule_from_snapshots_without_guessing_steps()
    {
        var before = Snapshot.Capture("ini", FilePath);
        File.WriteAllText(FilePath, Ini.Replace("Shadows=1", "Shadows=3"));
        var after = Snapshot.Capture("ini", FilePath);
        var diff = Assert.Single(Snapshot.Diff(before, after));
        Assert.Equal("Video/Shadows", diff.Key);
        Assert.Equal("int", diff.After!.Type);

        var one = new DraftKey { Key = diff.Key, Type = "int", Values = { "3" }, Category = "quality" };
        var (json, rule, problems) = RuleBuilder.Build(_game, "ini", FilePath, new[] { one });
        Assert.DoesNotContain(problems, p => p.Severity == RuleProblemSeverity.Error);
        Assert.Equal("{install}/settings.ini", rule.Files[0].Path);
        Assert.Equal(JsonValueKind.Number, rule.Files[0].Set["Video/Shadows"].ValueKind); // one value, not a made-up ladder

        var two = new DraftKey { Key = diff.Key, Type = "int", Values = { "1", "3" } };
        Assert.Throws<FormatException>(() => RuleBuilder.Build(_game, "ini", FilePath, new[] { two }));
        var six = new DraftKey { Key = diff.Key, Type = "int", Values = { "0", "1", "1", "2", "3", "3" } };
        Assert.Equal(6, RuleBuilder.Build(_game, "ini", FilePath, new[] { six }).rule.Files[0].Set["Video/Shadows"].GetArrayLength());
        Assert.Throws<FormatException>(() => RuleBuilder.Build(_game, "ini", FilePath, new[] { new DraftKey { Key = "Video/Shadows", Type = "int", Values = { "high" } } }));
    }

    [Fact]
    public void Snapshot_never_writes()
    {
        var bytes = File.ReadAllBytes(FilePath);
        var time = File.GetLastWriteTimeUtc(FilePath);
        Snapshot.Capture("ini", FilePath);
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        Assert.Equal(time, File.GetLastWriteTimeUtc(FilePath));
    }

    [Fact]
    public void Export_import_round_trip_with_remap_conflicts_and_validation()
    {
        var src = new Prefs { GlobalOffset = -1 };
        var prof = src.CreateProfile("Evening", src.FindProfile("quiet"));
        src.Game("steam:9").ProfileId = prof.Id;
        src.Game("steam:9").Excluded.Add("ue:bUseVSync");
        src.Game("steam:9").Display = @"\\.\DISPLAY7";
        src.ManualGames.Add(new ManualGame { Path = @"Z:\Games\Old", Name = "Old" });
        src.Game(LibraryScanner.ManualId(@"Z:\Games\Old")).Step = QualityStep.Low;
        var json = PrefsTransfer.Export(src, includeRules: false);
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.Equal("gameopt-preferences", doc.RootElement.GetProperty("format").GetString());
            Assert.False(doc.RootElement.GetProperty("portable").GetProperty("games").GetProperty("steam:9").TryGetProperty("Display", out _));
        }

        var dst = new Prefs();
        dst.Game("steam:9").Step = QualityStep.Ultra; // differs from the import: a conflict
        var noRules = new Dictionary<string, string>();
        var plan = PrefsTransfer.Analyze(json, dst, ImportMode.Merge, ConflictPolicy.KeepMine, existingRules: noRules,
            folderExists: p => p == @"D:\Games\Old", displayExists: _ => false);
        Assert.False(plan.CanApply);
        Assert.Contains(@"Z:\Games\Old", plan.NeedsRemap);

        plan = PrefsTransfer.Analyze(json, dst, ImportMode.Merge, ConflictPolicy.KeepMine,
            new Dictionary<string, string> { [@"Z:\Games\Old"] = @"D:\Games\Old" }, noRules, p => p == @"D:\Games\Old", _ => false);
        Assert.True(plan.CanApply);
        Assert.Contains(plan.Additions, a => a.Contains("Evening"));
        Assert.Contains(plan.Conflicts, c => c.Contains("steam:9"));
        Assert.Equal(QualityStep.Ultra, plan.Result.Game("steam:9").Step);       // yours kept
        Assert.Equal(QualityStep.Low, plan.Result.Game(LibraryScanner.ManualId(@"D:\Games\Old")).Step);
        Assert.Contains(plan.Skipped, s => s.Contains("DISPLAY7"));

        var take = PrefsTransfer.Analyze(json, dst, ImportMode.Merge, ConflictPolicy.TakeImported,
            new Dictionary<string, string> { [@"Z:\Games\Old"] = @"D:\Games\Old" }, noRules, _ => true, _ => true);
        Assert.Equal(prof.Id, take.Result.Game("steam:9").ProfileId);

        Assert.NotEmpty(PrefsTransfer.Analyze("{\"format\":\"other\"}", dst, ImportMode.Merge, ConflictPolicy.KeepMine).Errors);
        Assert.NotEmpty(PrefsTransfer.Analyze("{\"format\":\"gameopt-preferences\",\"version\":99}", dst, ImportMode.Merge, ConflictPolicy.KeepMine).Errors);
    }
}

public class RuleValidationTests
{
    static List<RuleProblem> Check(string json) { RuleSet.TryParse(json, out var p); return p; }

    [Fact]
    public void Older_rules_without_metadata_are_valid() =>
        Assert.DoesNotContain(Check("""{"id":"x","name":"X","match":{"installDirs":["X"]},"files":[{"format":"ini","path":"{install}/a.ini","set":{"A/B":1}}]}"""),
            p => p.Severity == RuleProblemSeverity.Error);

    [Theory]
    [InlineData("""{"id":"x","match":{},"files":[{"format":"ini","path":"a","set":{"A/B":1}}]}""", "match")]
    [InlineData("""{"id":"x","match":{"installDirs":["X"]},"files":[{"format":"toml","path":"a","set":{"a":1}}]}""", "format")]
    [InlineData("""{"id":"x","match":{"installDirs":["X"]},"files":[{"format":"ini","path":"a","set":{"NoSection":1}}]}""", "Section/Key")]
    [InlineData("""{"id":"x","match":{"installDirs":["X"]},"files":[{"format":"ini","path":"a","set":{"A/B":"$turbo"}}]}""", "unknown variable")]
    [InlineData("""{"id":"x","match":{"installDirs":["X"]},"files":[{"format":"ini","path":"a","set":{"A/B":[1,2,3]}}]}""", "exactly 6")]
    [InlineData("""{"id":"x","match":{"installDirs":["X"]},"files":[{"format":"ini","path":"a","set":{"A/B":1,"A/B":2}}]}""", "listed 2 times")]
    [InlineData("""{"id":"x","match":{"installDirs":["X"]},"files":[{"format":"ini","path":"a","set":{"A/B":1}},{"format":"ini","path":"a","set":{"A/C":1}}]}""", "listed twice")]
    [InlineData("""{"id":"x","match":{"installDirs":["X"]},"files":[{"format":"registry","path":"HKLM/Software/X","set":{"v":1}}]}""", "HKCU")]
    [InlineData("""{"id":"x","match":{"installDirs":["X"]},"files":[{"format":"kv","path":"a","set":{"k":"$nativeResolution"}}]}""", "width/height")]
    [InlineData("""{"id":"Bad Id","match":{"installDirs":["X"]},"files":[{"format":"ini","path":"a","set":{"A/B":1}}]}""", "lowercase")]
    [InlineData("""{"id":"x","match":{"installDirs":["X"]},"files":[{"format":"ini","path":"a","set":{"A/B":1},"categories":{"A/B":"speed"}}]}""", "not one of")]
    public void Problems_are_reported_with_helpful_messages(string json, string expected) =>
        Assert.Contains(Check(json), p => p.Severity == RuleProblemSeverity.Error && p.ToString().Contains(expected));

    [Fact]
    public void Bad_metadata_is_a_warning_not_an_error()
    {
        var p = Check("""{"id":"x","match":{"installDirs":["X"]},"verified":"last week","files":[{"format":"ini","path":"a","set":{"A/B":1}}]}""");
        Assert.Contains(p, x => x.Severity == RuleProblemSeverity.Warning && x.Where == "verified");
        Assert.DoesNotContain(p, x => x.Severity == RuleProblemSeverity.Error);
    }

    [Fact]
    public void Shipped_rules_and_examples_validate()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "rules");
        var files = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Where(f => !f.EndsWith(".schema.json")).ToList();
        Assert.NotEmpty(files);
        foreach (var f in files)
            Assert.DoesNotContain(Check(File.ReadAllText(f)), p => p.Severity == RuleProblemSeverity.Error);
    }
}

public class DiscoveryTests
{
    [Fact]
    public void Same_folder_from_several_sources_becomes_one_entry()
    {
        var games = new[]
        {
            new GameInfo { Id = "manual:c:\\games\\a", Name = "A (mine)", InstallDir = @"C:\Games\A\", Store = "Manual" },
            new GameInfo { Id = "gog:1", Name = "A", InstallDir = @"c:\games\a", Store = "GOG", GogId = "1" },
            new GameInfo { Id = "steam:5", Name = "A", InstallDir = @"C:\Games\A", Store = "Steam", SteamAppId = "5" },
            new GameInfo { Id = "steam:6", Name = "B", InstallDir = @"C:\Games\B", Store = "Steam" },
        };
        var merged = LibraryScanner.Deduplicate(games);
        Assert.Equal(2, merged.Count);
        var a = merged.Single(g => g.Name == "A");
        Assert.Equal("steam:5", a.Id);
        Assert.Equal(new[] { "Steam", "GOG", "Added by you" }, a.Sources);
    }

    [Fact]
    public void Epic_installs_without_a_launcher_record_are_found_by_their_store_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "gameopt-epic-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Make(string name, params string[] storeEntries)
            {
                var store = Directory.CreateDirectory(Path.Combine(root, name, ".egstore")).FullName;
                foreach (var e in storeEntries)
                    if (e.EndsWith(".manifest")) File.WriteAllText(Path.Combine(store, e), "x");
                    else Directory.CreateDirectory(Path.Combine(store, e));
            }
            Make("Complete", "ABC.manifest");
            Make("Downloading", "ABC.manifest", "bps");
            Make("NoManifest");
            Make("Listed", "DEF.manifest");
            Directory.CreateDirectory(Path.Combine(root, "Launcher"));

            var found = LibraryScanner.FindEpicFolders(new[] { root, root + "\\" }, new[] { Path.Combine(root, "Listed") });
            var g = Assert.Single(found);
            Assert.Equal("Complete", g.Name);
            Assert.Equal("epic:Complete", g.Id);
            Assert.Equal("Epic", g.Store);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Linked_app_id_is_explicit_and_never_guessed_from_the_name()
    {
        var hw = new HardwareInfo();
        var s = new OptimizerSession { Hw = hw, Scan = new ScanResult(), Prefs = new Prefs() };
        var gog = new GameInfo { Id = "gog:1", Name = "Half-Life 2", InstallDir = @"C:\x", Store = "GOG" };
        Assert.Null(s.AppIdFor(gog));
        Assert.Equal("none", s.AppIdSource(gog));
        s.Prefs.Game(gog.Id).SteamAppId = "220";
        Assert.Equal("220", s.AppIdFor(gog));
        Assert.Equal("linked by you", s.AppIdSource(gog));
    }
}

public class BenchmarkTests
{
    static string Csv(string header, IEnumerable<double> values) =>
        header + "\n" + string.Join("\n", values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Metrics_follow_their_definitions()
    {
        // 99 frames at 10 ms and 1 at 50 ms: 100 frames in 1.04 s.
        var frames = Enumerable.Repeat(10.0, 99).Append(50.0).ToList();
        var m = BenchMetrics.From(frames);
        Assert.Equal(100, m.Frames);
        Assert.Equal(1.04, m.DurationSeconds, 6);
        Assert.Equal(100 / 1.04, m.AverageFps, 6);
        Assert.Equal(10.0, m.P99FrameTimeMs); // nearest rank: the 99th of 100 sorted values
        Assert.Equal(100.0, m.OnePercentLowFps, 6);
        Assert.Equal(50.0, m.MaxFrameTimeMs);
    }

    [Fact]
    public void Both_formats_are_read()
    {
        var (f1, fmt1) = FrameTimeCsv.Parse(Csv("frametime_ms", Enumerable.Repeat(16.6, 120)));
        Assert.Equal(120, f1.Count);
        Assert.Equal("GameOptimizer CSV", fmt1);
        var pm = "Application,ProcessID,MsBetweenPresents,Dropped\n" + string.Join("\n", Enumerable.Repeat("game.exe,1,8.33,0", 150));
        var (f2, fmt2) = FrameTimeCsv.Parse(pm);
        Assert.Equal(150, f2.Count);
        Assert.Contains("PresentMon", fmt2);
    }

    [Theory]
    [InlineData("fps\n60\n61", "frame rates per row")]
    [InlineData("time,value\n1,2", "No frame-time column")]
    [InlineData("frametime_ms\n16\n-3", "not positive")]
    [InlineData("frametime_ms\n16\nabc", "not a number")]
    [InlineData("frametime_ms\n16\n9000", "over 5000")]
    [InlineData("frametime_ms\n16\n16", "at least 100")]
    public void Malformed_data_is_rejected_with_a_reason(string csv, string expected)
    {
        var e = Assert.Throws<FormatException>(() => FrameTimeCsv.Parse(csv));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void Seconds_instead_of_milliseconds_is_caught()
    {
        var e = Assert.Throws<FormatException>(() => FrameTimeCsv.Parse(Csv("frametime_ms", Enumerable.Repeat(0.0166, 200))));
        Assert.Contains("seconds", e.Message);
    }

    [Fact]
    public void Comparison_warns_when_captures_are_not_like_for_like()
    {
        BenchResult R(string label, double ms, int n, string res) => new()
        {
            Id = label, GameId = "g", GameName = "G", Label = label, Resolution = res, FpsCap = "162", Hardware = "h",
            Metrics = BenchMetrics.From(Enumerable.Repeat(ms, n).ToList()),
        };
        var c = BenchmarkStore.Compare(R("before", 20, 3000, "2560x1440"), R("after", 10, 3000, "1920x1080"));
        Assert.Contains(c.Differences, d => d.StartsWith("Average FPS: 50 -> 100 (+100%)"));
        Assert.Contains(c.Warnings, w => w.Contains("Durations differ"));
        Assert.Contains(c.Warnings, w => w.Contains("Resolutions differ"));
    }
}

public class FixtureTests
{
    static string F(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Synthetic_captures_parse_in_both_formats()
    {
        var (a, fa) = FrameTimeCsv.Parse(File.ReadAllText(F("frametimes-synthetic.csv")));
        Assert.Equal(600, a.Count);
        Assert.Equal("GameOptimizer CSV", fa);
        var m = BenchMetrics.From(a);
        Assert.InRange(m.AverageFps, 55, 61);
        Assert.True(m.OnePercentLowFps < m.AverageFps);
        var (b, fb) = FrameTimeCsv.Parse(File.ReadAllText(F("presentmon-synthetic.csv")));
        Assert.Equal(400, b.Count);
        Assert.Contains("MsBetweenPresents", fb);
    }

    [Fact]
    public void Snapshot_fixture_pair_diffs_to_one_key()
    {
        var d = Snapshot.Diff(Snapshot.FromJson(File.ReadAllText(F("snapshot-before.json"))), Snapshot.FromJson(File.ReadAllText(F("snapshot-after.json"))));
        var only = Assert.Single(d);
        Assert.Equal("Video/Shadows", only.Key);
        Assert.Equal("changed", only.Kind);
    }
}
