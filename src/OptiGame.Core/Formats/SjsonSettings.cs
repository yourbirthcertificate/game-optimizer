using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OptiGame.Core.Formats;

internal sealed class SjsonSettingsHandler : IFormatHandler
{
    public ISettingsDocument Read(byte[] bytes) => new SjsonSettings(bytes);
}

/// <summary>Reads the assignment-based config and edits only unique, literal root booleans.</summary>
internal sealed partial class SjsonSettings : ISettingsDocument
{
    internal const string Format = "sjson";
    static readonly UTF8Encoding Utf8 = new(false, true);
    readonly byte[] _bom;
    Dictionary<string, Token?> _fields;
    string _text;

    public SjsonSettings(byte[] bytes)
    {
        if (bytes.Length > 2 * 1024 * 1024) throw new FormatException("The config exceeds the settings size limit.");
        int offset = bytes.AsSpan().StartsWith("\uFEFF"u8) ? 3 : 0;
        _bom = bytes[..offset];
        try { _text = Utf8.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException ex) { throw new FormatException("The config is not valid UTF-8.", ex); }
        _fields = new Reader(_text).ReadRoot();
    }

    public bool HasObject(string key) => _fields.GetValueOrDefault(key) is { Text: "{" };

    public string? GetRaw(string locator) => Boolean(locator)?.Text;

    public bool TrySet(string locator, string value)
    {
        if (value is not ("true" or "false") || Boolean(locator) is not { } token) return false;
        var updated = string.Concat(_text.AsSpan(0, token.Offset), value, _text.AsSpan(token.Offset + token.Text.Length));
        _fields = new Reader(updated).ReadRoot();
        _text = updated;
        return true;
    }

    public byte[] ToBytes() => [.. _bom, .. Utf8.GetBytes(_text)];

    Token? Boolean(string locator) =>
        locator.Split('|') is [Format, var key] && _fields.GetValueOrDefault(key) is { Text: "true" or "false" } token
            ? token : null;

    enum Kind { End, Name, String, Number, Symbol }
    readonly record struct Token(Kind Kind, int Offset, string Text);

    sealed class Reader
    {
        readonly string _text;
        int _next;
        Token _current = new(Kind.End, 0, "");
        int _previousEnd;

        public Reader(string text) { _text = text; Advance(); }

        public Dictionary<string, Token?> ReadRoot()
        {
            var fields = new Dictionary<string, Token?>(StringComparer.Ordinal);
            while (_current.Kind != Kind.End)
            {
                var (key, token) = ReadAssignment(depth: 0);
                if (!fields.TryAdd(key, token)) fields[key] = null;
                Separate(close: null);
            }
            return fields;
        }

        (string Key, Token Value) ReadAssignment(int depth)
        {
            string key = _current.Kind switch
            {
                Kind.Name => _current.Text,
                Kind.String => DecodeKey(_current.Text),
                _ => throw InvalidConfig(),
            };
            Advance();
            Require("=");
            var value = _current;
            ReadValue(depth);
            return (key, value);
        }

        void ReadValue(int depth)
        {
            if (depth >= 64) throw InvalidConfig();
            if (Eat("{"))
            {
                while (!Eat("}")) { ReadAssignment(depth + 1); Separate("}"); }
            }
            else if (Eat("["))
            {
                while (!Eat("]")) { ReadValue(depth + 1); Separate("]"); }
            }
            else if (_current.Kind is Kind.String or Kind.Number || _current.Text is "true" or "false" or "null")
                Advance();
            else throw InvalidConfig();
        }

        static string DecodeKey(string token)
        {
            try { return JsonSerializer.Deserialize<string>(token)!; }
            catch (JsonException ex) { throw new FormatException("The config contains an unsupported quoted key.", ex); }
        }

        void Require(string symbol)
        {
            if (!Eat(symbol)) throw InvalidConfig();
        }

        bool Eat(string symbol)
        {
            if (_current.Text != symbol) return false;
            Advance();
            return true;
        }

        void Separate(string? close)
        {
            if (Eat(",") || _current.Kind == Kind.End || _current.Text == close) return;
            if (_current.Offset == _previousEnd) throw InvalidConfig();
        }

        void Advance()
        {
            _previousEnd = _current.Offset + _current.Text.Length;
            while (_next < _text.Length)
            {
                var match = Lex().Match(_text, _next);
                if (!match.Success) throw InvalidConfig();
                _next += match.Length;
                if (match.Groups["trivia"].Success) continue;
                var kind = match.Groups["name"].Success ? Kind.Name
                    : match.Groups["quoted"].Success ? Kind.String
                    : match.Groups["number"].Success ? Kind.Number : Kind.Symbol;
                _current = new(kind, match.Index, match.Value);
                return;
            }
            _current = new(Kind.End, _next, "");
        }

        static FormatException InvalidConfig() => new("The config contains malformed or unsupported assignment syntax.");
    }

    [GeneratedRegex("""\G(?:(?<trivia>[ \t\r\n]+|//[^\r\n]*|/\*(?:[^*]|\*(?!/))*\*/)|(?<quoted>"(?:[^"\\\r\n]|\\[^\r\n])*")|(?<name>[A-Za-z_][A-Za-z_0-9]*)|(?<number>-?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?)|[{}\[\]=,])""", RegexOptions.CultureInvariant)]
    private static partial Regex Lex();
}
