using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace OptiGame.Core.Formats;

internal sealed class XmlSettingsHandler : IFormatHandler
{
    public ISettingsDocument Read(byte[] bytes) => new XmlSettings(bytes);
}

/// <summary>Edits literal scalar text in unique direct children of an XML root.</summary>
internal sealed partial class XmlSettings : ISettingsDocument
{
    const int MaxSize = 2 * 1024 * 1024;
    readonly Encoding _encoding;
    readonly byte[] _bom;
    string _text;
    XDocument _document;

    public XmlSettings(byte[] bytes)
    {
        if (bytes.Length > MaxSize) throw new FormatException("The XML exceeds the settings size limit.");
        (_encoding, int offset) = bytes.AsSpan() switch
        {
            [0xff, 0xfe, ..] => (new UnicodeEncoding(false, false, true), 2),
            [0xfe, 0xff, ..] => (new UnicodeEncoding(true, false, true), 2),
            [0xef, 0xbb, 0xbf, ..] => (new UTF8Encoding(false, true), 3),
            _ => ((Encoding)new UTF8Encoding(false, true), 0),
        };
        _bom = bytes[..offset];
        try { _text = _encoding.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException ex) { throw new FormatException("The XML encoding is unsupported.", ex); }

        _document = Parse(_text);
        if (_document.Declaration?.Encoding is { } declaration &&
            !declaration.Equals(_encoding.WebName, StringComparison.OrdinalIgnoreCase) &&
            !(_encoding is UnicodeEncoding && declaration.Equals("utf-16", StringComparison.OrdinalIgnoreCase)))
            throw new FormatException("The XML encoding declaration does not match its bytes.");
    }

    static XDocument Parse(string text)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxSize,
            });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException ex) { throw new FormatException("The settings XML could not be parsed safely.", ex); }
    }

    public string? GetRaw(string locator) => FindTokens(locator) is { } tokens
        ? string.Join("x", tokens.Select(token => token.Value)) : null;

    public bool TrySet(string locator, string value)
    {
        if (FindTokens(locator) is not { } tokens) return false;
        var values = tokens.Length == 1 ? [value] : value.Split('x');
        if (tokens.Length != values.Length || values.Any(part => !Scalar().IsMatch(part))) return false;

        var updated = _text;
        foreach (var (token, replacement) in tokens.Zip(values).OrderByDescending(edit => edit.First.Index))
            updated = string.Concat(updated.AsSpan(0, token.Index), replacement, updated.AsSpan(token.Index + token.Length));
        _document = Parse(updated);
        _text = updated;
        return true;
    }

    public byte[] ToBytes() => [.. _bom, .. _encoding.GetBytes(_text)];

    // A paired locator records both dimensions as one change so undo detects conflicts in either dimension.
    Group[]? FindTokens(string locator)
    {
        return locator.Split('|') switch
        {
            ["xml", var root, var key] when Find(root, key) is { } token => [token],
            ["xml", var root, var first, var second] when first != second &&
                Find(root, first) is { } left && Find(root, second) is { } right => [left, right],
            _ => null,
        };
    }

    Group? Find(string rootName, string key)
    {
        if (_document.Root is not { } root || root.Name.Namespace != XNamespace.None ||
            root.Name.LocalName != rootName ||
            root.Elements().Where(element => element.Name.Namespace == XNamespace.None &&
                element.Name.LocalName == key).ToArray() is not [var element] ||
            element.HasAttributes || element.Nodes().ToArray() is not [XText text] || text is XCData)
            return null;

        var line = (IXmlLineInfo)element;
        int offset = 0;
        for (int number = 1; number < line.LineNumber; number++)
        {
            while (offset < _text.Length && _text[offset] is not ('\r' or '\n')) offset++;
            if (offset < _text.Length && _text[offset++] == '\r' && offset < _text.Length && _text[offset] == '\n')
                offset++;
        }
        // The reported column points to the element name, one character after '<'.
        offset += line.LinePosition - 2;
        if (offset < 0 || offset >= _text.Length) return null;

        var tag = Regex.Escape(key);
        var literal = new Regex(@"\G<" + tag + @"[ \t\r\n]*>[ \t\r\n]*(?<value>[A-Za-z0-9_-]+)[ \t\r\n]*</" +
            tag + @"[ \t\r\n]*>", RegexOptions.CultureInvariant);
        var match = literal.Match(_text, offset);
        var value = match.Groups["value"];
        return match.Success && value.Value == text.Value.Trim() ? value : null;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Scalar();
}
