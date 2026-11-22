using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptiGame.Core.Optimize;

public enum ImportMode { Merge, Replace }
public enum ConflictPolicy { KeepMine, TakeImported }

/// <summary>What an import would do. Nothing is written until Apply.</summary>
public sealed class ImportPlan
{
    public List<string> Additions { get; } = new();
    public List<string> Replacements { get; } = new();
    public List<string> Conflicts { get; } = new();
    public List<string> NeedsRemap { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<string> Errors { get; } = new();
    public Prefs Result { get; set; } = new();
    public Dictionary<string, string> RulesToWrite { get; } = new();
    public bool CanApply => Errors.Count == 0 && NeedsRemap.Count == 0;
}

/// <summary>
/// Versioned export of preferences: profiles, the All games setting, per-game choices, saved exclusions
/// and your own rules. Machine-specific values (folders, display device names) are kept in a separate
/// "machine" section so they can be remapped or dropped on another PC. Game saves, backups, raw
/// registry data and benchmark captures are never included.
/// </summary>
public static class PrefsTransfer
{
    public const string FormatName = "gameopt-preferences";
    public const int FormatVersion = 1;

    static readonly JsonSerializerOptions Json = Prefs.Json;

    public static string Export(Prefs p, bool includeRules = true)
    {
        var portableGames = new JsonObject();
        var machineGames = new JsonObject();
        var gameDisplays = new JsonObject();
        foreach (var (id, g) in p.Games.Where(kv => !kv.Value.IsEmpty))
        {
            var node = JsonSerializer.SerializeToNode(g, Json)!.AsObject();
            if (node["Display"] is { } d) { gameDisplays[id] = d.GetValue<string>(); node.Remove("Display"); }
            (id.StartsWith("manual:") ? machineGames : portableGames)[id] = node;
        }
        var profiles = new JsonArray();
        var profileDisplays = new JsonObject();
        foreach (var pr in p.Profiles)
        {
            var node = JsonSerializer.SerializeToNode(pr, Json)!.AsObject();
            if (node["Display"] is { } d) { profileDisplays[pr.Id] = d.GetValue<string>(); node.Remove("Display"); }
            profiles.Add(node);
        }
        var rules = new JsonArray();
        if (includeRules && Directory.Exists(RuleSet.UserRulesDir))
            foreach (var f in Directory.EnumerateFiles(RuleSet.UserRulesDir, "*.json").Order())
                rules.Add(new JsonObject { ["file"] = Path.GetFileName(f), ["json"] = File.ReadAllText(f) });

        var root = new JsonObject
        {
            ["format"] = FormatName,
            ["version"] = FormatVersion,
            ["exported"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["portable"] = new JsonObject
            {
                ["globalOffset"] = p.GlobalOffset,
                ["defaultProfileId"] = p.DefaultProfileId,
                ["profiles"] = profiles,
                ["games"] = portableGames,
                ["rules"] = rules,
            },
            ["machine"] = new JsonObject
            {
                ["gamingDisplay"] = p.GamingDisplay,
                ["profileDisplays"] = profileDisplays,
                ["gameDisplays"] = gameDisplays,
                ["manualGames"] = JsonSerializer.SerializeToNode(p.ManualGames, Json),
                ["manualGameSettings"] = machineGames,
                ["sharedCacheDirs"] = JsonSerializer.SerializeToNode(p.SharedCacheDirs, Json),
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Works out an import. maps renames folders ("old path" -> "new path") for manually added games
    /// whose folder does not exist here; any such folder without a mapping stops the import.
    /// existingRules: id -> json of rules already in the user rules folder.
    /// </summary>
    public static ImportPlan Analyze(string json, Prefs current, ImportMode mode, ConflictPolicy policy,
        IReadOnlyDictionary<string, string>? maps = null, IReadOnlyDictionary<string, string>? existingRules = null,
        Func<string, bool>? folderExists = null, Func<string, bool>? displayExists = null)
    {
        folderExists ??= Directory.Exists;
        displayExists ??= _ => true;
        maps ??= new Dictionary<string, string>();
        existingRules ??= ReadUserRules();
        var plan = new ImportPlan();
        JsonObject root;
        try { root = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("empty file"); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { plan.Errors.Add("Not a preferences file: " + e.Message); return plan; }
        if (root["format"]?.GetValue<string>() != FormatName) { plan.Errors.Add($"Not a GameOptimizer preferences file (format is not '{FormatName}')."); return plan; }
        var ver = root["version"]?.GetValue<int>() ?? 0;
        if (ver < 1 || ver > FormatVersion) { plan.Errors.Add($"Preferences file version {ver} is not supported (this version reads 1 to {FormatVersion})."); return plan; }

        // Start from a copy of the current preferences (merge) or a fresh one keeping machine settings (replace).
        var result = Prefs.Parse(current.ToJson(), out _);
        if (mode == ImportMode.Replace)
        {
            if (result.Profiles.Count > 0) plan.Replacements.Add($"All {result.Profiles.Count} of your profiles are replaced");
            if (result.Games.Count > 0) plan.Replacements.Add($"Choices for {result.Games.Count} game(s) are replaced");
            result.Profiles.Clear();
            result.Games.Clear();
        }
        var portable = root["portable"]?.AsObject() ?? new JsonObject();
        var machine = root["machine"]?.AsObject() ?? new JsonObject();

        // Global settings
        if (portable["globalOffset"] is { } go && Math.Clamp(go.GetValue<int>(), -Prefs.MaxOffset, Prefs.MaxOffset) != result.GlobalOffset)
        {
            int off = Math.Clamp(go.GetValue<int>(), -Prefs.MaxOffset, Prefs.MaxOffset);
            Take("All games slider", true, () => result.GlobalOffset = off,
                 $"{Prefs.OffsetLabel(result.GlobalOffset)} -> {Prefs.OffsetLabel(off)}", added: false);
        }

        // Profiles
        var profileDisplays = machine["profileDisplays"]?.AsObject();
        foreach (var node in portable["profiles"]?.AsArray() ?? new JsonArray())
        {
            Profile? pr;
            try { pr = node.Deserialize<Profile>(Json); }
            catch (JsonException e) { plan.Errors.Add("A profile could not be read: " + e.Message); continue; }
            if (pr is null || string.IsNullOrWhiteSpace(pr.Id)) { plan.Errors.Add("A profile has no id."); continue; }
            if (Prefs.BuiltInProfiles.Any(b => b.Id == pr.Id)) { plan.Skipped.Add($"Profile '{pr.Id}' has a built-in id; skipped."); continue; }
            if (profileDisplays?[pr.Id]?.GetValue<string>() is { } disp)
            {
                if (displayExists(disp)) pr.Display = disp;
                else plan.Skipped.Add($"Profile {pr.Name}: display {disp} is not connected here; it will use the gaming display.");
            }
            var mine = result.Profiles.FirstOrDefault(x => x.Id == pr.Id);
            if (mine is null) { result.Profiles.Add(pr); plan.Additions.Add($"Profile {pr.Name}"); continue; }
            bool same = JsonSerializer.Serialize(mine, Json) == JsonSerializer.Serialize(pr, Json);
            if (same) continue;
            Take($"Profile {pr.Name}", true, () => { result.Profiles.Remove(mine); result.Profiles.Add(pr); }, "differs from yours", added: false);
        }
        if (portable["defaultProfileId"]?.GetValue<string>() is { } def && def != result.DefaultProfileId)
        {
            if (result.FindProfile(def) is null) plan.Skipped.Add($"Default profile '{def}' is not in the file; kept {result.DefaultProfile.Name}.");
            else Take("Default profile", mode == ImportMode.Merge, () => result.DefaultProfileId = def, $"{result.DefaultProfile.Name} -> {result.FindProfile(def)!.Name}", added: false);
        }

        // Per-game choices (portable ids such as steam:123)
        var gameDisplays = machine["gameDisplays"]?.AsObject();
        void ImportGames(JsonObject? games, Func<string, string?> mapId)
        {
            foreach (var (id, node) in games ?? new JsonObject())
            {
                var target = mapId(id);
                if (target is null) continue;
                GamePrefs? g;
                try { g = node.Deserialize<GamePrefs>(Json); }
                catch (JsonException e) { plan.Errors.Add($"Choices for {id} could not be read: {e.Message}"); continue; }
                if (g is null) continue;
                if (g.ProfileId is { } pid && result.FindProfile(pid) is null) { plan.Skipped.Add($"{id}: profile '{pid}' does not exist; the game uses the default profile."); g.ProfileId = null; }
                if (gameDisplays?[id]?.GetValue<string>() is { } disp)
                {
                    if (displayExists(disp)) g.Display = disp;
                    else plan.Skipped.Add($"{id}: display {disp} is not connected here; dropped.");
                }
                var mine = result.GameOrNull(target);
                if (mine is null || mine.IsEmpty) { result.Games[target] = g; plan.Additions.Add($"Choices for {target}"); continue; }
                if (JsonSerializer.Serialize(mine, Json) == JsonSerializer.Serialize(g, Json)) continue;
                Take($"Choices for {target}", true, () => result.Games[target] = g, "differ from yours", added: false);
            }
        }
        ImportGames(portable["games"]?.AsObject(), id => id);

        // Machine-specific: manually added folders need to exist here, or be remapped.
        var pathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in machine["manualGames"]?.AsArray() ?? new JsonArray())
        {
            var m = node.Deserialize<ManualGame>(Json);
            if (m is null || string.IsNullOrWhiteSpace(m.Path)) continue;
            var path = maps.TryGetValue(m.Path, out var mapped) ? mapped : m.Path;
            if (!folderExists(path)) { plan.NeedsRemap.Add(m.Path); continue; }
            pathMap[Library.LibraryScanner.ManualId(m.Path)] = Library.LibraryScanner.ManualId(path);
            if (result.ManualGames.Any(x => Library.LibraryScanner.NormalizeDir(x.Path) == Library.LibraryScanner.NormalizeDir(path))) continue;
            result.ManualGames.Add(new ManualGame { Path = path, Name = m.Name });
            plan.Additions.Add($"Game folder {path}" + (path != m.Path ? $" (was {m.Path})" : ""));
        }
        ImportGames(machine["manualGameSettings"]?.AsObject(), id => pathMap.GetValueOrDefault(id));
        foreach (var node in machine["sharedCacheDirs"]?.AsArray() ?? new JsonArray())
        {
            var d = node?.GetValue<string>();
            if (d is null || result.SharedCacheDirs.Contains(d, StringComparer.OrdinalIgnoreCase)) continue;
            if (folderExists(d)) { result.SharedCacheDirs.Add(d); plan.Additions.Add($"Shared cache folder {d}"); }
            else plan.Skipped.Add($"Shared cache folder {d} does not exist here; skipped.");
        }
        if (machine["gamingDisplay"]?.GetValue<string>() is { } gd && gd != result.GamingDisplay)
        {
            if (displayExists(gd)) Take("Gaming display", mode == ImportMode.Merge && result.GamingDisplay is not null, () => result.GamingDisplay = gd, gd, added: result.GamingDisplay is null);
            else plan.Skipped.Add($"Gaming display {gd} is not connected here; kept yours.");
        }

        // Your own rules
        foreach (var node in portable["rules"]?.AsArray() ?? new JsonArray())
        {
            var text = node?["json"]?.GetValue<string>();
            if (text is null) continue;
            var rule = RuleSet.TryParse(text, out var problems);
            var errs = problems.Where(p => p.Severity == RuleProblemSeverity.Error).ToList();
            if (rule is null || errs.Count > 0) { plan.Errors.Add($"Rule {node?["file"]}: {string.Join("; ", errs.Select(e => e.ToString()).DefaultIfEmpty("unreadable"))}"); continue; }
            if (!existingRules.TryGetValue(rule.Id, out var mineJson)) { plan.RulesToWrite[rule.Id] = text; plan.Additions.Add($"Rule {rule.Id}"); continue; }
            if (mineJson.Trim() == text.Trim()) continue;
            Take($"Rule {rule.Id}", true, () => plan.RulesToWrite[rule.Id] = text, "differs from yours", added: false);
        }

        plan.Result = result;
        return plan;

        // In Replace mode imported values win; in Merge mode a differing item is a conflict, settled by policy.
        void Take(string what, bool isConflict, Action apply, string detail, bool added)
        {
            if (mode == ImportMode.Replace || !isConflict)
            {
                apply();
                (added ? plan.Additions : plan.Replacements).Add($"{what}: {detail}");
                return;
            }
            plan.Conflicts.Add($"{what}: {detail} ({(policy == ConflictPolicy.TakeImported ? "imported one used" : "yours kept")})");
            if (policy == ConflictPolicy.TakeImported) apply();
        }
    }

    public static Dictionary<string, string> ReadUserRules()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(RuleSet.UserRulesDir)) return d;
        foreach (var f in Directory.EnumerateFiles(RuleSet.UserRulesDir, "*.json"))
        {
            try
            {
                var text = File.ReadAllText(f);
                if (RuleSet.TryParse(text, out _) is { } r) d[r.Id] = text;
            }
            catch (IOException) { }
        }
        return d;
    }

    /// <summary>Saves the analyzed result. The previous preferences file is kept as prefs.json.before-import.</summary>
    public static void Apply(ImportPlan plan)
    {
        if (!plan.CanApply) throw new InvalidOperationException("This import has errors or folders that need a new location.");
        if (File.Exists(AppPaths.PrefsFile)) File.Copy(AppPaths.PrefsFile, AppPaths.PrefsFile + ".before-import", overwrite: true);
        plan.Result.Save();
        if (plan.RulesToWrite.Count > 0)
        {
            Directory.CreateDirectory(RuleSet.UserRulesDir);
            var existing = Directory.EnumerateFiles(RuleSet.UserRulesDir, "*.json")
                .Select(f => (f, r: RuleSet.TryParse(File.ReadAllText(f), out _))).Where(x => x.r is not null).ToList();
            foreach (var (id, json) in plan.RulesToWrite)
            {
                var path = existing.FirstOrDefault(x => x.r!.Id == id).f ?? Path.Combine(RuleSet.UserRulesDir, id + ".json");
                File.WriteAllText(path, json);
            }
        }
    }
}
