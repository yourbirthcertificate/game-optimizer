using System.Text;
using System.Text.RegularExpressions;

namespace OptiGame.Core.Formats;

internal sealed class FlatIniHandler : IFormatHandler
{
    public ISettingsDocument Read(byte[] bytes) => new FlatIniSettings(bytes);
}

/// <summary>Reads flat INI assignments and replaces only uniquely identified boolean values.</summary>
internal sealed partial class FlatIniSettings : ISettingsDocument
{
    internal const string Format = "flat-ini";
    static readonly UTF8Encoding Utf8 = new(false, true);
    readonly byte[] _bom;
    string _text;
    Dictionary<string, Field?> _fields;

    public FlatIniSettings(byte[] bytes)
    {
        if (bytes.Length > 2 * 1024 * 1024) throw new FormatException("The INI exceeds the settings size limit.");
        int offset = bytes.AsSpan().StartsWith("\uFEFF"u8) ? 3 : 0;
        _bom = bytes[..offset];
        try { _text = Utf8.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException ex) { throw new FormatException("The INI is not valid UTF-8.", ex); }
        _fields = ReadFields(_text);
    }

    public string? GetRaw(string locator) => Find(locator)?.Value;

    public bool TrySet(string locator, string value)
    {
        if (value is not ("true" or "false") || Find(locator) is not { Value: "true" or "false" } field)
            return false;
        var updated = string.Concat(_text.AsSpan(0, field.Offset), value,
            _text.AsSpan(field.Offset + field.Value.Length));
        _fields = ReadFields(updated);
        _text = updated;
        return true;
    }

    public byte[] ToBytes() => [.. _bom, .. Utf8.GetBytes(_text)];

    Field? Find(string locator) =>
        locator.Split('|') is [Format, var key] && _fields.GetValueOrDefault(key) is { } field &&
        field.Key.Equals(key, StringComparison.Ordinal) ? field : null;

    static Dictionary<string, Field?> ReadFields(string text)
    {
        if (text.Contains('\0')) throw new FormatException("The INI contains NUL characters.");
        // A different spelling of the same key is ambiguous even though we edit only an exact key.
        var fields = new Dictionary<string, Field?>(StringComparer.OrdinalIgnoreCase);
        foreach (Match line in Line().Matches(text))
        {
            var raw = text.AsSpan(line.Index, line.Length);
            var trimmed = raw.TrimStart();
            if (trimmed.IsEmpty || trimmed[0] is '#' or ';') continue;

            int equals = raw.IndexOf('=');
            if (equals <= 0 || !Key().IsMatch(raw[..equals].Trim()))
                throw new FormatException("Expected sectionless key=value assignments.");

            var key = raw[..equals].Trim().ToString();
            var rest = raw[(equals + 1)..];
            var value = rest.TrimStart();
            int valueOffset = line.Index + line.Length - value.Length;
            var field = new Field(key, value.TrimEnd().ToString(), valueOffset);
            if (!fields.TryAdd(key, field)) fields[key] = null;
        }
        return fields;
    }

    sealed record Field(string Key, string Value, int Offset);

    [GeneratedRegex("[^\\r\\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex Line();

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Key();
}
