using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OptiGame.Core.Optimize;

public enum RuleProblemSeverity { Error, Warning }

public sealed record RuleProblem(RuleProblemSeverity Severity, string Where, string Message)
{
    public static RuleProblem Error(string where, string msg) => new(RuleProblemSeverity.Error, where, msg);
    public static RuleProblem Warning(string where, string msg) => new(RuleProblemSeverity.Warning, where, msg);
    public override string ToString() => $"{(Severity == RuleProblemSeverity.Error ? "error" : "warning")}: {(Where.Length > 0 ? Where + ": " : "")}{Message}";
}

/// <summary>Checks a rule before it is used. Errors stop the rule from loading; warnings do not.</summary>
public static partial class RuleValidator
{
    public static readonly string[] Formats = { "gvas", "ini", "json", "kv", "registry" };

    public static List<RuleProblem> Validate(GameRule r, string? rawJson = null)
    {
        var p = new List<RuleProblem>();
        if (string.IsNullOrWhiteSpace(r.Id)) p.Add(RuleProblem.Error("id", "missing"));
        else if (!IdRx().IsMatch(r.Id)) p.Add(RuleProblem.Error("id", $"'{r.Id}' should be lowercase letters, digits and dashes"));
        if (string.IsNullOrWhiteSpace(r.Name)) p.Add(RuleProblem.Warning("name", "missing; the id will be shown instead"));
        if (r.Match.SteamAppIds.Count + r.Match.EpicAppNames.Count + r.Match.GogIds.Count + r.Match.InstallDirs.Count == 0)
            p.Add(RuleProblem.Error("match", "needs at least one of steamAppIds, epicAppNames, gogIds or installDirs"));
        foreach (var id in r.Match.SteamAppIds.Where(x => !x.All(char.IsAsciiDigit) || x.Length == 0))
            p.Add(RuleProblem.Error("match.steamAppIds", $"'{id}' is not a number"));
        if (r.Files.Count == 0) p.Add(RuleProblem.Error("files", "the rule lists no files"));
        if (r.Verified is { } v && !DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            p.Add(RuleProblem.Warning("verified", $"'{v}' should be a date like 2026-11-15"));
        if (r.FpsUncappedValue is { } u && u.ValueKind != JsonValueKind.Number)
            p.Add(RuleProblem.Error("fpsUncappedValue", "must be a number"));

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < r.Files.Count; i++)
        {
            var f = r.Files[i];
            var at = $"files[{i}]";
            var fmt = f.Format.ToLowerInvariant();
            if (!Formats.Contains(fmt)) p.Add(RuleProblem.Error(at + ".format", $"'{f.Format}' is not one of {string.Join(", ", Formats)}"));
            if (string.IsNullOrWhiteSpace(f.Path)) p.Add(RuleProblem.Error(at + ".path", "missing"));
            else if (!paths.Add(fmt + "|" + f.Path.Replace('\\', '/')))
                p.Add(RuleProblem.Error(at + ".path", $"'{f.Path}' is listed twice; merge the two entries"));
            if (fmt == "registry" && !RuleSet.TryRegistryPath(f.Path, out _))
                p.Add(RuleProblem.Error(at + ".path", "registry paths must start with HKCU/"));
            if (fmt != "registry" && f.Path.Contains('\\') && !f.Path.Contains("\\\\"))
                p.Add(RuleProblem.Warning(at + ".path", "use forward slashes in paths"));
            if (f.Set.Count == 0) p.Add(RuleProblem.Error(at + ".set", "no settings"));
            foreach (var (key, val) in f.Set)
            {
                var kat = $"{at}.set[\"{key}\"]";
                if (fmt == "ini" && key.LastIndexOf('/') <= 0) p.Add(RuleProblem.Error(kat, "ini keys need the form Section/Key"));
                CheckValue(p, kat, val, fmt);
            }
            if (f.Categories is not null)
                foreach (var (key, c) in f.Categories)
                {
                    if (!f.Set.ContainsKey(key)) p.Add(RuleProblem.Error($"{at}.categories[\"{key}\"]", "names a key that is not in set"));
                    if (!Categories.Keepable.Contains(c) && c != Categories.Other)
                        p.Add(RuleProblem.Error($"{at}.categories[\"{key}\"]", $"'{c}' is not one of {string.Join(", ", Categories.Keepable)}, other"));
                }
        }
        if (rawJson is not null) DuplicateKeys(p, rawJson);
        return p;
    }

    static void CheckValue(List<RuleProblem> p, string at, JsonElement v, string fmt)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Array:
                int n = v.GetArrayLength();
                if (n == 6)
                {
                    int i = 0;
                    foreach (var e in v.EnumerateArray()) CheckScalar(p, $"{at}[{i++}]", e, fmt);
                }
                else if (n == 2 && v.EnumerateArray().All(e => e.TryGetInt32(out _)))
                {
                    if (fmt is "registry" or "kv") p.Add(RuleProblem.Error(at, $"[width, height] pairs cannot be written to {fmt} values"));
                }
                else p.Add(RuleProblem.Error(at, $"a list needs exactly 6 values (one per slider step) or 2 whole numbers; this one has {n}"));
                break;
            default: CheckScalar(p, at, v, fmt); break;
        }
    }

    static void CheckScalar(List<RuleProblem> p, string at, JsonElement v, string fmt)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False: return;
            case JsonValueKind.String:
                var s = v.GetString()!;
                if (s.StartsWith('$') && !Profiles.KnownVariables.Contains(s[1..], StringComparer.OrdinalIgnoreCase))
                    p.Add(RuleProblem.Error(at, $"unknown variable {s}; known: {string.Join(", ", Profiles.KnownVariables.Select(x => "$" + x))}"));
                if (s is "$nativeResolution" or "$resolution" && fmt is "registry" or "kv")
                    p.Add(RuleProblem.Error(at, $"{s} is a width/height pair and cannot be written to a {fmt} value"));
                return;
            case JsonValueKind.Array when v.GetArrayLength() == 2 && v.EnumerateArray().All(e => e.TryGetInt32(out _)): return;
            default:
                p.Add(RuleProblem.Error(at, $"unsupported value {v.GetRawText()} (use a number, true/false, text, a $variable or [w, h])"));
                return;
        }
    }

    /// <summary>JSON allows a key twice in one object; the second silently wins. Flag that in "set".</summary>
    static void DuplicateKeys(List<RuleProblem> p, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("files", out var files) && !doc.RootElement.TryGetProperty("Files", out files)) return;
            if (files.ValueKind != JsonValueKind.Array) return;
            int i = 0;
            foreach (var f in files.EnumerateArray())
            {
                if (f.ValueKind == JsonValueKind.Object && (f.TryGetProperty("set", out var set) || f.TryGetProperty("Set", out set)) && set.ValueKind == JsonValueKind.Object)
                    foreach (var dup in set.EnumerateObject().GroupBy(x => x.Name).Where(g => g.Count() > 1))
                        p.Add(RuleProblem.Error($"files[{i}].set[\"{dup.Key}\"]", $"listed {dup.Count()} times with different values; only one can apply"));
                i++;
            }
        }
        catch (JsonException) { }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")] private static partial Regex IdRx();
}
