using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Library.ConfigDiscovery;
using OptiGame.Core.Optimize;

namespace OptiGame.Core.Rules;

public sealed record RuleDraft(string Json, IReadOnlyList<string> Skipped, IReadOnlyList<RuleProblem> Problems);

/// <summary>Drafts an inactive rule from two snapshots without reading or changing the captured target.</summary>
public static class SnapshotRuleDraft
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static Snapshot ReadSnapshot(string json)
    {
        try { return JsonSerializer.Deserialize<Snapshot>(json, SnapshotOptions) ?? throw new FormatException("Empty snapshot."); }
        catch (JsonException ex) { throw new FormatException("Invalid or ambiguous snapshot JSON. " + ex.Message, ex); }
    }

    public static RuleDraft Build(GameInfo game, Snapshot before, Snapshot after, QualityStep step, IReadOnlyList<string> keys)
    {
        if (!Enum.IsDefined(step))
            throw new ArgumentOutOfRangeException(nameof(step));
        if (!Snapshot.Formats.Contains(before.Format) || before.Format != after.Format)
            throw new FormatException("Both snapshots must use the same supported format.");
        if (before.Taken == default || after.Taken == default || after.Taken < before.Taken)
            throw new FormatException("Both capture times are required, with the before snapshot first.");

        var target = Target(game, before.Format, before.Target);
        if (!target.Equals(Target(game, after.Format, after.Target), StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Both snapshots must identify the same settings target.");

        var changed = Snapshot.Diff(before, after);
        var requested = keys.ToHashSet(StringComparer.Ordinal);
        var observed = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var change in changed)
        {
            if (requested.Count > 0 && !requested.Contains(change.Key)) continue;
            if (change.Before is null || change.After is null)
                skipped.Add(change.Key + " was added, removed or unreadable in one snapshot.");
            else if (!SameValueType(after.Format, change.Before, change.After))
                skipped.Add(change.Key + " changed type between snapshots.");
            else if (string.IsNullOrWhiteSpace(change.Key) || change.Key.Contains('|') ||
                     Literal(after.Format, change.After) is not { } value)
                skipped.Add(change.Key + " could not be represented as a literal rule setting.");
            else
                observed.Add(change.Key, value);
        }

        foreach (var key in requested.Where(key => !changed.Any(change => change.Key == key)))
            skipped.Add(key + " has no recorded change.");
        if (observed.Count == 0 || requested.Any(key => !observed.ContainsKey(key)))
            throw new FormatException("No draft was written because a selected setting had no usable observation." +
                (skipped.Count == 0 ? " No settings changed." : "\n" + string.Join("\n", skipped)));
        if (requested.Count == 0 && observed.Count > 1)
            throw new ArgumentException("More than one setting changed. Repeat --key for the settings you intended to change: " +
                string.Join(", ", observed.Keys));

        var set = new JsonObject();
        foreach (var (key, value) in observed)
        {
            var slots = new JsonNode?[QualitySteps.Count];
            slots[(int)step] = value;
            set[key] = new JsonArray(slots);
        }

        var notes = FormattableString.Invariant(
            $"Snapshot draft for the {step.Label()} step. Each setting value came from the after capture ({after.Taken:O}) and changed from the before capture ({before.Taken:O}). Other steps keep the current setting.");
        var (json, rule, problems) = RuleBuilder.BuildDocument(game, after.Format, target, set, null, null, notes,
            "OptiGame+ before/after snapshots");
        if (problems.Any(problem => problem.Severity == RuleProblemSeverity.Error))
            throw new FormatException(string.Join("\n", problems));
        if (after.Format != "registry" && !target.Equals(Target(game, after.Format, rule.Files[0].Path),
                StringComparison.OrdinalIgnoreCase))
            throw new FormatException("The rule path did not resolve to the observed settings file.");
        return new(json, skipped, problems);
    }

    /// <summary>Saves to the drafts subdirectory, outside the active user rule list.</summary>
    public static string Save(RuleDraft draft)
    {
        var rule = RuleSet.TryParse(draft.Json, out var problems);
        if (rule is null || problems.Any(problem => problem.Severity == RuleProblemSeverity.Error) ||
            !ConfigPaths.IsFileComponent(rule.Id))
            throw new FormatException("The rule draft did not pass validation.");

        const string folder = "drafts";
        var directory = ConfigPaths.CanonicalLocal(Path.GetFullPath(Path.Combine(RuleSet.UserRulesDir, folder)));
        if (directory is null || ConfigPaths.Inspect(directory) is not (PathState.Directory or PathState.Missing))
            throw new IOException("The user rules draft folder is unavailable.");
        Directory.CreateDirectory(directory);
        if (ConfigPaths.Inspect(directory) != PathState.Directory)
            throw new IOException("The user rules draft folder is unavailable.");

        var path = Path.Combine(directory, rule.Id + ".json");
        var temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
        try
        {
            using (stream)
            using (var writer = new StreamWriter(stream))
                writer.Write(draft.Json);
            File.Move(temp, path);
        }
        finally { File.Delete(temp); }
        return path;
    }

    static string Target(GameInfo game, string format, string target)
    {
        if (format == "registry")
            return RuleSet.TryRegistryPath(target, out var subKey)
                ? "HKCU/" + subKey.Replace('\\', '/')
                : throw new FormatException("A registry snapshot must identify an HKCU key.");
        return ConfigPaths.CanonicalLocal(RuleSet.ExpandPath(target, game))
            ?? throw new FormatException("A file snapshot must identify a fully qualified local path.");
    }

    static bool SameValueType(string format, SnapValue before, SnapValue after) =>
        before.Type == after.Type ||
        format == "json" && before.Type is ("int" or "double") && after.Type is ("int" or "double");

    static JsonNode? Literal(string format, SnapValue value)
    {
        var text = value.Display;
        if (text.StartsWith('$')) return null;
        if (format is "ini" or "kv")
            return value.Type is ("int" or "double" or "bool" or "string") && text.AsSpan().IndexOfAny('\r', '\n', '\0') < 0
                ? JsonValue.Create(text)
                : null;

        return (format, value.Type) switch
        {
            ("json" or "registry", "string") => JsonValue.Create(text),
            ("gvas", "int") or ("registry", "dword" or "qword")
                when int.TryParse(text, NumberStyles.Integer, Inv, out var number) => JsonValue.Create(number),
            ("json", "int" or "double") => JsonNumber(text),
            ("json" or "gvas", "bool") when text is "true" or "false" or "on" or "off" => RuleBuilder.TypedValue("bool", text),
            ("gvas", "intpoint") => TryIntPoint(text),
            _ => null,
        };
    }

    static JsonNode? JsonNumber(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, Inv, out var number) || !double.IsFinite(number)) return null;
        try
        {
            using var input = JsonDocument.Parse(text);
            return JsonElement.DeepEquals(input.RootElement, JsonSerializer.SerializeToElement(number))
                ? JsonValue.Create(number)
                : null;
        }
        catch (JsonException) { return null; }
    }

    static JsonNode? TryIntPoint(string text)
    {
        if (text.Split('x') is [var w, var h] && int.TryParse(w, NumberStyles.Integer, Inv, out var width) &&
            int.TryParse(h, NumberStyles.Integer, Inv, out var height))
            return new JsonArray(width, height);
        return null;
    }
}
