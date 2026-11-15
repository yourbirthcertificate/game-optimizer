using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameOptimizer.Core.Formats;
using GameOptimizer.Core.Hardware;
using GameOptimizer.Core.Library;
using Microsoft.Win32;

namespace GameOptimizer.Core.Optimize;

/// <summary>
/// Builds a GamePlan: reads the game's settings, works out new values for the chosen step and keeps
/// the full new file contents in memory. Never writes anything. Anything it cannot read with
/// confidence goes to plan.Skipped with a reason instead of being guessed.
/// </summary>
public static partial class Planner
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static GamePlan Plan(GameInfo g, HardwareInfo hw, QualityStep step, IReadOnlyList<GameRule> rules)
    {
        var plan = new GamePlan { Game = g, Step = step };
        var rule = rules.FirstOrDefault(r => r.Matches(g));
        plan.RuleId = rule?.Id;
        var vars = Profiles.Variables(step, hw);

        if (rule is null || !rule.ReplaceEngineHandler)
        {
            switch (g.Engine)
            {
                case GameEngine.Unreal: Unreal(plan, hw, step, rule); break;
                case GameEngine.Unity: Unity(plan, hw, step); break;
                case GameEngine.Source: Source(plan, hw, step); break;
                case GameEngine.Source2:
                    if (rule is null) plan.Skipped.Add("Source 2 game: its settings format is not supported yet");
                    break;
                default:
                    if (rule is null) plan.Skipped.Add(g.Engine == GameEngine.Unknown
                        ? "Engine not recognized and no rule for this game"
                        : $"{g.Engine} game with no rule for it yet");
                    break;
            }
        }
        if (rule is not null)
            foreach (var f in rule.Files) RuleFileEdit(plan, g, f, step, vars);

        bool examined = plan.Files.Count > 0 || plan.Examined;
        plan.Files.RemoveAll(f => f.Changes.Count == 0);
        plan.Status = plan.HasChanges ? PlanStatus.Ready : examined ? PlanStatus.NothingToChange : PlanStatus.Skipped;
        return plan;
    }

    // ------------------------------------------------------------------ Unreal
    static readonly string[] UnrealPlatforms = { "Windows", "WindowsNoEditor", "WinGDK", "WindowsClient" };

    public static string? FindUnrealSettings(GameInfo g, GameRule? rule)
    {
        var names = new[] { rule?.UnrealConfigName, g.EngineDetail }.Where(n => !string.IsNullOrEmpty(n)).Distinct();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games"),
        };
        return (from n in names
                from r in roots
                from p in UnrealPlatforms
                let f = Path.Combine(r, n!, "Saved", "Config", p, "GameUserSettings.ini")
                where File.Exists(f)
                orderby File.GetLastWriteTimeUtc(f) descending
                select f).FirstOrDefault();
    }

    static void Unreal(GamePlan plan, HardwareInfo hw, QualityStep step, GameRule? rule)
    {
        var path = FindUnrealSettings(plan.Game, rule);
        if (path is null)
        {
            plan.Skipped.Add("No GameUserSettings.ini yet. Start the game once so it creates its settings.");
            return;
        }
        byte[] data = File.ReadAllBytes(path);
        IniDocument ini;
        try { ini = IniDocument.Parse(data); }
        catch (FormatException e) { plan.Skipped.Add($"GameUserSettings.ini unreadable: {e.Message}"); return; }

        var edit = new FileEdit { Path = path, Original = data, Updated = data, Format = "ini" };
        var gus = ini.Sections().FirstOrDefault(s => ini.Get(s, "ResolutionSizeX") is not null);
        if (gus is null)
            plan.Skipped.Add("Kept: resolution and display mode (no ResolutionSizeX in GameUserSettings.ini)");
        else
        {
            void SetExisting(string key, string value, string label)
            {
                var old = ini.Get(gus, key);
                if (old is null || old == value) return;
                if (ini.Set(gus, key, value, addIfMissing: false, out _)) edit.Changes.Add(new(label, old, value));
            }
            string w = hw.NativeWidth.ToString(Inv), h = hw.NativeHeight.ToString(Inv);
            SetExisting("ResolutionSizeX", w, "Resolution width");
            SetExisting("ResolutionSizeY", h, "Resolution height");
            SetExisting("LastUserConfirmedResolutionSizeX", w, "Confirmed resolution width");
            SetExisting("LastUserConfirmedResolutionSizeY", h, "Confirmed resolution height");
            foreach (var k in new[] { "FullscreenMode", "LastConfirmedFullscreenMode", "PreferredFullscreenMode" })
                SetExisting(k, "0", k + " (0 = fullscreen)");
            SetExisting("bUseVSync", "False", "VSync");
            var fr = ini.Get(gus, "FrameRateLimit");
            if (fr is not null && double.TryParse(fr, NumberStyles.Float, Inv, out var cur) && Math.Abs(cur - hw.FpsCap) > 0.01)
                SetExisting("FrameRateLimit", hw.FpsCap.ToString("0.000000", Inv), "Frame rate limit");
        }

        const string sg = "ScalabilityGroups";
        foreach (var (group, level) in Profiles.Unreal(step, hw))
        {
            var key = $"sg.{group}Quality";
            var old = ini.Get(sg, key);
            var val = level.ToString(Inv);
            if (old == val) continue;
            if (ini.Set(sg, key, val, addIfMissing: true, out var err)) edit.Changes.Add(new(key, old, val));
            else plan.Skipped.Add($"Kept: {err}");
        }
        var rq = ini.Get(sg, "sg.ResolutionQuality");
        if (rq is null || !double.TryParse(rq, NumberStyles.Float, Inv, out var rqv) || Math.Abs(rqv - 100) > 0.01)
        {
            var val = rq?.Contains('.') == true ? "100.000000" : "100";
            if (ini.Set(sg, "sg.ResolutionQuality", val, addIfMissing: true, out _)) edit.Changes.Add(new("Render resolution %", rq, val));
        }
        edit.Updated = ini.ToBytes();
        plan.Files.Add(edit);
    }

    // ------------------------------------------------------------------ Unity
    static void Unity(GamePlan plan, HardwareInfo hw, QualityStep step)
    {
        var g = plan.Game;
        if (string.IsNullOrEmpty(g.EngineDetail)) { plan.Skipped.Add("Unity game without app.info: cannot find its settings"); return; }
        var sub = $@"Software\{g.EngineDetail}";
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sub);
        if (key is null) { plan.Skipped.Add("No Unity settings in the registry: never started, or the game keeps its own settings file"); return; }
        plan.Examined = true;
        var names = key.GetValueNames().ToHashSet();

        void SetDword(string pref, int value, string label)
        {
            var name = UnityQuality.PrefName(pref);
            if (!names.Contains(name)) return;
            if (key.GetValueKind(name) != RegistryValueKind.DWord) { plan.Skipped.Add($"Kept: {label} (unexpected value type)"); return; }
            var old = (int)key.GetValue(name)!;
            if (old == value) return;
            plan.Registry.Add(new RegistryEdit { SubKey = sub, ValueName = name, Setting = label, Kind = RegistryValueKind.DWord, OldValue = old, NewValue = value });
        }

        if (!names.Contains(UnityQuality.PrefName("Screenmanager Resolution Width")))
            plan.Skipped.Add("Kept: resolution (the game does not use Unity's saved screen settings)");
        SetDword("Screenmanager Resolution Width", hw.NativeWidth, "Resolution width");
        SetDword("Screenmanager Resolution Height", hw.NativeHeight, "Resolution height");
        SetDword("Screenmanager Resolution Use Native", 1, "Use native resolution");
        SetDword("Screenmanager Fullscreen mode", 1, "Display mode (1 = borderless fullscreen)");

        var q = UnityQuality.PrefName("UnityGraphicsQuality");
        if (!names.Contains(q))
        {
            plan.Skipped.Add("Kept: quality (the game uses its own settings menu)");
            return;
        }
        var dataDir = Directory.EnumerateDirectories(g.InstallDir, "*_Data").FirstOrDefault(d => File.Exists(Path.Combine(d, "app.info")));
        var levels = dataDir is null ? null : UnityQuality.Read(dataDir, out var why);
        if (levels is null) { plan.Skipped.Add("Kept: quality (could not read the game's quality level list)"); return; }
        int idx = step.MapToLevels(levels.Names.Count);
        var before = key.GetValue(q) as int?;
        if (before != idx)
            plan.Registry.Add(new RegistryEdit
            {
                SubKey = sub, ValueName = q, Kind = RegistryValueKind.DWord, OldValue = before, NewValue = idx,
                Setting = $"Quality level ({levels.Names[idx]}, {idx + 1} of {levels.Names.Count})",
            });
    }

    // ------------------------------------------------------------------ Source 1
    static void Source(GamePlan plan, HardwareInfo hw, QualityStep step)
    {
        var path = Path.Combine(plan.Game.InstallDir, plan.Game.EngineDetail ?? "", "cfg", "video.txt");
        if (!File.Exists(path)) { plan.Skipped.Add("No cfg\\video.txt yet. Start the game once so it creates its settings."); return; }
        var data = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new System.Text.UTF8Encoding(false, true).GetString(data);
            if (KvNode.Parse(text)["VideoConfig"] is null) throw new FormatException("no VideoConfig block");
        }
        catch (Exception e) when (e is FormatException or System.Text.DecoderFallbackException)
        { plan.Skipped.Add($"video.txt unreadable: {e.Message}"); return; }

        var edit = new FileEdit { Path = path, Original = data, Updated = data, Format = "kv" };
        var (cpu, gpu, mem) = Profiles.Source(step, hw);
        var wanted = new (string key, string val, string label)[]
        {
            ("setting.defaultres", hw.NativeWidth.ToString(Inv), "Resolution width"),
            ("setting.defaultresheight", hw.NativeHeight.ToString(Inv), "Resolution height"),
            ("setting.fullscreen", "1", "Fullscreen"),
            ("setting.nowindowborder", "0", "Borderless window"),
            ("setting.mat_vsync", "0", "VSync"),
            ("setting.cpu_level", cpu.ToString(Inv), "Effect detail"),
            ("setting.gpu_level", gpu.ToString(Inv), "Shader detail"),
            ("setting.gpu_mem_level", mem.ToString(Inv), "Texture detail"),
        };
        foreach (var (k, v, label) in wanted)
            if (KvText.TrySet(ref text, k, v, out var old) && old != v) edit.Changes.Add(new(label, old, v));
        edit.Updated = new System.Text.UTF8Encoding(false).GetBytes(text);
        plan.Files.Add(edit);
    }

    // ------------------------------------------------------------------ rule files
    static void RuleFileEdit(GamePlan plan, GameInfo g, RuleFile rf, QualityStep step, IReadOnlyDictionary<string, object> vars)
    {
        var path = RuleSet.ExpandPath(rf.Path, g);
        var fileName = Path.GetFileName(path);
        if (!File.Exists(path)) { plan.Skipped.Add($"Kept: {fileName} (not found; start the game once)"); return; }
        if (plan.Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        { plan.Warnings.Add($"Rule lists {fileName} twice; second entry ignored"); return; }
        var data = File.ReadAllBytes(path);
        var edit = new FileEdit { Path = path, Original = data, Updated = data, Format = rf.Format.ToLowerInvariant() };
        if (rf.SteamCloud || HasSteamCloudMarker(path))
            plan.Warnings.Add($"{fileName} syncs through Steam Cloud. If Steam asks which copy to keep next launch, pick the one on this PC.");

        var values = new List<(string key, object val)>();
        foreach (var (k, v) in rf.Set)
        {
            var val = RuleSet.Resolve(v, (int)step, vars, out var err);
            if (val is null) plan.Warnings.Add($"Rule value for {k} skipped: {err}");
            else values.Add((k, val));
        }

        try
        {
            switch (edit.Format)
            {
                case "gvas": EditGvas(plan, edit, values); break;
                case "ini": EditIni(plan, edit, values); break;
                case "json": EditJson(plan, edit, values); break;
                case "kv": EditKv(plan, edit, values); break;
                default: plan.Skipped.Add($"Kept: {fileName} (unknown format '{rf.Format}' in rule)"); return;
            }
        }
        catch (FormatException e)
        {
            plan.Skipped.Add($"Kept: {fileName} ({e.Message})");
            return;
        }
        plan.Files.Add(edit);
    }

    static bool HasSteamCloudMarker(string path)
    {
        var dir = Path.GetDirectoryName(path);
        for (int i = 0; i < 3 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "steam_autocloud.vdf"))) return true;
        return false;
    }

    static void EditGvas(GamePlan plan, FileEdit edit, List<(string key, object val)> values)
    {
        var sav = GvasFile.Load(edit.Original);
        foreach (var (k, v) in values)
        {
            var p = sav.Find(k);
            if (p is null) { plan.Skipped.Add($"Kept: {k} (not in {Path.GetFileName(edit.Path)})"); continue; }
            var old = p.Get();
            if (old is null) { plan.Skipped.Add($"Kept: {k} ({p.Type} values are not editable)"); continue; }
            var target = v is int i && old is double ? (double)i : v;
            if (Show(old) == Show(target)) continue;
            if (!p.TrySet(target, out var err)) { plan.Skipped.Add($"Kept: {err}"); continue; }
            edit.Changes.Add(new(k, Show(old), Show(target)));
        }
        var bytes = sav.Save();
        GvasFile.Load(bytes); // the new file must parse and round-trip too
        edit.Updated = bytes;
    }

    static void EditIni(GamePlan plan, FileEdit edit, List<(string key, object val)> values)
    {
        var ini = IniDocument.Parse(edit.Original);
        foreach (var (k, v) in values)
        {
            int slash = k.LastIndexOf('/');
            if (slash <= 0) { plan.Warnings.Add($"Rule ini key '{k}' needs the form Section/Key"); continue; }
            string section = k[..slash], key = k[(slash + 1)..];
            var old = ini.Get(section, key);
            if (old is null) { plan.Skipped.Add($"Kept: {key} (not in {Path.GetFileName(edit.Path)})"); continue; }
            var val = IniValue(v, old);
            if (old == val) continue;
            if (ini.Set(section, key, val, addIfMissing: false, out var err)) edit.Changes.Add(new(key, old, val));
            else plan.Skipped.Add($"Kept: {err}");
        }
        edit.Updated = ini.ToBytes();
    }

    /// <summary>Formats a value the way the existing ini value is written (True/true/1, 60 / 60.000000).</summary>
    static string IniValue(object v, string old) => v switch
    {
        bool b when old is "True" or "False" => b ? "True" : "False",
        bool b when old is "true" or "false" => b ? "true" : "false",
        bool b => b ? "1" : "0",
        int i when old.Contains('.') => i.ToString("0.000000", Inv),
        double d => d.ToString(old.Contains('.') ? "0.000000" : "0.######", Inv),
        int[] a => string.Join(old.Contains('x') ? "x" : ",", a),
        _ => Convert.ToString(v, Inv) ?? "",
    };

    static void EditJson(GamePlan plan, FileEdit edit, List<(string key, object val)> values)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(edit.Original, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { throw new FormatException("not valid JSON: " + e.Message); }
        if (root is null) throw new FormatException("empty JSON");
        foreach (var (k, v) in values)
        {
            var parts = k.Split('.');
            JsonNode? parent = root;
            foreach (var p in parts[..^1]) parent = parent?[p];
            if (parent is not JsonObject obj || !obj.TryGetPropertyValue(parts[^1], out var cur) || cur is not JsonValue jv)
            { plan.Skipped.Add($"Kept: {k} (not in {Path.GetFileName(edit.Path)})"); continue; }
            var kind = jv.GetValueKind();
            JsonNode? nv = (v, kind) switch
            {
                (bool b, JsonValueKind.True or JsonValueKind.False) => JsonValue.Create(b),
                (int i, JsonValueKind.Number) => JsonValue.Create(i),
                (double d, JsonValueKind.Number) => JsonValue.Create(d),
                (string s, JsonValueKind.String) => JsonValue.Create(s),
                _ => null,
            };
            if (nv is null) { plan.Skipped.Add($"Kept: {k} (rule value does not match its type)"); continue; }
            var oldText = jv.ToJsonString();
            if (oldText == nv.ToJsonString()) continue;
            obj[parts[^1]] = nv;
            edit.Changes.Add(new(k, oldText, nv.ToJsonString()));
        }
        bool indented = edit.Original.Contains((byte)'\n');
        edit.Updated = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = indented }));
    }

    static void EditKv(GamePlan plan, FileEdit edit, List<(string key, object val)> values)
    {
        var text = new System.Text.UTF8Encoding(false, true).GetString(edit.Original);
        KvNode.Parse(text);
        foreach (var (k, v) in values)
        {
            var val = v is bool b ? (b ? "1" : "0") : Convert.ToString(v, Inv)!;
            if (!KvText.TrySet(ref text, k, val, out var old)) { plan.Skipped.Add($"Kept: {k} (not in {Path.GetFileName(edit.Path)})"); continue; }
            if (old != val) edit.Changes.Add(new(k, old, val));
        }
        edit.Updated = new System.Text.UTF8Encoding(false).GetBytes(text);
    }

    public static string Show(object? v) => v switch
    {
        null => "(not set)",
        bool b => b ? "on" : "off",
        double d => d.ToString("0.###", Inv),
        int[] a => string.Join("x", a),
        _ => Convert.ToString(v, Inv) ?? "",
    };
}

/// <summary>Line-level edits of flat "key" "value" pairs in Valve text files.</summary>
public static partial class KvText
{
    public static bool TrySet(ref string text, string key, string value, out string? old)
    {
        var rx = new Regex("^(\\s*\"" + Regex.Escape(key) + "\"\\s+\")([^\"]*)(\")", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var ms = rx.Matches(text);
        old = null;
        if (ms.Count != 1) return false;
        old = ms[0].Groups[2].Value;
        text = rx.Replace(text, m => m.Groups[1].Value + value + m.Groups[3].Value);
        return true;
    }
}
