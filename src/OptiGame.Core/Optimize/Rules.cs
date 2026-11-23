using System.Text.Json;
using System.Text.Json.Serialization;
using OptiGame.Core.Library;
using OptiGame.Core.Hardware;

namespace OptiGame.Core.Optimize;

/// <summary>
/// Game-specific knowledge as data. One JSON file per game in rules/ (shipped inside the program) or in
/// %LOCALAPPDATA%\GameOpt\rules (your own, loaded last so they win). See rules/README.md and
/// rules/rule.schema.json.
/// </summary>
public sealed class GameRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public RuleMatch Match { get; set; } = new();
    public string? Notes { get; set; }
    /// <summary>When true the engine handler (e.g. GameUserSettings.ini for Unreal) is not used for this game.</summary>
    public bool ReplaceEngineHandler { get; set; }
    /// <summary>Unreal only: config folder name under %LOCALAPPDATA% when it differs from the project folder.</summary>
    public string? UnrealConfigName { get; set; }
    /// <summary>The value this game writes for "no frame cap", if known. Without it, "uncapped" leaves the cap alone.</summary>
    public JsonElement? FpsUncappedValue { get; set; }
    public List<RuleFile> Files { get; set; } = new();

    // Optional metadata. Older rules without it are valid.
    public string? GameVersion { get; set; }
    /// <summary>Where the knowledge came from (a page, a test on a named version).</summary>
    [JsonPropertyName("source")] public string? Reference { get; set; }
    public string? Author { get; set; }
    /// <summary>Date (YYYY-MM-DD) the rule was last checked against the game.</summary>
    public string? Verified { get; set; }

    /// <summary>Where this rule was loaded from (built-in resource or a file path).</summary>
    [JsonIgnore] public string Origin { get; set; } = "";
    [JsonIgnore] public bool IsUserRule => !Origin.StartsWith("built-in", StringComparison.Ordinal);

    public bool Matches(GameInfo g) =>
        (g.SteamAppId is not null && Match.SteamAppIds.Contains(g.SteamAppId)) ||
        (g.Id.StartsWith("epic:") && Match.EpicAppNames.Contains(g.Id[5..], StringComparer.OrdinalIgnoreCase)) ||
        (g.Id.StartsWith("gog:") && Match.GogIds.Contains(g.Id[4..])) ||
        Match.InstallDirs.Contains(Path.GetFileName(g.InstallDir.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
}

public sealed class RuleMatch
{
    public List<string> SteamAppIds { get; set; } = new();
    public List<string> EpicAppNames { get; set; } = new();
    public List<string> GogIds { get; set; } = new();
    public List<string> InstallDirs { get; set; } = new();
}

public sealed class RuleFile
{
    /// <summary>gvas, ini, json, kv or registry.</summary>
    public string Format { get; set; } = "";
    /// <summary>File path (may use %ENVVARS%, %DOCUMENTS%, %SAVEDGAMES%, {install}); for registry, HKCU/Software/....</summary>
    public string Path { get; set; } = "";
    public bool SteamCloud { get; set; }
    /// <summary>
    /// Setting name -> value. ini keys are "Section/Key" (split at the last '/'), json keys are dotted paths.
    /// A value is a literal, a "$variable", or a list of exactly 6 slots. A null slot keeps the setting at that step.
    /// </summary>
    public Dictionary<string, JsonElement> Set { get; set; } = new();
    /// <summary>Optional category per key (resolution, displayMode, vsync, fpsCap, renderScale, texture, quality) for "keep my settings".</summary>
    public Dictionary<string, string>? Categories { get; set; }
    /// <summary>Optional human labels per key, shown in previews.</summary>
    public Dictionary<string, string>? Labels { get; set; }
}

/// <summary>A rule value the user's preferences say to leave alone (e.g. "$fpsCap" when the cap is "keep").</summary>
public sealed record Keep(string Why);

public static class RuleSet
{
    internal static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string UserRulesDir => System.IO.Path.Combine(AppPaths.DataDir, "rules");

    public static List<GameRule> Load(List<string>? errors = null)
    {
        var rules = new Dictionary<string, GameRule>(StringComparer.OrdinalIgnoreCase);
        var asm = typeof(RuleSet).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".json") && n.Contains(".rules.")).Order())
        {
            using var s = asm.GetManifestResourceStream(res)!;
            Add(new StreamReader(s).ReadToEnd(), "built-in " + res);
        }
        if (Directory.Exists(UserRulesDir))
            foreach (var f in Directory.EnumerateFiles(UserRulesDir, "*.json").Order())
            {
                try { Add(File.ReadAllText(f), f); }
                catch (IOException e) { errors?.Add($"Rule file {f} unreadable: {e.Message}"); }
            }
        return rules.Values.ToList();

        void Add(string json, string origin)
        {
            var r = TryParse(json, out var problems);
            var fatal = problems.Where(p => p.Severity == RuleProblemSeverity.Error).ToList();
            if (r is null || fatal.Count > 0)
            {
                errors?.Add($"Rule {origin} ignored: {string.Join("; ", fatal.Select(p => p.ToString()).DefaultIfEmpty("unreadable"))}");
                return;
            }
            r.Origin = origin;
            rules[r.Id] = r;
        }
    }

    public static GameRule? TryParse(string json, out List<RuleProblem> problems)
    {
        problems = new();
        GameRule? r;
        try { r = JsonSerializer.Deserialize<GameRule>(json, Opts); }
        catch (JsonException e) { problems.Add(RuleProblem.Error("", $"not valid JSON: {e.Message}")); return null; }
        if (r is null) { problems.Add(RuleProblem.Error("", "empty file")); return null; }
        problems.AddRange(RuleValidator.Validate(r, json));
        return r;
    }

    public static string ExpandPath(string path, GameInfo g)
    {
        var p = path.Replace("{install}", g.InstallDir, StringComparison.OrdinalIgnoreCase)
            .Replace("%DOCUMENTS%", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.OrdinalIgnoreCase)
            .Replace("%SAVEDGAMES%", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"), StringComparison.OrdinalIgnoreCase);
        return Environment.ExpandEnvironmentVariables(p);
    }

    /// <summary>"HKCU/Software/Company/Game" (or with backslashes) -> "Software\Company\Game".</summary>
    public static bool TryRegistryPath(string path, out string subKey)
    {
        var p = path.Replace('/', '\\').Trim('\\');
        foreach (var prefix in new[] { "HKCU\\", "HKEY_CURRENT_USER\\" })
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { subKey = p[prefix.Length..]; return subKey.Length > 0; }
        subKey = "";
        return false;
    }

    /// <summary>Category for "keep my settings", inferred from the variable a value uses.</summary>
    public static string InferCategory(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 6) return Categories.Quality;
        if (v.ValueKind != JsonValueKind.String || v.GetString() is not { } s || !s.StartsWith('$')) return Categories.Other;
        return s[1..].ToLowerInvariant() switch
        {
            "fpscap" => Categories.FpsCap,
            "nativeresolution" or "resolution" or "nativewidth" or "nativeheight" or "width" or "height" => Categories.Resolution,
            "vsync" => Categories.VSync,
            "displaymode" or "uewindowmode" => Categories.DisplayMode,
            "ue.texture" or "texturecap" => Categories.Texture,
            var x when x.StartsWith("ue.") || x == "step" => Categories.Quality,
            _ => Categories.Other,
        };
    }

    /// <summary>
    /// Turn a rule value into int, double, bool, string, int[] or Keep; null when it cannot be resolved.
    /// </summary>
    public static object? Resolve(JsonElement v, int step, IReadOnlyDictionary<string, object> vars, out string? error)
    {
        error = null;
        if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == QualitySteps.Count)
        {
            v = v[step];
            if (v.ValueKind == JsonValueKind.Null) return new Keep("no observed value for this step");
        }
        switch (v.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number:
                if (v.TryGetInt32(out var i)) return i;
                return v.GetDouble();
            case JsonValueKind.String:
                var s = v.GetString()!;
                if (!s.StartsWith('$')) return s;
                if (vars.TryGetValue(s[1..], out var val)) return val;
                error = $"unknown variable {s}";
                return null;
            case JsonValueKind.Array when v.GetArrayLength() == 2 && v.EnumerateArray().All(e => e.TryGetInt32(out _)):
                return v.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            default:
                error = $"unsupported value {v.GetRawText()}";
                return null;
        }
    }
}
