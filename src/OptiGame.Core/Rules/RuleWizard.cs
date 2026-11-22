using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptiGame.Core.Formats;
using OptiGame.Core.Library;
using OptiGame.Core.Optimize;
using Microsoft.Win32;

namespace OptiGame.Core.Rules;

/// <summary>One readable value in a snapshot. Type is int, double, bool, string, intpoint, dword, qword.</summary>
public sealed record SnapValue(string Type, string Display);

public sealed record SnapDiff(string Key, SnapValue? Before, SnapValue? After)
{
    public string Kind => Before is null ? "added" : After is null ? "removed" : "changed";
    public override string ToString() => $"{Key}: {Before?.Display ?? "(none)"} -> {After?.Display ?? "(none)"}";
}

/// <summary>
/// A read-only picture of one settings file or registry key. Capturing never writes anything and never
/// starts the game. Values the tool cannot read or write safely are listed in Unreadable instead.
/// </summary>
public sealed class Snapshot
{
    public string Format { get; set; } = "";
    public string Target { get; set; } = "";
    public DateTime Taken { get; set; }
    public Dictionary<string, SnapValue> Values { get; set; } = new();
    public List<string> Unreadable { get; set; } = new();

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly string[] Formats = RuleValidator.Formats;

    /// <summary>Guess the format from a file name; null when the user has to say.</summary>
    public static string? GuessFormat(string target)
    {
        if (RuleSet.TryRegistryPath(target, out _)) return "registry";
        return Path.GetExtension(target).ToLowerInvariant() switch
        {
            ".ini" or ".cfg" when !target.EndsWith("video.txt", StringComparison.OrdinalIgnoreCase) => "ini",
            ".sav" => "gvas",
            ".json" => "json",
            ".txt" or ".vcfg" => "kv",
            _ => null,
        };
    }

    public static Snapshot Capture(string format, string target)
    {
        var s = new Snapshot { Format = format.ToLowerInvariant(), Target = target, Taken = DateTime.Now };
        if (s.Format == "registry") { CaptureRegistry(s, target); return s; }
        if (!File.Exists(target)) throw new FileNotFoundException("Settings file not found", target);
        var data = File.ReadAllBytes(target);
        switch (s.Format)
        {
            case "ini":
                foreach (var (k, v) in IniDocument.Parse(data).Flatten())
                    if (v is null) s.Unreadable.Add($"{k}: appears more than once (array-style key)");
                    else s.Values[k] = new SnapValue(TypeOfText(v), v);
                break;
            case "gvas":
                GvasFile sav;
                try { sav = GvasFile.Load(data); }
                catch (FormatException e) { throw new FormatException("This save cannot be read safely, so it cannot be used for a rule: " + e.Message, e); }
                foreach (var p in sav.Properties)
                {
                    var v = p.Get();
                    if (v is null) { s.Unreadable.Add($"{p.Name}: {p.Type} values are not editable"); continue; }
                    s.Values[p.Name] = new SnapValue(v switch { int => "int", long => "int", double => "double", bool => "bool", int[] => "intpoint", _ => "string" }, Planner.Show(v));
                }
                break;
            case "json":
                JsonNode? root;
                try { root = JsonNode.Parse(data, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
                catch (JsonException e) { throw new FormatException("Not valid JSON: " + e.Message, e); }
                FlattenJson(s, root, "");
                break;
            case "kv":
                var text = new System.Text.UTF8Encoding(false, true).GetString(data);
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var leaves = new List<(string k, string v)>();
                void Walk(KvNode n) { foreach (var c in n.Children) { if (c.Value is not null) { leaves.Add((c.Key, c.Value)); counts[c.Key] = counts.GetValueOrDefault(c.Key) + 1; } else Walk(c); } }
                Walk(KvNode.Parse(text));
                foreach (var (k, v) in leaves)
                    if (counts[k] > 1) { if (!s.Unreadable.Any(u => u.StartsWith(k + ":"))) s.Unreadable.Add($"{k}: appears more than once"); }
                    else s.Values[k] = new SnapValue(TypeOfText(v), v);
                break;
            default: throw new NotSupportedException($"'{format}' files cannot be read safely; supported: {string.Join(", ", Formats)}");
        }
        return s;
    }

    static void CaptureRegistry(Snapshot s, string target)
    {
        if (!RuleSet.TryRegistryPath(target, out var sub)) throw new ArgumentException("Registry targets look like HKCU/Software/Company/Game.");
        using var key = Registry.CurrentUser.OpenSubKey(sub) ?? throw new FileNotFoundException($"HKCU\\{sub} does not exist (start the game once).");
        foreach (var name in key.GetValueNames())
        {
            switch (key.GetValueKind(name))
            {
                case RegistryValueKind.DWord: s.Values[name] = new SnapValue("dword", ((int)key.GetValue(name)!).ToString(Inv)); break;
                case RegistryValueKind.QWord: s.Values[name] = new SnapValue("qword", ((long)key.GetValue(name)!).ToString(Inv)); break;
                case RegistryValueKind.String: s.Values[name] = new SnapValue("string", (string)key.GetValue(name)!); break;
                default: s.Unreadable.Add($"{name}: {key.GetValueKind(name)} values are not editable (Unity stores text this way)"); break;
            }
        }
    }

    static void FlattenJson(Snapshot s, JsonNode? n, string prefix)
    {
        switch (n)
        {
            case JsonObject o: foreach (var (k, v) in o) FlattenJson(s, v, prefix.Length == 0 ? k : prefix + "." + k); break;
            case JsonArray: s.Unreadable.Add($"{prefix}: lists are not editable"); break;
            case JsonValue v:
                var kind = v.GetValueKind();
                s.Values[prefix] = new SnapValue(kind switch
                {
                    JsonValueKind.True or JsonValueKind.False => "bool",
                    JsonValueKind.Number => v.ToJsonString().Contains('.') ? "double" : "int",
                    _ => "string",
                }, kind == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString());
                break;
        }
    }

    static string TypeOfText(string v) =>
        int.TryParse(v, NumberStyles.Integer, Inv, out _) ? "int"
        : double.TryParse(v, NumberStyles.Float, Inv, out _) ? "double"
        : v is "True" or "False" or "true" or "false" ? "bool" : "string";

    public static List<SnapDiff> Diff(Snapshot before, Snapshot after) =>
        before.Values.Keys.Union(after.Values.Keys)
            .Select(k => new SnapDiff(k, before.Values.GetValueOrDefault(k), after.Values.GetValueOrDefault(k)))
            .Where(d => d.Before?.Display != d.After?.Display || d.Before?.Type != d.After?.Type)
            .OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase).ToList();

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    public static Snapshot FromJson(string json) => JsonSerializer.Deserialize<Snapshot>(json) ?? throw new FormatException("empty snapshot");
}

/// <summary>One key the user chose for a rule: either one value for every step, or exactly six.</summary>
public sealed class DraftKey
{
    public string Key { get; set; } = "";
    public string Type { get; set; } = "string";
    public string? Label { get; set; }
    public string? Category { get; set; }
    /// <summary>One entry = same value at every step; six entries = Low..Ultra. Values are text, typed by Type.</summary>
    public List<string> Values { get; set; } = new();
}

/// <summary>Turns a snapshot diff plus the user's choices into a validated local rule.</summary>
public static class RuleBuilder
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Path with machine-specific parts replaced by variables, so the rule works on other PCs.</summary>
    public static string TemplatePath(string path, GameInfo? game)
    {
        if (RuleSet.TryRegistryPath(path, out var sub)) return "HKCU/" + sub.Replace('\\', '/');
        var p = path;
        if (game is not null && p.StartsWith(game.InstallDir, StringComparison.OrdinalIgnoreCase)) p = "{install}" + p[game.InstallDir.Length..];
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
        foreach (var (dir, token) in new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (docs, "%DOCUMENTS%"), (saved, "%SAVEDGAMES%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
        })
            if (dir.Length > 0 && p.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) { p = token + p[dir.Length..]; break; }
        return p.Replace('\\', '/');
    }

    public static JsonNode TypedValue(string type, string text)
    {
        text = text.Trim();
        if (text.StartsWith('$')) return JsonValue.Create(text)!;
        switch (type)
        {
            case "int" or "dword" or "qword":
                if (long.TryParse(text, NumberStyles.Integer, Inv, out var l)) return JsonValue.Create(l)!;
                break;
            case "double":
                if (double.TryParse(text, NumberStyles.Float, Inv, out var d)) return JsonValue.Create(d)!;
                break;
            case "bool":
                if (text is "on" or "true" or "True" or "1") return JsonValue.Create(true)!;
                if (text is "off" or "false" or "False" or "0") return JsonValue.Create(false)!;
                break;
            case "intpoint":
                var parts = text.Split('x', ',', ' ').Where(x => x.Length > 0).ToArray();
                if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)) return new JsonArray(w, h);
                break;
            default: return JsonValue.Create(text)!;
        }
        throw new FormatException($"'{text}' is not a valid {type} value");
    }

    /// <summary>Builds the rule JSON. Throws FormatException with every problem when it does not validate.</summary>
    public static (string json, GameRule rule, List<RuleProblem> problems) Build(
        GameInfo game, string format, string targetPath, IEnumerable<DraftKey> keys, string? author = null, string? gameVersion = null, string? notes = null)
    {
        var id = Prefs.NewId(game.Name);
        var match = new JsonObject();
        if (game.SteamAppId is not null) match["steamAppIds"] = new JsonArray(game.SteamAppId);
        if (game.GogId is not null) match["gogIds"] = new JsonArray(game.GogId);
        if (game.Id.StartsWith("epic:")) match["epicAppNames"] = new JsonArray(game.Id[5..]);
        match["installDirs"] = new JsonArray(Path.GetFileName(game.InstallDir.TrimEnd('/', Path.DirectorySeparatorChar)));

        var set = new JsonObject();
        var cats = new JsonObject();
        var labels = new JsonObject();
        var errors = new List<string>();
        foreach (var k in keys)
        {
            try
            {
                if (k.Values.Count == 1) set[k.Key] = TypedValue(k.Type, k.Values[0]);
                else if (k.Values.Count == 6) set[k.Key] = new JsonArray(k.Values.Select(v => TypedValue(k.Type, v)).ToArray());
                else { errors.Add($"{k.Key}: give one value for every step, or exactly six (Low to Ultra); got {k.Values.Count}"); continue; }
            }
            catch (FormatException e) { errors.Add($"{k.Key}: {e.Message}"); continue; }
            if (!string.IsNullOrWhiteSpace(k.Category)) cats[k.Key] = k.Category;
            if (!string.IsNullOrWhiteSpace(k.Label)) labels[k.Key] = k.Label;
        }
        if (errors.Count > 0) throw new FormatException(string.Join("\n", errors));

        var file = new JsonObject { ["format"] = format, ["path"] = TemplatePath(targetPath, game), ["set"] = set };
        if (cats.Count > 0) file["categories"] = cats;
        if (labels.Count > 0) file["labels"] = labels;
        var root = new JsonObject
        {
            ["id"] = id, ["name"] = game.Name, ["match"] = match,
            ["notes"] = notes ?? "Created with the rule wizard from before/after snapshots.",
            ["author"] = author, ["gameVersion"] = gameVersion,
            ["source"] = "Rule wizard: before/after snapshots on the author's PC",
            ["verified"] = DateTime.Now.ToString("yyyy-MM-dd", Inv),
            ["files"] = new JsonArray(file),
        };
        foreach (var k in root.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) root.Remove(k);
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var rule = RuleSet.TryParse(json, out var problems) ?? throw new FormatException(string.Join("\n", problems));
        return (json, rule, problems);
    }

    /// <summary>Saves to the user rules folder. Refuses to replace an existing file unless asked.</summary>
    public static string Save(string json, string id, bool overwrite = false)
    {
        Directory.CreateDirectory(RuleSet.UserRulesDir);
        var path = Path.Combine(RuleSet.UserRulesDir, id + ".json");
        if (File.Exists(path) && !overwrite) throw new IOException($"{path} already exists. Choose another name or allow replacing it.");
        File.WriteAllText(path, json);
        return path;
    }
}
