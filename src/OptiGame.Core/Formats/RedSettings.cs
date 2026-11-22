using System.Text;
using System.Text.Json;

namespace OptiGame.Core.Formats;

/// <summary>One option of a REDengine 4 UserSettings.json (Cyberpunk 2077): where its value sits in the file's bytes.</summary>
public sealed class RedOption
{
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsDynamic { get; set; }
    /// <summary>Byte range of the "value" token (strings include their quotes).</summary>
    public (int Start, int End) Value { get; set; } = (-1, -1);
    /// <summary>Byte range of the "index" token, when the option is a list.</summary>
    public (int Start, int End) Index { get; set; } = (-1, -1);
    /// <summary>Raw JSON text of each entry of "values" (absent for dynamic lists).</summary>
    public List<string> Values { get; } = new();
}

/// <summary>
/// Reads and edits CD Projekt Red's UserSettings.json without rewriting it: options are found by scanning the bytes, and
/// a change replaces only the "value" token (and a list's "index" token). Every other byte, including spacing, line
/// endings and number formatting, stays as the game wrote it. default_value is never touched.
/// Shape: {"version":152,"data":[{"group_name":"/graphics/advanced","options":[{"name":..,"type":..,"value":..,"index":..,"values":[..]}]}]}
/// </summary>
public sealed class RedSettingsFile
{
    byte[] _bytes;
    List<RedOption> _options = new();

    public RedSettingsFile(byte[] bytes) { _bytes = bytes; Scan(); }

    public static RedSettingsFile Parse(byte[] bytes) => new(bytes);
    public byte[] ToBytes() => (byte[])_bytes.Clone();
    public IReadOnlyList<RedOption> Options => _options;

    public RedOption? Find(string group, string name) =>
        _options.FirstOrDefault(o => o.Group.Equals(group, StringComparison.Ordinal) && o.Name.Equals(name, StringComparison.Ordinal));

    /// <summary>The option's value as JSON text (<c>"High"</c> with quotes, <c>true</c>, <c>165</c>), or null when it is not there.</summary>
    public string? GetRaw(string group, string name) => Find(group, name) is { Value.Start: >= 0 } o ? Text(o.Value) : null;

    string Text((int Start, int End) r) => Encoding.UTF8.GetString(_bytes, r.Start, r.End - r.Start);

    /// <summary>
    /// Sets an option to a new raw JSON value. A list option must be given one of its own "values" and gets its "index" moved
    /// with it; a dynamic list (no "values") or a value of another JSON kind is refused. Returns false with the reason.
    /// </summary>
    public bool TrySet(string group, string name, string newRaw, out string error)
    {
        error = "";
        var o = Find(group, name);
        if (o is null || o.Value.Start < 0) { error = $"{name} is not in the file"; return false; }
        var cur = Text(o.Value);
        if (Kind(cur) != Kind(newRaw)) { error = $"{name}: {newRaw} is not the same kind of value as {cur}"; return false; }
        string? indexText = null;
        if (o.Index.Start >= 0)
        {
            if (o.Values.Count == 0) { error = $"{name} is a list the game builds at run time, so its index cannot be worked out"; return false; }
            int idx = o.Values.IndexOf(newRaw);
            if (idx < 0) { error = $"{name}: {newRaw} is not one of the game's values"; return false; }
            indexText = idx.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        // Edit the later token first so the earlier token's offsets stay valid.
        var edits = new List<((int Start, int End) Range, string Text)> { (o.Value, newRaw) };
        if (indexText is not null) edits.Add((o.Index, indexText));
        var buf = new List<byte>(_bytes);
        foreach (var (range, text) in edits.OrderByDescending(e => e.Range.Start))
        {
            buf.RemoveRange(range.Start, range.End - range.Start);
            buf.InsertRange(range.Start, Encoding.UTF8.GetBytes(text));
        }
        _bytes = buf.ToArray();
        Scan();
        return true;
    }

    static string Kind(string raw) =>
        raw.StartsWith('"') ? "string" : raw is "true" or "false" ? "bool" : raw == "null" ? "null" : raw.Contains('.') || raw.Contains('e') || raw.Contains('E') ? "float" : "int";

    // ------------------------------------------------------------------ scanning

    sealed class Frame { public bool IsArray; public string? Prop; }

    void Scan()
    {
        var options = new List<RedOption>();
        var stack = new List<Frame>();
        string group = "";
        RedOption? cur = null;
        var reader = new Utf8JsonReader(_bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        stack[^1].Prop = reader.GetString();
                        break;
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        stack.Add(new Frame { IsArray = reader.TokenType == JsonTokenType.StartArray });
                        // root{data:[ group{options:[ option{...
                        if (stack.Count == 5 && stack[0].Prop == "data" && stack[2].Prop == "options") cur = new RedOption { Group = group };
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (stack.Count == 5 && cur is not null && !stack[^1].IsArray)
                        {
                            if (cur.Name.Length > 0) options.Add(cur);
                            cur = null;
                        }
                        stack.RemoveAt(stack.Count - 1);
                        break;
                    default:
                    {
                        int start = (int)reader.TokenStartIndex, end = (int)reader.BytesConsumed;
                        // Strings and numbers are consumed exactly to the token's end.
                        if (stack.Count == 3 && stack[0].Prop == "data" && stack[2].Prop == "group_name" && reader.TokenType == JsonTokenType.String)
                            group = reader.GetString() ?? "";
                        else if (stack.Count == 5 && cur is not null && stack[0].Prop == "data" && stack[2].Prop == "options")
                        {
                            switch (stack[4].Prop)
                            {
                                case "name" when reader.TokenType == JsonTokenType.String:
                                    cur.Name = reader.GetString() ?? "";
                                    break;
                                case "type" when reader.TokenType == JsonTokenType.String: cur.Type = reader.GetString() ?? ""; break;
                                case "is_dynamic": cur.IsDynamic = reader.TokenType == JsonTokenType.True; break;
                                case "value": cur.Value = (start, end); break;
                                case "index": cur.Index = (start, end); break;
                            }
                        }
                        else if (stack.Count == 6 && cur is not null && stack[5].IsArray && stack[4].Prop == "values")
                            cur.Values.Add(Encoding.UTF8.GetString(_bytes, start, end - start));
                        break;
                    }
                }
            }
        }
        catch (JsonException e) { throw new FormatException("not valid JSON: " + e.Message); }
        _options = options;
    }
}
