using System.Text.RegularExpressions;

namespace OptiGame.Core.Optimize;

/// <summary>Line-level edits of flat "key" "value" pairs in Valve text files.</summary>
public static partial class KvText
{
    public static bool TrySet(ref string text, string key, string value, out string? old)
    {
        var rx = Rx(key);
        var ms = rx.Matches(text);
        old = null;
        if (ms.Count != 1) return false;
        old = ms[0].Groups[2].Value;
        text = rx.Replace(text, m => m.Groups[1].Value + value + m.Groups[3].Value);
        return true;
    }

    public static string? Get(string text, string key)
    {
        var ms = Rx(key).Matches(text);
        return ms.Count == 1 ? ms[0].Groups[2].Value : null;
    }

    static Regex Rx(string key) =>
        new("^(\\s*\"" + Regex.Escape(key) + "\"\\s+\")([^\"]*)(\")", RegexOptions.Multiline | RegexOptions.IgnoreCase);
}
