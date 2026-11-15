using System.Text;

namespace GameOptimizer.Core.Formats;

/// <summary>
/// Valve text KeyValues (VDF, ACF, Source video.txt). Read-only tree parser plus a
/// line-preserving value setter for flat files like video.txt.
/// </summary>
public sealed class KvNode
{
    public string Key { get; }
    public string? Value { get; }
    public List<KvNode> Children { get; } = new();

    public KvNode(string key, string? value = null) { Key = key; Value = value; }

    public KvNode? this[string key] =>
        Children.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public string? Get(string key) => this[key]?.Value;

    public static KvNode Parse(string text)
    {
        var tokens = Tokenize(text).GetEnumerator();
        var root = new KvNode("<root>");
        ParseInto(root, tokens, topLevel: true);
        return root;
    }

    static void ParseInto(KvNode parent, IEnumerator<string?> tokens, bool topLevel)
    {
        while (tokens.MoveNext())
        {
            var key = tokens.Current;
            if (key is null) // closing brace
            {
                if (topLevel) throw new FormatException("Unbalanced '}' in KeyValues text");
                return;
            }
            if (!tokens.MoveNext()) throw new FormatException($"Key '{key}' has no value");
            var next = tokens.Current;
            if (next == "\u0001{")
            {
                var child = new KvNode(key);
                ParseInto(child, tokens, topLevel: false);
                parent.Children.Add(child);
            }
            else if (next is null) throw new FormatException($"Key '{key}' followed by '}}'");
            else parent.Children.Add(new KvNode(key, next));
        }
        if (!topLevel) throw new FormatException("Missing '}' in KeyValues text");
    }

    /// <summary>Yields strings; "{" as "\u0001{" and "}" as null. Skips comments and [$CONDITIONALS].</summary>
    static IEnumerable<string?> Tokenize(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '{') { i++; yield return "\u0001{"; continue; }
            if (c == '}') { i++; yield return null; continue; }
            if (c == '[') { while (i < s.Length && s[i] != ']') i++; i++; continue; }
            var sb = new StringBuilder();
            if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        i++;
                        sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });
                    }
                    else sb.Append(s[i]);
                    i++;
                }
                if (i >= s.Length) throw new FormatException("Unterminated string in KeyValues text");
                i++;
            }
            else
            {
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}' && s[i] != '"') sb.Append(s[i++]);
            }
            yield return sb.ToString();
        }
    }
}
