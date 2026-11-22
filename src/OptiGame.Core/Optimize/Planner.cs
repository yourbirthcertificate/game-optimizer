using System.Globalization;
using OptiGame.Core.Formats;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using Microsoft.Win32;

namespace OptiGame.Core.Optimize;

/// <summary>
/// Builds a GamePlan: reads the game's settings, works out new values for the request and keeps the full
/// new file contents in memory. Never writes anything. Changes the request keeps (a profile's "keep",
/// a game's saved exclusions, a one-off untick) are never put into the new contents; they are listed in
/// plan.Excluded instead. Anything that cannot be read with confidence is skipped with a reason.
/// </summary>
public static partial class Planner
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Plan with the default (Balanced) preferences at a given step.</summary>
    public static GamePlan Plan(GameInfo g, HardwareInfo hw, QualityStep step, IReadOnlyList<GameRule> rules) =>
        Plan(g, hw, OptimizeRequest.Default(hw, step), rules);

    public static GamePlan Plan(GameInfo g, HardwareInfo hw, OptimizeRequest req, IReadOnlyList<GameRule> rules)
    {
        var plan = new GamePlan { Game = g, Step = req.Step, Request = req };
        plan.Warnings.AddRange(req.Notes);
        var rule = rules.FirstOrDefault(r => r.Matches(g));
        plan.RuleId = rule?.Id;

        if (rule is null || !rule.ReplaceEngineHandler)
        {
            if (IsEldenRing(g)) EldenRing(plan, req);
            else if (IsHelldivers(g)) Helldivers(plan, req);
            else if (IsZomboid(g)) Zomboid(plan, req);
            else switch (g.Engine)
            {
                case GameEngine.Unreal: Unreal(plan, hw, req, rule); break;
                case GameEngine.Unity: Unity(plan, req); break;
                case GameEngine.Source: Source(plan, hw, req); break;
                case GameEngine.RedEngine: RedEngine(plan, hw, req); break;
                case GameEngine.Source2:
                    if (rule is null) { plan.Skip(SkipKind.NoHandler, "Source 2 game: its settings format is not supported yet"); LikelySettings(plan); }
                    break;
                default:
                    if (rule is null)
                    {
                        plan.Skip(SkipKind.NoHandler, g.Engine == GameEngine.Unknown
                            ? "Engine not recognized and no rule for this game"
                            : $"{g.Engine} game with no rule for it yet");
                        LikelySettings(plan);
                    }
                    break;
            }
        }
        if (rule is not null)
        {
            var vars = Profiles.Variables(req, hw, rule);
            foreach (var f in rule.Files) RuleFileEdit(plan, g, rule, f, req, vars);
        }

        bool examined = plan.Files.Count > 0 || plan.Examined;
        plan.Files.RemoveAll(f => f.Changes.Count == 0);
        plan.Status = plan.HasChanges ? PlanStatus.Ready : examined ? PlanStatus.NothingToChange : PlanStatus.Skipped;
        if (examined) plan.SkipKind = SkipKind.None;
        AddActions(plan);
        return plan;
    }

    static void AddActions(GamePlan plan)
    {
        switch (plan.SkipKind)
        {
            case SkipKind.NotCreatedYet:
                plan.Actions.Add(PlanAction.LaunchOnceAndRescan);
                if (plan.SettingsFolder is not null && Directory.Exists(plan.SettingsFolder)) plan.Actions.Add(PlanAction.OpenSettingsFolder);
                break;
            case SkipKind.LocationNotFound:
                if (plan.SettingsFolder is not null && Directory.Exists(plan.SettingsFolder)) plan.Actions.Add(PlanAction.OpenSettingsFolder);
                plan.Actions.Add(PlanAction.AddRule);
                break;
            case SkipKind.NoHandler or SkipKind.UnsupportedFormat or SkipKind.Ambiguous:
                if (plan.SettingsFolder is not null && Directory.Exists(plan.SettingsFolder)) plan.Actions.Add(PlanAction.OpenSettingsFolder);
                plan.Actions.Add(PlanAction.AddRule);
                break;
        }
    }

    /// <summary>For a game with no handler: name the folder (and settings-like files) that probably holds its settings.</summary>
    static void LikelySettings(GamePlan plan)
    {
        if (SettingsLocator.Find(plan.Game) is not { } c) return;
        plan.SettingsFolder ??= c.Folder;
        plan.Skipped.Add($"Its settings are probably in {PathDisplay.Short(c.Folder)} ({string.Join(", ", c.Files)}). Use Add a rule to teach the tool, or open the folder.");
    }

    /// <summary>True when the change goes ahead; otherwise it is recorded as kept.</summary>
    internal static bool Want(GamePlan plan, OptimizeRequest req, string id, string category, string label, string? old, string @new)
    {
        if (req.WhyKept(id, category) is not { } why) return true;
        plan.Excluded.Add(new ExcludedChange(id, category, label, old, @new, why));
        return false;
    }

    static string Fps(double v) => v.ToString("0.######", Inv);

    // ------------------------------------------------------------------ Unreal
    static readonly string[] UnrealPlatforms = { "Windows", "WindowsNoEditor", "WinGDK", "WindowsClient" };

    static IEnumerable<string> UnrealRoots(GameInfo g, GameRule? rule, string? local = null, string? documents = null)
    {
        var names = new[] { rule?.UnrealConfigName, g.EngineDetail }.Where(n => !string.IsNullOrEmpty(n)).Distinct();
        foreach (var n in names)
        {
            yield return Path.Combine(local ?? LocalAppData, n!);
            yield return Path.Combine(documents ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", n!);
        }
    }

    internal static IEnumerable<string> UnrealSettingsPaths(GameInfo g, GameRule? rule, string? local = null, string? documents = null) =>
        from root in UnrealRoots(g, rule, local, documents)
        from platform in UnrealPlatforms
        select Path.Combine(root, "Saved", "Config", platform, "GameUserSettings.ini");

    public static List<string> FindUnrealSettingsAll(GameInfo g, GameRule? rule) =>
        UnrealSettingsPaths(g, rule).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).ToList();

    public static string? FindUnrealSettings(GameInfo g, GameRule? rule) => FindUnrealSettingsAll(g, rule).FirstOrDefault();

    static void Unreal(GamePlan plan, HardwareInfo hw, OptimizeRequest req, GameRule? rule)
    {
        var all = FindUnrealSettingsAll(plan.Game, rule);
        if (all.Count == 0)
        {
            var existing = UnrealRoots(plan.Game, rule).FirstOrDefault(Directory.Exists);
            if (existing is null)
                plan.Skip(SkipKind.NotCreatedYet, "No GameUserSettings.ini yet. Start the game once so it creates its settings.",
                          UnrealRoots(plan.Game, rule).FirstOrDefault());
            else
                plan.Skip(SkipKind.LocationNotFound, $"The game's folder exists ({existing}) but has no Saved\\Config\\...\\GameUserSettings.ini.", existing);
            return;
        }
        var path = all[0];
        if (all.Count > 1)
            plan.Warnings.Add($"{all.Count} GameUserSettings.ini files found; using the most recently saved one ({Path.GetFileName(Path.GetDirectoryName(path))}).");
        plan.SettingsFolder = Path.GetDirectoryName(path);
        byte[] data = File.ReadAllBytes(path);
        IniDocument ini;
        try { ini = FormatHandlers.Ini.Read(data).Content; }
        catch (FormatException e) { plan.Skip(SkipKind.UnsupportedFormat, $"GameUserSettings.ini unreadable: {e.Message}", plan.SettingsFolder); return; }

        var edit = new FileEdit { Path = path, Original = data, Updated = data, Format = "ini" };

        void Set(string section, string key, string value, string label, string category, bool add = false)
        {
            var old = ini.Get(section, key);
            if (old == value || (old is null && !add)) return;
            var id = "ue:" + key;
            if (!Want(plan, req, id, category, label, old, value)) return;
            if (ini.Set(section, key, value, add, out var err))
                edit.Changes.Add(new(label, old, value) { Id = id, Category = category, Locator = $"ini|{section}|{key}", OldRaw = old, NewRaw = value });
            else plan.Skipped.Add($"Kept: {err}");
        }

        var gus = ini.Sections().FirstOrDefault(s => ini.Get(s, "ResolutionSizeX") is not null);
        if (gus is null)
            plan.Skipped.Add("Kept: resolution and display mode (no ResolutionSizeX in GameUserSettings.ini)");
        else
        {
            if (req.Resolution is var (w, h))
            {
                string ws = w.ToString(Inv), hs = h.ToString(Inv);
                Set(gus, "ResolutionSizeX", ws, "Resolution width", Categories.Resolution);
                Set(gus, "ResolutionSizeY", hs, "Resolution height", Categories.Resolution);
                Set(gus, "LastUserConfirmedResolutionSizeX", ws, "Confirmed resolution width", Categories.Resolution);
                Set(gus, "LastUserConfirmedResolutionSizeY", hs, "Confirmed resolution height", Categories.Resolution);
            }
            string? fm = req.DisplayMode switch
            {
                DisplayModeChoice.Recommended or DisplayModeChoice.Fullscreen => "0",
                DisplayModeChoice.Borderless => "1",
                DisplayModeChoice.Windowed => "2",
                _ => null,
            };
            if (fm is not null)
                foreach (var k in new[] { "FullscreenMode", "LastConfirmedFullscreenMode", "PreferredFullscreenMode" })
                    Set(gus, k, fm, $"{k} (0 fullscreen, 1 borderless, 2 windowed)", Categories.DisplayMode);
            if (req.VSync != VSyncChoice.Keep)
            {
                var old = ini.Get(gus, "bUseVSync");
                var v = req.VSync == VSyncChoice.On ? (old is "true" or "false" ? "true" : "True") : (old is "true" or "false" ? "false" : "False");
                Set(gus, "bUseVSync", v, "VSync", Categories.VSync);
            }
            var fr = ini.Get(gus, "FrameRateLimit");
            double? target = req.FpsMode switch { FpsMode.Uncapped => 0, FpsMode.Keep => null, _ => req.FpsCap };
            if (fr is not null && target is { } t && (!double.TryParse(fr, NumberStyles.Float, Inv, out var cur) || Math.Abs(cur - t) > 0.0001))
                Set(gus, "FrameRateLimit", t.ToString("0.000000", Inv), t == 0 ? "Frame rate limit (0 = none)" : "Frame rate limit", Categories.FpsCap);
        }

        const string sg = "ScalabilityGroups";
        foreach (var (group, level) in Profiles.Unreal(req.Step, hw))
            Set(sg, $"sg.{group}Quality", level.ToString(Inv), $"sg.{group}Quality",
                group == "Texture" ? Categories.Texture : Categories.Quality, add: true);
        var rq = ini.Get(sg, "sg.ResolutionQuality");
        if (rq is null || !double.TryParse(rq, NumberStyles.Float, Inv, out var rqv) || Math.Abs(rqv - 100) > 0.01)
            Set(sg, "sg.ResolutionQuality", rq?.Contains('.') == true ? "100.000000" : "100", "Render resolution %", Categories.RenderScale, add: true);
        edit.Updated = ini.ToBytes();
        plan.Files.Add(edit);
    }

    // ------------------------------------------------------------------ Unity
    static void Unity(GamePlan plan, OptimizeRequest req)
    {
        var g = plan.Game;
        if (string.IsNullOrEmpty(g.EngineDetail))
        { plan.Skip(SkipKind.LocationNotFound, "Unity game without app.info: cannot tell where it saves settings", g.InstallDir); return; }
        var sub = $@"Software\{g.EngineDetail}";
        using var key = Registry.CurrentUser.OpenSubKey(sub);
        if (key is null)
        { plan.Skip(SkipKind.NotCreatedYet, "No Unity settings in the registry yet: start the game once (some games keep their own settings file instead)"); return; }
        plan.Examined = true;
        var names = key.GetValueNames().ToHashSet();

        void SetDword(string pref, int value, string label, string category)
        {
            var name = UnityQuality.PrefName(pref);
            if (!names.Contains(name)) return;
            if (key.GetValueKind(name) != RegistryValueKind.DWord) { plan.Skipped.Add($"Kept: {label} (unexpected value type)"); return; }
            var old = (int)key.GetValue(name)!;
            if (old == value) return;
            var id = "unity:" + pref;
            if (!Want(plan, req, id, category, label, old.ToString(Inv), value.ToString(Inv))) return;
            plan.Registry.Add(new RegistryEdit
            { SubKey = sub, ValueName = name, Setting = label, Kind = RegistryValueKind.DWord, OldValue = old, NewValue = value, Id = id, Category = category });
        }

        if (req.Resolution is var (w, h))
        {
            if (!names.Contains(UnityQuality.PrefName("Screenmanager Resolution Width")))
                plan.Skipped.Add("Kept: resolution (the game does not use Unity's saved screen settings)");
            SetDword("Screenmanager Resolution Width", w, "Resolution width", Categories.Resolution);
            SetDword("Screenmanager Resolution Height", h, "Resolution height", Categories.Resolution);
            SetDword("Screenmanager Resolution Use Native", (w, h) == req.Display.Native ? 1 : 0, "Use native resolution", Categories.Resolution);
        }
        int? mode = req.DisplayMode switch
        {
            DisplayModeChoice.Fullscreen => 0,
            DisplayModeChoice.Recommended or DisplayModeChoice.Borderless => 1,
            DisplayModeChoice.Windowed => 3,
            _ => null,
        };
        if (mode is { } m) SetDword("Screenmanager Fullscreen mode", m, "Display mode (0 exclusive, 1 borderless, 3 windowed)", Categories.DisplayMode);
        if (req.FpsMode != FpsMode.Keep) plan.NotControlled.Add("Frame-rate cap: Unity games set it in code or their own menu; left as the game has it.");
        if (req.VSync != VSyncChoice.Keep) plan.NotControlled.Add("VSync: not stored in Unity's saved settings; left as the game has it.");

        var q = UnityQuality.PrefName("UnityGraphicsQuality");
        if (!names.Contains(q)) { plan.Skipped.Add("Kept: quality (the game uses its own settings menu)"); return; }
        var dataDir = Directory.EnumerateDirectories(g.InstallDir, "*_Data").FirstOrDefault(d => File.Exists(Path.Combine(d, "app.info")));
        var levels = dataDir is null ? null : UnityQuality.Read(dataDir, out _);
        if (levels is null) { plan.Skipped.Add("Kept: quality (could not read the game's quality level list)"); return; }
        int idx = req.Step.MapToLevels(levels.Names.Count);
        var before = key.GetValue(q) as int?;
        if (before == idx) return;
        var label = $"Quality level ({levels.Names[idx]}, {idx + 1} of {levels.Names.Count})";
        if (!Want(plan, req, "unity:UnityGraphicsQuality", Categories.Quality, label, before?.ToString(Inv), idx.ToString(Inv))) return;
        plan.Registry.Add(new RegistryEdit
        {
            SubKey = sub, ValueName = q, Kind = RegistryValueKind.DWord, OldValue = before, NewValue = idx, Setting = label,
            Id = "unity:UnityGraphicsQuality", Category = Categories.Quality,
        });
    }

    // ------------------------------------------------------------------ Source 1
    internal static string SourceSettingsPath(GameInfo game) =>
        Path.Combine(game.InstallDir, game.EngineDetail ?? "", "cfg", "video.txt");

    static void Source(GamePlan plan, HardwareInfo hw, OptimizeRequest req)
    {
        var path = SourceSettingsPath(plan.Game);
        var dir = Path.GetDirectoryName(path)!;
        if (!File.Exists(path)) { plan.Skip(SkipKind.NotCreatedYet, "No cfg\\video.txt yet. Start the game once so it creates its settings.", dir); return; }
        plan.SettingsFolder = dir;
        var data = File.ReadAllBytes(path);
        KvSettingsDocument document;
        try
        {
            document = FormatHandlers.Kv.Read(data);
            if (document.Root?["VideoConfig"] is null) throw new FormatException("no VideoConfig block");
        }
        catch (Exception e) when (e is FormatException or System.Text.DecoderFallbackException)
        { plan.Skip(SkipKind.UnsupportedFormat, $"video.txt unreadable: {e.Message}", dir); return; }

        var edit = new FileEdit { Path = path, Original = data, Updated = data, Format = "kv" };
        var (cpu, gpu, mem) = Profiles.Source(req.Step, hw);
        var wanted = new List<(string key, string val, string label, string cat)>();
        if (req.Resolution is var (w, h))
        {
            wanted.Add(("setting.defaultres", w.ToString(Inv), "Resolution width", Categories.Resolution));
            wanted.Add(("setting.defaultresheight", h.ToString(Inv), "Resolution height", Categories.Resolution));
        }
        (string fs, string nb)? dm = req.DisplayMode switch
        {
            DisplayModeChoice.Recommended or DisplayModeChoice.Fullscreen => ("1", "0"),
            DisplayModeChoice.Borderless => ("0", "1"),
            DisplayModeChoice.Windowed => ("0", "0"),
            _ => null,
        };
        if (dm is var (fsv, nbv))
        {
            wanted.Add(("setting.fullscreen", fsv, "Fullscreen", Categories.DisplayMode));
            wanted.Add(("setting.nowindowborder", nbv, "Borderless window", Categories.DisplayMode));
        }
        if (req.VSync != VSyncChoice.Keep) wanted.Add(("setting.mat_vsync", req.VSync == VSyncChoice.On ? "1" : "0", "VSync", Categories.VSync));
        wanted.Add(("setting.cpu_level", cpu.ToString(Inv), "Effect detail", Categories.Quality));
        wanted.Add(("setting.gpu_level", gpu.ToString(Inv), "Shader detail", Categories.Quality));
        wanted.Add(("setting.gpu_mem_level", mem.ToString(Inv), "Texture detail", Categories.Texture));
        if (req.FpsMode != FpsMode.Keep) plan.NotControlled.Add("Frame-rate cap: Source games keep it in fps_max (console or autoexec), not in video.txt; left alone.");

        foreach (var (k, v, label, cat) in wanted)
        {
            var locator = "kv|" + k;
            var old = document.GetRaw(locator);
            if (old is null || old == v || !Want(plan, req, "source:" + k, cat, label, old, v)) continue;
            if (!document.TrySet(locator, v)) continue;
            edit.Changes.Add(new(label, old, v) { Id = "source:" + k, Category = cat, Locator = locator, OldRaw = old, NewRaw = v });
        }
        edit.Updated = document.ToBytes();
        plan.Files.Add(edit);
    }

    // ------------------------------------------------------------------ rule files
    static void RuleFileEdit(GamePlan plan, GameInfo g, GameRule rule, RuleFile rf, OptimizeRequest req, IReadOnlyDictionary<string, object> vars)
    {
        var format = rf.Format.ToLowerInvariant();
        if (format == "registry") { RuleRegistry(plan, rf, req, vars); return; }
        var path = RuleSet.ExpandPath(rf.Path, g);
        var fileName = Path.GetFileName(path);
        var folder = Path.GetDirectoryName(path);
        if (!File.Exists(path))
        {
            plan.Skip(folder is not null && Directory.Exists(folder) ? SkipKind.LocationNotFound : SkipKind.NotCreatedYet,
                      $"Kept: {fileName} (not found; start the game once)", folder);
            return;
        }
        plan.SettingsFolder ??= folder;
        if (plan.Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        { plan.Warnings.Add($"Rule lists {fileName} twice; second entry ignored"); return; }
        var data = File.ReadAllBytes(path);
        var edit = new FileEdit { Path = path, Original = data, Updated = data, Format = format };
        if (rf.SteamCloud || HasSteamCloudMarker(path))
            plan.Warnings.Add($"{fileName} syncs through Steam Cloud. If Steam asks which copy to keep next launch, pick the one on this PC.");

        var values = ResolveValues(plan, rf, req, vars, fileName);
        try
        {
            if (FormatHandlers.Find(format) is not IRuleFormatHandler handler)
            {
                plan.Skip(SkipKind.UnsupportedFormat, $"Kept: {fileName} (unknown format '{rf.Format}' in rule)");
                return;
            }
            handler.EditRule(plan, req, edit, values);
        }
        catch (FormatException e)
        {
            plan.Skip(SkipKind.UnsupportedFormat, $"Kept: {fileName} ({e.Message})", folder);
            return;
        }
        plan.Files.Add(edit);
    }

    internal sealed record RuleValue(string Key, object Value, string Id, string Category);

    static List<RuleValue> ResolveValues(GamePlan plan, RuleFile rf, OptimizeRequest req, IReadOnlyDictionary<string, object> vars, string fileName)
    {
        var values = new List<RuleValue>();
        foreach (var (k, v) in rf.Set)
        {
            var val = RuleSet.Resolve(v, (int)req.Step, vars, out var err);
            var cat = rf.Categories?.GetValueOrDefault(k) ?? RuleSet.InferCategory(v);
            if (val is Keep keep) { plan.Skipped.Add($"Kept: {k} ({keep.Why})"); continue; }
            if (val is null) { plan.Warnings.Add($"Rule value for {k} skipped: {err}"); continue; }
            values.Add(new RuleValue(k, val, $"rule:{fileName}:{k}", cat));
        }
        return values;
    }

    static void RuleRegistry(GamePlan plan, RuleFile rf, OptimizeRequest req, IReadOnlyDictionary<string, object> vars)
    {
        if (!RuleSet.TryRegistryPath(rf.Path, out var sub)) { plan.Warnings.Add($"Registry rule path must start with HKCU: {rf.Path}"); return; }
        using var key = Registry.CurrentUser.OpenSubKey(sub);
        if (key is null) { plan.Skip(SkipKind.NotCreatedYet, $"Registry key HKCU\\{sub} does not exist yet; start the game once."); return; }
        plan.Examined = true;
        foreach (var v in ResolveValues(plan, rf, req, vars, "registry"))
        {
            var cur = key.GetValue(v.Key);
            if (cur is null) { plan.Skipped.Add($"Kept: {v.Key} (not in HKCU\\{sub})"); continue; }
            var kind = key.GetValueKind(v.Key);
            object? nv = (kind, v.Value) switch
            {
                (RegistryValueKind.DWord, int i) => i,
                (RegistryValueKind.DWord, bool b) => b ? 1 : 0,
                (RegistryValueKind.DWord, double d) => (int)Math.Round(d),
                (RegistryValueKind.QWord, int i) => (long)i,
                (RegistryValueKind.QWord, double d) => (long)Math.Round(d),
                (RegistryValueKind.String, string s) => s,
                _ => null,
            };
            if (nv is null) { plan.Skipped.Add($"Kept: {v.Key} ({kind} value does not take {Show(v.Value)})"); continue; }
            if (Equals(Convert.ToString(cur, Inv), Convert.ToString(nv, Inv))) continue;
            if (!Want(plan, req, v.Id, v.Category, v.Key, Convert.ToString(cur, Inv), Convert.ToString(nv, Inv))) continue;
            plan.Registry.Add(new RegistryEdit
            { SubKey = sub, ValueName = v.Key, Setting = v.Key, Kind = kind, OldValue = cur, NewValue = nv, Id = v.Id, Category = v.Category });
        }
    }

    static bool HasSteamCloudMarker(string path)
    {
        var dir = Path.GetDirectoryName(path);
        for (int i = 0; i < 3 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "steam_autocloud.vdf"))) return true;
        return false;
    }

    /// <summary>Typed value as JSON text, the raw form a key-level undo of a save file uses.</summary>
    public static string GvasRaw(object v) => GvasSettingsDocument.ToRaw(v);

    public static string Show(object? v) => v switch
    {
        null => "(not set)",
        bool b => b ? "on" : "off",
        double d => d.ToString("0.###", Inv),
        int[] a => string.Join("x", a),
        _ => Convert.ToString(v, Inv) ?? "",
    };
}
