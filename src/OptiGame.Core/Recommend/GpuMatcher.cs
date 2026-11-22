using System.Text.Json;
using System.Text.RegularExpressions;

namespace OptiGame.Core.Recommend;

/// <summary>A graphics card found in text, with a speed score on the shared PassMark-like scale.</summary>
public sealed record GpuMatch(string Name, int Score, string Vendor, string Kind);

/// <summary>
/// Turns free-text card names ("NVIDIA GeForce RTX 3060 Laptop GPU", "GTX 970 / R9 290 or better")
/// into entries of data/gpus.json. C# port of Game Spec Check's matcher: both sides are reduced to
/// the same token stream and the longest database entry that appears contiguously wins.
/// </summary>
public static partial class GpuMatcher
{
    sealed record Entry(string Name, int Score, string Vendor);

    static readonly HashSet<string> GpuStop = new() { "graphics", "gpu", "tm" };
    static readonly HashSet<string> NvFamily = new() { "gtx", "rtx", "gt", "gts", "mx", "titan" };
    static readonly HashSet<string> AmdFamily = new() { "rx", "r", "hd", "vega" };
    static readonly HashSet<string> LaptopToks = new() { "laptop", "mobile", "notebook", "maxq" };
    static readonly HashSet<string> AltBreak = new() { "or", "and", "desktop", "vs", "versus" };

    static readonly Dictionary<string, List<(string[] key, Entry e)>> Index = new();
    static readonly Dictionary<string, Entry> Exact = new();
    static readonly List<(Regex rx, string label, int score)> Generic = new();

    static GpuMatcher()
    {
        using var s = typeof(GpuMatcher).Assembly.GetManifestResourceStream("OptiGame.Core.data.gpus.json")
                      ?? throw new InvalidOperationException("gpus.json is not embedded");
        using var doc = JsonDocument.Parse(s);
        foreach (var g in doc.RootElement.GetProperty("gpus").EnumerateArray())
        {
            var name = g[0].GetString()!;
            var e = new Entry(name, g[1].GetInt32(), VendorOf(name));
            foreach (var key in Keys(name))
            {
                if (!Index.TryGetValue(key[0], out var list)) Index[key[0]] = list = new();
                list.Add((key, e));
                Exact[string.Join(' ', key)] = e;
            }
        }
        foreach (var list in Index.Values) list.Sort((a, b) => b.key.Length.CompareTo(a.key.Length));
        foreach (var g in doc.RootElement.GetProperty("generic").EnumerateArray())
            Generic.Add((new Regex(g[0].GetString()!, RegexOptions.CultureInvariant), g[1].GetString()!, g[2].GetInt32()));
    }

    public static int Count => Exact.Count;

    static string VendorOf(string name) =>
        name.StartsWith("GeForce") || name.StartsWith("Titan") ? "nvidia" : name.StartsWith("Radeon") ? "amd" : "intel";

    public static List<string> Tokens(string? text, ISet<string>? stop = null)
    {
        var t = Marks().Replace(text ?? "", " ").ToLowerInvariant();
        return TokenRx().Matches(t).Select(m => m.Value).Where(x => stop is null || !stop.Contains(x)).ToList();
    }

    static IEnumerable<string[]> Keys(string name)
    {
        var t = Tokens(name, GpuStop);
        if (t.Count > 1 && ((t[0] == "geforce" && NvFamily.Contains(t[1])) || (t[0] == "radeon" && AmdFamily.Contains(t[1]))))
            t.RemoveAt(0);
        yield return t.ToArray();
        if (t.Count > 2 && t[0] == "rx" && t[1] == "vega") yield return t.Skip(1).ToArray();
    }

    static List<(int s, int e, Entry entry)> Scan(List<string> toks)
    {
        var hits = new List<(int, int, Entry)>();
        int i = 0;
        while (i < toks.Count)
        {
            (int, int, Entry)? hit = null;
            if (Index.TryGetValue(toks[i], out var list))
                foreach (var (key, e) in list)
                    if (i + key.Length <= toks.Count && key.SequenceEqual(toks.Skip(i).Take(key.Length)))
                    { hit = (i, i + key.Length, e); break; }
            if (hit is { } h) { hits.Add(h); i = h.Item2; }
            else i++;
        }
        return hits;
    }

    static double LaptopFactor(Entry e)
    {
        var m = Regex.Match(e.Name, @"\d{3,4}");
        int num = m.Success ? int.Parse(m.Value) : 0;
        if (e.Vendor == "nvidia")
        {
            if (e.Name.Contains("RTX")) return (num % 100) switch { 90 => 0.72, 80 => 0.77, 70 => 0.80, 60 => 0.87, _ => 0.85 };
            return num >= 1000 ? 0.9 : 0.6;
        }
        return 0.88;
    }

    static bool LaptopNear(List<string> toks, int s, int e, int prevEnd, int nextStart)
    {
        for (int j = e; j < Math.Min(nextStart, e + 4); j++)
        {
            var t = toks[j];
            if (AltBreak.Contains(t)) break;
            if (LaptopToks.Contains(t) || (t == "max" && j + 1 < toks.Count && toks[j + 1] == "q")) return true;
        }
        for (int j = s - 1; j >= Math.Max(prevEnd, s - 2); j--)
        {
            var t = toks[j];
            if (AltBreak.Contains(t)) break;
            if (LaptopToks.Contains(t)) return true;
        }
        return false;
    }

    static bool IsNum(string t) => t.Length is >= 3 and <= 4 && t.All(char.IsAsciiDigit);

    static GpuMatch? SeriesHit(List<string> toks)
    {
        for (int i = 0; i < toks.Count; i++)
        {
            if (!SeriesNum().IsMatch(toks[i])) continue;
            var nxt = i + 1 < toks.Count ? toks[i + 1] : "";
            var prev = i > 0 ? toks[i - 1] : "";
            string[] fams = { "gtx", "rtx", "gt", "gts", "rx", "hd" };
            if (nxt != "series" && !fams.Contains(prev)) continue;
            int lo = int.Parse(toks[i]), span = (int)Math.Pow(10, toks[i].Length - 1);
            var prefixes = fams.Contains(prev) ? new[] { prev } : new[] { "gtx", "rtx", "rx", "hd", "gt" };
            foreach (var p in prefixes)
            {
                var pool = Exact.Where(kv => kv.Key.Split(' ') is [var a, var b] && a == p && b.All(char.IsAsciiDigit)
                                             && int.Parse(b) >= lo && int.Parse(b) < lo + span)
                                .Select(kv => kv.Value).Distinct().ToList();
                if (pool.Count == 0) continue;
                var gaming = pool.Where(x => int.Parse(Regex.Match(x.Name, @"\d+").Value) % 100 >= 50).ToList();
                var e = (gaming.Count > 0 ? gaming : pool).MinBy(x => x.Score)!;
                return new GpuMatch($"{e.Name} (entry card of the {p.ToUpperInvariant()} {toks[i]} series)", e.Score, e.Vendor, "generic");
            }
        }
        return null;
    }

    /// <summary>Every graphics card named in the text.</summary>
    public static List<GpuMatch> Match(string? text)
    {
        var toks = Tokens(text, GpuStop);
        var hits = Scan(toks);
        var covered = new HashSet<int>(hits.SelectMany(h => Enumerable.Range(h.s, h.e - h.s)));

        // "GeForce 1060" / "Radeon 580": a bare model number after a vendor word.
        string? vendor = null;
        for (int i = 0; i < toks.Count; i++)
        {
            var tok = toks[i];
            if (tok is "nvidia" or "geforce" or "gtx" or "rtx") vendor = "nvidia";
            else if (tok is "amd" or "radeon" or "ati" or "rx") vendor = "amd";
            if (covered.Contains(i) || vendor is null || !IsNum(tok)) continue;
            var nxt = i + 1 < toks.Count ? toks[i + 1] : "";
            var prev = i > 0 ? toks[i - 1] : "";
            var after = i + 2 < toks.Count ? toks[i + 2] : "";
            if (nxt is "p" or "mb" or "gb" or "hz" or "fps" or "mhz" || (nxt == "x" && after.All(char.IsAsciiDigit) && after.Length > 0) ||
                (prev == "x" && i > 1 && toks[i - 2].All(char.IsAsciiDigit))) continue;
            string[][] prefixes = vendor == "nvidia"
                ? new[] { new[] { "gtx" }, new[] { "rtx" }, new[] { "gt" }, new[] { "gts" } }
                : new[] { new[] { "rx" }, new[] { "hd" }, new[] { "r", "9" }, new[] { "r", "7" } };
            var tail = toks.Skip(i + 1).Take(2).ToList();
            Entry? found = null;
            for (int n = 2; n >= 0 && found is null; n--)
                foreach (var p in prefixes)
                {
                    var key = string.Join(' ', p.Append(tok).Concat(tail.Take(n)));
                    if (Exact.TryGetValue(key, out found)) { hits.Add((i, i + 1 + Math.Min(n, tail.Count), found)); break; }
                }
        }

        hits.Sort((a, b) => a.s.CompareTo(b.s));
        var outList = new List<GpuMatch>();
        for (int n = 0; n < hits.Count; n++)
        {
            var (s, e, entry) = hits[n];
            int prevEnd = n > 0 ? hits[n - 1].e : 0;
            int nextStart = n + 1 < hits.Count ? hits[n + 1].s : toks.Count;
            bool laptop = (e < toks.Count && toks[e] == "m") || LaptopNear(toks, s, e, prevEnd, nextStart);
            if (laptop && !NoLaptopFactor().IsMatch(entry.Name))
                outList.Add(new GpuMatch(entry.Name + " (laptop)", (int)Math.Round(entry.Score * LaptopFactor(entry)), entry.Vendor, "variant"));
            else outList.Add(new GpuMatch(entry.Name, entry.Score, entry.Vendor, "exact"));
        }
        if (outList.Count > 0) return outList;

        if (SeriesHit(toks) is { } series) return new() { series };

        var low = Marks().Replace(text ?? "", "").ToLowerInvariant();
        foreach (var (rx, label, score) in Generic)
            if (rx.IsMatch(low))
                return new() { new GpuMatch(label, score, label.Contains("Intel") ? "intel" : label.Contains("AMD") ? "amd" : "", "generic") };
        return new();
    }

    /// <summary>The bar to clear when a requirement lists alternatives: same vendor as ours if listed, else the easiest.</summary>
    public static GpuMatch? PickRequirement(List<GpuMatch> cands, string? localVendor)
    {
        var pool = cands.Where(c => c.Kind != "generic").ToList();
        if (pool.Count == 0) pool = cands;
        if (pool.Count == 0) return null;
        var same = pool.Where(c => localVendor is not null && c.Vendor == localVendor).ToList();
        return (same.Count > 0 ? same : pool).MinBy(c => c.Score);
    }

    /// <summary>Best reading of a local adapter name.</summary>
    public static GpuMatch? Identify(string name) => Match(name).MaxBy(c => c.Score);

    [GeneratedRegex(@"[a-z]+|\d+(?:\.\d+)?")] private static partial Regex TokenRx();
    [GeneratedRegex(@"\((?:r|tm|c)\)|[®™©]", RegexOptions.IgnoreCase)] private static partial Regex Marks();
    [GeneratedRegex(@"^[1-9]0{2,3}$")] private static partial Regex SeriesNum();
    [GeneratedRegex(@"\bMX\d|\d{3}MX|RTX 4050|RTX 3050 Ti|RTX 2050|GTX 1650 Ti|Radeon \d{3}M|Vega (3|6|8|10|11)$|Iris|UHD|^HD |Arc 1\d\d|GMA", RegexOptions.IgnoreCase)]
    private static partial Regex NoLaptopFactor();
}
