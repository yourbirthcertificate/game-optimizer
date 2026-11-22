using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace OptiGame.Core.Formats;

/// <summary>
/// Unreal Engine SaveGame (GVAS) file: header kept as raw bytes, top-level properties parsed as tags with
/// opaque values. Supports the classic tag layout (UE4 to UE5.3) and the "complete type name" layout
/// from UE5.4 on, where bools live in the tag flags byte (0x10 = true). Load only succeeds when writing
/// the parsed file back reproduces the original bytes exactly, so edits never rest on a guess.
/// </summary>
public sealed partial class GvasFile
{
    public int SaveGameVersion { get; private set; }
    public int UE4Version { get; private set; }
    public int UE5Version { get; private set; }
    public string EngineVersion { get; private set; } = "";
    public string SaveClass { get; private set; } = "";
    public bool CompleteTypeNames { get; private set; }
    public List<GvasProperty> Properties { get; } = new();

    byte[] _header = Array.Empty<byte>();
    byte[] _trailer = Array.Empty<byte>(); // "None" terminator and everything after it

    public static GvasFile Load(byte[] data)
    {
        Exception? last = null;
        foreach (var (control, complete) in Layouts(data))
        {
            try
            {
                var f = new GvasFile();
                f.Parse(data, control, complete);
                if (!f.Save().AsSpan().SequenceEqual(data))
                    throw new FormatException("Re-saving the parsed file does not reproduce it byte for byte.");
                return f;
            }
            catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException)
            {
                last = e;
            }
        }
        throw new FormatException("Not a save layout this tool can edit safely: " + last?.Message, last);
    }

    /// <summary>Candidate (has control-extension byte, complete type names) layouts, most likely first.</summary>
    static IEnumerable<(bool, bool)> Layouts(byte[] data)
    {
        int ue5 = 0;
        if (data.Length > 16 && BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)) >= 3)
            ue5 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        var first = (ue5 >= 1011, ue5 >= 1012);
        yield return first;
        foreach (var l in new[] { (true, true), (false, false), (true, false) })
            if (l != first) yield return l;
    }

    void Parse(byte[] data, bool controlByte, bool complete)
    {
        var r = new Reader(data);
        if (Encoding.ASCII.GetString(r.Bytes(4)) != "GVAS") throw new FormatException("Missing GVAS magic.");
        SaveGameVersion = r.I32();
        UE4Version = r.I32();
        if (SaveGameVersion >= 3) UE5Version = r.I32();
        int maj = r.U16(), min = r.U16(), pat = r.U16();
        r.I32(); // changelist
        var branch = r.FStr();
        EngineVersion = $"{maj}.{min}.{pat} {branch}".Trim();
        int cvFormat = r.I32();
        if (cvFormat != 3) throw new FormatException($"Unsupported custom-version format {cvFormat}.");
        int count = r.I32();
        if (count is < 0 or > 10000) throw new FormatException("Bad custom-version count.");
        r.Bytes(count * 20);
        SaveClass = r.FStr() ?? "";
        if (controlByte && r.U8() != 0) throw new FormatException("Unsupported serialization-control extension.");
        CompleteTypeNames = complete;
        _header = data[..r.Pos];

        while (true)
        {
            int start = r.Pos;
            var name = r.FStr();
            if (name == "None") { _trailer = data[start..]; break; }
            if (string.IsNullOrEmpty(name)) throw new FormatException("Empty property name.");
            var nameRaw = data[start..r.Pos];
            Properties.Add(complete ? ReadComplete(r, data, name, nameRaw) : ReadClassic(r, data, name, nameRaw));
        }
        if (_trailer.Length > 64 && _trailer.Skip(9).Any(b => b != 0))
            throw new FormatException("Unexpected data after the property list.");
    }

    static GvasProperty ReadComplete(Reader r, byte[] data, string name, byte[] nameRaw)
    {
        int a = r.Pos;
        var type = ReadTypeName(r, 0);
        var pre = data[a..r.Pos];
        int size = r.I32();
        int b = r.Pos;
        byte flags = r.U8();
        if ((flags & ~0x3F) != 0) throw new FormatException($"Unknown tag flags 0x{flags:X2} on {name}.");
        if ((flags & 0x01) != 0) r.I32();
        if ((flags & 0x02) != 0) r.Bytes(16);
        if ((flags & 0x04) != 0)
        {
            byte ext = r.U8();
            if ((ext & ~0x02) != 0) throw new FormatException($"Unknown tag extension 0x{ext:X2} on {name}.");
            if ((ext & 0x02) != 0) r.U8();
        }
        var post = data[b..r.Pos];
        var value = r.Bytes(size);
        return new GvasProperty(name, nameRaw, type.Name, type.Params.FirstOrDefault()?.Name, pre, post, value, complete: true);
    }

    sealed record TypeNode(string Name, List<TypeNode> Params);

    static TypeNode ReadTypeName(Reader r, int depth)
    {
        if (depth > 8) throw new FormatException("Type name nested too deeply.");
        var n = r.FStr() ?? throw new FormatException("Null type name.");
        int count = r.I32();
        if (count is < 0 or > 16) throw new FormatException("Bad type parameter count.");
        var ps = new List<TypeNode>();
        for (int i = 0; i < count; i++) ps.Add(ReadTypeName(r, depth + 1));
        return new TypeNode(n, ps);
    }

    static readonly HashSet<string> PlainTypes = new()
    {
        "IntProperty", "Int8Property", "Int16Property", "Int64Property", "UInt16Property", "UInt32Property", "UInt64Property",
        "FloatProperty", "DoubleProperty", "StrProperty", "NameProperty", "TextProperty", "ObjectProperty",
        "SoftObjectProperty", "ClassProperty", "SoftClassProperty", "InterfaceProperty",
    };

    static GvasProperty ReadClassic(Reader r, byte[] data, string name, byte[] nameRaw)
    {
        int a = r.Pos;
        var type = r.FStr() ?? throw new FormatException("Null type.");
        var pre = data[a..r.Pos];
        int size = r.I32();
        int b = r.Pos;
        r.I32(); // array index
        string? sub = null;
        switch (type)
        {
            case "StructProperty": sub = r.FStr(); r.Bytes(16); break;
            case "BoolProperty": r.U8(); break;
            case "ByteProperty": case "EnumProperty": sub = r.FStr(); break;
            case "ArrayProperty": case "SetProperty": sub = r.FStr(); break;
            case "MapProperty": sub = r.FStr(); r.FStr(); break;
            default:
                if (!PlainTypes.Contains(type)) throw new FormatException($"Unknown property type {type} on {name}.");
                break;
        }
        if (r.U8() != 0) r.Bytes(16); // property guid
        var post = data[b..r.Pos];
        var value = r.Bytes(size);
        return new GvasProperty(name, nameRaw, type, sub, pre, post, value, complete: false);
    }

    public byte[] Save()
    {
        using var ms = new MemoryStream();
        ms.Write(_header);
        Span<byte> i32 = stackalloc byte[4];
        foreach (var p in Properties)
        {
            ms.Write(p.NameRaw);
            ms.Write(p.Pre);
            BinaryPrimitives.WriteInt32LittleEndian(i32, p.Value.Length);
            ms.Write(i32);
            ms.Write(p.Post);
            ms.Write(p.Value);
        }
        ms.Write(_trailer);
        return ms.ToArray();
    }

    /// <summary>Exact name, or a Blueprint-mangled name such as "VSync_12_0123456789ABCDEF0123456789ABCDEF".</summary>
    public GvasProperty? Find(string name)
    {
        var exact = Properties.FirstOrDefault(p => p.Name == name);
        if (exact is not null) return exact;
        var hits = Properties.Where(p => p.Name.StartsWith(name + "_", StringComparison.Ordinal) &&
                                         MangledSuffix().IsMatch(p.Name[(name.Length)..])).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    [GeneratedRegex(@"^_\d+_[0-9A-F]{32}$")] private static partial Regex MangledSuffix();

    internal sealed class Reader(byte[] d)
    {
        public int Pos;
        public byte[] Bytes(int n)
        {
            if (n < 0 || Pos + n > d.Length) throw new EndOfStreamException("Unexpected end of save file.");
            var b = d[Pos..(Pos + n)]; Pos += n; return b;
        }
        public byte U8() => Bytes(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));
        public string? FStr()
        {
            int len = I32();
            if (len == 0) return null;
            if (len > 0)
            {
                if (len > 65536) throw new FormatException("String too long.");
                var b = Bytes(len);
                if (b[^1] != 0) throw new FormatException("String not null-terminated.");
                return Encoding.Latin1.GetString(b, 0, len - 1);
            }
            if (len < -65536) throw new FormatException("String too long.");
            var u = Bytes(-len * 2);
            return Encoding.Unicode.GetString(u, 0, u.Length - 2);
        }
    }
}

public sealed class GvasProperty
{
    public string Name { get; }
    public string Type { get; }
    /// <summary>Struct name (e.g. IntPoint) or enum name (e.g. EWindowMode), when the tag has one.</summary>
    public string? SubType { get; }
    internal byte[] NameRaw { get; }
    internal byte[] Pre { get; }
    internal byte[] Post { get; private set; }
    internal byte[] Value { get; private set; }
    readonly bool _complete;

    internal GvasProperty(string name, byte[] nameRaw, string type, string? sub, byte[] pre, byte[] post, byte[] value, bool complete)
    { Name = name; NameRaw = nameRaw; Type = type; SubType = sub; Pre = pre; Post = post; Value = value; _complete = complete; }

    bool IsEnumByte => Type == "ByteProperty" && SubType is not null && SubType != "None";

    /// <summary>Current value as a plain object: int, long, double, bool, string or int[2]. Null when not editable.</summary>
    public object? Get()
    {
        try
        {
            return Type switch
            {
                "IntProperty" when Value.Length == 4 => BinaryPrimitives.ReadInt32LittleEndian(Value),
                "Int64Property" when Value.Length == 8 => BinaryPrimitives.ReadInt64LittleEndian(Value),
                "FloatProperty" when Value.Length == 4 => (double)BinaryPrimitives.ReadSingleLittleEndian(Value),
                "DoubleProperty" when Value.Length == 8 => BinaryPrimitives.ReadDoubleLittleEndian(Value),
                "BoolProperty" when Value.Length == 0 => _complete ? (Post[0] & 0x10) != 0 : Post[4] != 0,
                "ByteProperty" when !IsEnumByte && Value.Length == 1 => (int)Value[0],
                "ByteProperty" or "EnumProperty" or "StrProperty" or "NameProperty" => ReadFString(Value),
                "StructProperty" when SubType == "IntPoint" && Value.Length == 8 =>
                    new[] { BinaryPrimitives.ReadInt32LittleEndian(Value), BinaryPrimitives.ReadInt32LittleEndian(Value.AsSpan(4)) },
                _ => null,
            };
        }
        catch (FormatException) { return null; }
    }

    /// <summary>Set a value of the matching kind. Returns false (and changes nothing) on a kind mismatch.</summary>
    public bool TrySet(object value, out string? error)
    {
        error = null;
        switch (Type)
        {
            case "IntProperty" when TryLong(value, out var l) && l is >= int.MinValue and <= int.MaxValue:
                Value = BitConverter.GetBytes((int)l); return true;
            case "Int64Property" when TryLong(value, out var l):
                Value = BitConverter.GetBytes(l); return true;
            case "FloatProperty" when TryDouble(value, out var d):
                Value = BitConverter.GetBytes((float)d); return true;
            case "DoubleProperty" when TryDouble(value, out var d):
                Value = BitConverter.GetBytes(d); return true;
            case "BoolProperty" when value is bool bv:
                var post = (byte[])Post.Clone();
                if (_complete) post[0] = (byte)(bv ? post[0] | 0x10 : post[0] & ~0x10);
                else post[4] = (byte)(bv ? 1 : 0);
                Post = post; return true;
            case "ByteProperty" when !IsEnumByte && TryLong(value, out var l) && l is >= 0 and <= 255:
                Value = new[] { (byte)l }; return true;
            case "ByteProperty" or "EnumProperty" when IsEnumOrEnumProp && value is string s && s.Contains("::"):
                Value = WriteFString(s); return true;
            case "StrProperty" or "NameProperty" when value is string s:
                Value = WriteFString(s); return true;
            case "StructProperty" when SubType == "IntPoint" && value is int[] { Length: 2 } xy:
                Value = BitConverter.GetBytes(xy[0]).Concat(BitConverter.GetBytes(xy[1])).ToArray(); return true;
        }
        error = $"{Name} is {Type}{(SubType is null ? "" : "<" + SubType + ">")}, which does not take {Describe(value)}";
        return false;
    }

    bool IsEnumOrEnumProp => Type == "EnumProperty" || IsEnumByte;

    static string Describe(object v) => v switch { int[] a => $"[{string.Join(",", a)}]", string s => $"\"{s}\"", _ => v.ToString() ?? "null" };
    static bool TryLong(object v, out long l)
    {
        switch (v) { case int i: l = i; return true; case long x: l = x; return true; case double d when d == Math.Floor(d): l = (long)d; return true; }
        l = 0; return false;
    }
    static bool TryDouble(object v, out double d)
    {
        switch (v) { case int i: d = i; return true; case long x: d = x; return true; case double y: d = y; return true; }
        d = 0; return false;
    }

    static string ReadFString(byte[] b)
    {
        var r = new GvasFile.Reader(b);
        var s = r.FStr() ?? "";
        if (r.Pos != b.Length) throw new FormatException("Trailing bytes after string value.");
        return s;
    }

    static byte[] WriteFString(string s)
    {
        if (s.Length == 0) return new byte[4];
        bool ascii = s.All(c => c < 128);
        var body = ascii ? Encoding.ASCII.GetBytes(s + "\0") : Encoding.Unicode.GetBytes(s + "\0");
        int len = ascii ? body.Length : -(body.Length / 2);
        return BitConverter.GetBytes(len).Concat(body).ToArray();
    }

    public override string ToString() => $"{Name} ({Type}) = {Describe(Get() ?? "?")}";
}
