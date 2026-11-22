using System.Buffers.Binary;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace OptiGame.Core.Formats;

/// <summary>A Unity game's built-in quality levels, read from its globalgamemanagers file.</summary>
public sealed record UnityQualityLevels(IReadOnlyList<string> Names, int DefaultIndex);

public static class UnityQuality
{
    /// <summary>
    /// Reads the QualitySettings object. Uses the type tree when the build kept one; otherwise reads the
    /// leading m_CurrentQuality / m_QualitySettings fields directly and accepts the result only if the
    /// count and first level name look sane. Returns null when it cannot tell for sure.
    /// </summary>
    public static UnityQualityLevels? Read(string dataDir, out string? why)
    {
        why = null;
        var path = new[] { "globalgamemanagers", "mainData" }.Select(f => Path.Combine(dataDir, f)).FirstOrDefault(File.Exists);
        if (path is null) { why = "no globalgamemanagers file"; return null; }
        var am = new AssetsManager();
        try
        {
            var inst = am.LoadAssetsFile(path, false);
            var infos = inst.file.GetAssetsOfType(AssetClassID.QualitySettings);
            if (infos.Count != 1) { why = $"found {infos.Count} QualitySettings objects"; return null; }
            var info = infos[0];

            if (inst.file.Metadata.TypeTreeEnabled)
            {
                var bf = am.GetBaseField(inst, info);
                var arr = bf["m_QualitySettings"]["Array"];
                var names = arr.Children.Select(c => c["name"].AsString).ToList();
                int cur = bf["m_CurrentQuality"].AsInt;
                return Check(names, cur, out why);
            }

            var reader = inst.file.Reader;
            if (reader.BigEndian) { why = "big-endian asset data"; return null; }
            reader.Position = info.GetAbsoluteByteOffset(inst.file);
            var raw = reader.ReadBytes((int)Math.Min(info.ByteSize, 1 << 20));
            int current = BinaryPrimitives.ReadInt32LittleEndian(raw);
            int count = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
            if (count is < 1 or > 16) { why = $"implausible level count {count}"; return null; }
            int len = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(8));
            if (len is < 1 or > 40 || 12 + len > raw.Length) { why = "first level name unreadable"; return null; }
            var first = Encoding.UTF8.GetString(raw, 12, len);
            if (!first.All(c => c >= ' ' && c < 127)) { why = "first level name unreadable"; return null; }
            // Only the first name can be read without the type tree; the rest are numbered.
            var list = new List<string> { first };
            for (int i = 1; i < count; i++) list.Add($"Level {i}");
            return Check(list, current, out why);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            why = "could not read Unity asset data: " + e.Message;
            return null;
        }
        finally { am.UnloadAll(); }
    }

    static UnityQualityLevels? Check(List<string> names, int cur, out string? why)
    {
        why = null;
        if (names.Count is < 1 or > 16) { why = $"implausible level count {names.Count}"; return null; }
        if (cur < 0 || cur >= names.Count) { why = $"default level {cur} out of range"; return null; }
        return new UnityQualityLevels(names, cur);
    }

    /// <summary>Unity's PlayerPrefs registry value name: key + "_h" + djb2-xor hash of the key.</summary>
    public static string PrefName(string key)
    {
        uint h = 5381;
        foreach (var b in Encoding.UTF8.GetBytes(key)) h = (h * 33) ^ b;
        return $"{key}_h{h}";
    }
}
