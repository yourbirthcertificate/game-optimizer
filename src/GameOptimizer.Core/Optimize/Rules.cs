using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameOptimizer.Core.Library;

namespace GameOptimizer.Core.Optimize;

/// <summary>
/// Game-specific knowledge as data. One JSON file per game in rules/ (shipped inside the program) or in
/// %LOCALAPPDATA%\GameOpt\rules (your own, loaded last so they win). See rules/README.md.
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
    public List<RuleFile> Files { get; set; } = new();
    [JsonIgnore] public string Source { get; set; } = "";

    public bool Matches(GameInfo g) =>
        (g.SteamAppId is not null && Match.SteamAppIds.Contains(g.SteamAppId)) ||
        (g.Id.StartsWith("epic:") && Match.EpicAppNames.Contains(g.Id[5..], StringComparer.OrdinalIgnoreCase)) ||
        Match.InstallDirs.Contains(Path.GetFileName(g.InstallDir.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
}

public sealed class RuleMatch
{
    public List<string> SteamAppIds { get; set; } = new();
    public List<string> EpicAppNames { get; set; } = new();
    public List<string> InstallDirs { get; set; } = new();
}

public sealed class RuleFile
{
    /// <summary>gvas, ini, json or kv.</summary>
    public string Format { get; set; } = "";
    /// <summary>May use %ENVVARS%, %DOCUMENTS%, %SAVEDGAMES% and {install}.</summary>
    public string Path { get; set; } = "";
    public bool SteamCloud { get; set; }
    /// <summary>
    /// Setting name -> value. ini keys are "Section/Key" (split at the last '/'), json keys are dotted paths.
    /// A value is a literal, a "$variable", or a list of exactly 6 values, one per slider step.
    /// </summary>
    public Dictionary<string, JsonElement> Set { get; set; } = new();
}

public static class RuleSet
{
    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string UserRulesDir =>
        System.IO.Path.Combine(AppPaths.DataDir, "rules");

    public static List<GameRule> Load(List<string>? errors = null)
    {
        var rules = new Dictionary<string, GameRule>(StringComparer.OrdinalIgnoreCase);
        var asm = typeof(RuleSet).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".json") && n.Contains(".rules.")).Order())
        {
            using var s = asm.GetManifestResourceStream(res)!;
            Add(s, "built-in " + res);
        }
        if (Directory.Exists(UserRulesDir))
            foreach (var f in Directory.EnumerateFiles(UserRulesDir, "*.json").Order())
            {
                using var s = File.OpenRead(f);
                Add(s, f);
            }
        return rules.Values.ToList();

        void Add(Stream s, string source)
        {
            try
            {
                var r = JsonSerializer.Deserialize<GameRule>(s, Opts);
                if (r is null || string.IsNullOrWhiteSpace(r.Id)) throw new JsonException("missing \"id\"");
                r.Source = source;
                rules[r.Id] = r;
            }
            catch (JsonException e) { errors?.Add($"Rule file {source} ignored: {e.Message}"); }
        }
    }

    public static string ExpandPath(string path, GameInfo g)
    {
        var p = path.Replace("{install}", g.InstallDir, StringComparison.OrdinalIgnoreCase)
            .Replace("%DOCUMENTS%", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.OrdinalIgnoreCase)
            .Replace("%SAVEDGAMES%", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"), StringComparison.OrdinalIgnoreCase);
        return Environment.ExpandEnvironmentVariables(p);
    }

    /// <summary>Turn a rule value into int, double, bool, string or int[]; null when it cannot be resolved.</summary>
    public static object? Resolve(JsonElement v, int step, IReadOnlyDictionary<string, object> vars, out string? error)
    {
        error = null;
        if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 6) v = v[step];
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
