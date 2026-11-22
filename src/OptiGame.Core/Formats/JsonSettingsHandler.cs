using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptiGame.Core.Optimize;
using static OptiGame.Core.Optimize.Planner;

namespace OptiGame.Core.Formats;

internal sealed class JsonSettingsHandler : IRuleFormatHandler
{
    public JsonSettingsDocument Read(byte[] bytes)
    {
        try { return Parse(bytes); }
        catch (JsonException ex) { throw new FormatException("not valid JSON: " + ex.Message); }
    }

    ISettingsDocument IFormatHandler.Read(byte[] bytes) => Read(bytes);

    public ISettingsDocument ReadForUndo(byte[] bytes) => Parse(bytes);

    static JsonSettingsDocument Parse(byte[] bytes)
    {
        var root = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        }) ?? throw new FormatException("empty JSON");
        return new(root, bytes.Contains((byte)'\n'));
    }

    public void EditRule(GamePlan plan, OptimizeRequest req, FileEdit edit, IReadOnlyList<RuleValue> values)
    {
        var document = Read(edit.Original);
        foreach (var rv in values)
        {
            var locator = "json|" + rv.Key;
            var (obj, key) = document.Locate(locator);
            if (obj is null || !obj.TryGetPropertyValue(key, out var cur) || cur is not JsonValue jv)
            { plan.Skipped.Add($"Kept: {rv.Key} (not in {Path.GetFileName(edit.Path)})"); continue; }
            var kind = jv.GetValueKind();
            JsonNode? nv = (rv.Value, kind) switch
            {
                (bool b, JsonValueKind.True or JsonValueKind.False) => JsonValue.Create(b),
                (int i, JsonValueKind.Number) => JsonValue.Create(i),
                (double d, JsonValueKind.Number) => JsonValue.Create(d),
                (string s, JsonValueKind.String) => JsonValue.Create(s),
                _ => null,
            };
            if (nv is null) { plan.Skipped.Add($"Kept: {rv.Key} (rule value does not match its type)"); continue; }
            var oldText = jv.ToJsonString();
            var newText = nv.ToJsonString();
            if (oldText == newText) continue;
            if (!Want(plan, req, rv.Id, rv.Category, rv.Key, oldText, newText)) continue;
            obj[key] = nv;
            edit.Changes.Add(new(rv.Key, oldText, newText) { Id = rv.Id, Category = rv.Category, Locator = locator, OldRaw = oldText, NewRaw = newText });
        }
        edit.Updated = document.ToBytes();
    }
}

internal sealed class JsonSettingsDocument(JsonNode root, bool indented) : ISettingsDocument
{
    public (JsonObject? Object, string Key) Locate(string locator)
    {
        var parts = locator["json|".Length..].Split('.');
        JsonNode? parent = root;
        foreach (var part in parts[..^1]) parent = parent?[part];
        return (parent as JsonObject, parts[^1]);
    }

    public string? GetRaw(string locator)
    {
        var (obj, key) = Locate(locator);
        return obj is not null && obj.TryGetPropertyValue(key, out var node) ? node?.ToJsonString() : null;
    }

    public bool TrySet(string locator, string value)
    {
        var (obj, key) = Locate(locator);
        if (obj is null) return false;
        obj[key] = JsonNode.Parse(value);
        return true;
    }

    public void RestoreRaw(string locator, string? value)
    {
        var (obj, key) = Locate(locator);
        obj![key] = value is null ? null : JsonNode.Parse(value);
    }

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = indented }));
}
