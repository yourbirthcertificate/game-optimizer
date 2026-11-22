using System.Text;

namespace OptiGame.Core.Formats;

/// <summary>
/// Line-preserving INI editor. Only lines we change are rewritten, so comments, ordering, duplicate
/// keys (Unreal's +Key= arrays) and unknown sections survive untouched. Keeps the file's encoding,
/// byte-order mark and line endings.
/// </summary>
public sealed class IniDocument
{
    readonly List<string> _lines;
    readonly Encoding _encoding;
    readonly byte[] _bom;
    readonly string _newline;
    readonly bool _finalNewline;

    IniDocument(List<string> lines, Encoding enc, byte[] bom, string nl, bool finalNl)
    { _lines = lines; _encoding = enc; _bom = bom; _newline = nl; _finalNewline = finalNl; }

    public static IniDocument Parse(byte[] data)
    {
        Encoding enc; int skip = 0;
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) { enc = new UnicodeEncoding(false, false); skip = 2; }
        else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) { enc = new UnicodeEncoding(true, false); skip = 2; }
        else if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) { enc = new UTF8Encoding(false); skip = 3; }
        else enc = new UTF8Encoding(false, throwOnInvalidBytes: true);
        var bom = data[..skip];
        string text;
        try { text = enc.GetString(data, skip, data.Length - skip); }
        catch (DecoderFallbackException) { enc = Encoding.Latin1; text = enc.GetString(data); }
        if (text.Contains('\0')) throw new FormatException("File contains NUL characters; not a text INI.");
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        bool finalNl = text.EndsWith('\n');
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (finalNl) lines.RemoveAt(lines.Count - 1);
        return new IniDocument(lines, enc, bom, nl, finalNl);
    }

    public byte[] ToBytes()
    {
        var text = string.Join(_newline, _lines) + (_finalNewline ? _newline : "");
        return _bom.Concat(_encoding.GetBytes(text)).ToArray();
    }

    static bool IsSection(string line, out string name)
    {
        var t = line.Trim();
        if (t.Length > 1 && t[0] == '[' && t[^1] == ']') { name = t[1..^1]; return true; }
        name = "";
        return false;
    }

    public IEnumerable<string> Sections()
    {
        foreach (var l in _lines) if (IsSection(l, out var n)) yield return n;
    }

    /// <summary>(start, end) line range of a section's body, or null.</summary>
    (int start, int end)? Range(string section)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            if (IsSection(_lines[i], out var n) && n.Equals(section, StringComparison.OrdinalIgnoreCase))
            {
                int j = i + 1;
                while (j < _lines.Count && !IsSection(_lines[j], out _)) j++;
                return (i + 1, j);
            }
        }
        return null;
    }

    int FindKey(string section, string key, out int count)
    {
        count = 0; int at = -1;
        var r = Range(section);
        if (r is null) return -1;
        for (int i = r.Value.start; i < r.Value.end; i++)
        {
            var l = _lines[i];
            int eq = l.IndexOf('=');
            if (eq <= 0 || l.TrimStart().StartsWith(';') || l.TrimStart().StartsWith('#')) continue;
            if (l[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) { count++; if (at < 0) at = i; }
        }
        return at;
    }

    public bool HasSection(string section) => Range(section) is not null;

    /// <summary>Removes a key that appears exactly once (used to undo a key the optimizer added).</summary>
    public bool Remove(string section, string key)
    {
        int i = FindKey(section, key, out int count);
        if (i < 0 || count != 1) return false;
        _lines.RemoveAt(i);
        return true;
    }

    /// <summary>All key=value pairs as "Section/Key" -> value; keys that appear more than once map to null.</summary>
    public Dictionary<string, string?> Flatten()
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        foreach (var l in _lines)
        {
            if (IsSection(l, out var n)) { section = n; continue; }
            int eq = l.IndexOf('=');
            if (eq <= 0 || l.TrimStart().StartsWith(';') || l.TrimStart().StartsWith('#')) continue;
            var k = section + "/" + l[..eq].Trim();
            d[k] = d.ContainsKey(k) ? null : l[(eq + 1)..].Trim();
        }
        return d;
    }

    public string? Get(string section, string key)
    {
        int i = FindKey(section, key, out _);
        return i < 0 ? null : _lines[i][(_lines[i].IndexOf('=') + 1)..].Trim();
    }

    /// <summary>
    /// Set a key. Refuses keys that appear more than once in the section (array-style keys), since
    /// rewriting one copy would be a guess. Adds the key (and the section) when missing and allowed.
    /// </summary>
    public bool Set(string section, string key, string value, bool addIfMissing, out string? error)
    {
        error = null;
        int i = FindKey(section, key, out int count);
        if (count > 1) { error = $"[{section}] {key} appears {count} times"; return false; }
        if (i >= 0)
        {
            var l = _lines[i];
            int eq = l.IndexOf('=');
            _lines[i] = l[..(eq + 1)] + value;
            return true;
        }
        if (!addIfMissing) { error = $"[{section}] {key} not present"; return false; }
        var r = Range(section);
        if (r is null)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return true;
        }
        int insert = r.Value.end;
        while (insert > r.Value.start && _lines[insert - 1].Trim().Length == 0) insert--;
        _lines.Insert(insert, $"{key}={value}");
        return true;
    }
}
