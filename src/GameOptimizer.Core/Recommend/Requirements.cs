using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GameOptimizer.Core.Recommend;

/// <summary>What a game's publisher lists, reduced to what the recommendation needs.</summary>
public sealed record GameRequirements(string Source, string? RecommendedGpu, string? MinimumGpu, int? ReleaseYear)
{
    /// <summary>When the answer was fetched (cache file time); null when unknown.</summary>
    public DateTime? FetchedUtc { get; init; }
}

/// <summary>
/// Reads published system requirements from Steam's store API and PCGamingWiki. Only Steam app ids
/// are sent. Answers are cached for a week in %LOCALAPPDATA%\GameOpt\cache, in the same file layout
/// as Game Spec Check, whose cache folders are also read (never written) so games it already looked
/// up are not fetched again.
/// </summary>
public static partial class RequirementSource
{
    static readonly TimeSpan GameTtl = TimeSpan.FromDays(7), MapTtl = TimeSpan.FromDays(30);
    static readonly HttpClient Http = CreateClient();
    static readonly Host Steam = new(TimeSpan.FromSeconds(1.5)), Pcgw = new(TimeSpan.FromSeconds(0.4));

    public static string CacheDir => Path.Combine(AppPaths.DataDir, "cache");

    /// <summary>Game Spec Check's cache folders: the installed exe's, plus any set in prefs.</summary>
    public static List<string> SharedCacheDirs { get; } = new()
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSpecCheck", "cache"),
    };

    /// <summary>When false, only cached answers are used.</summary>
    public static bool AllowNetwork { get; set; } = true;

    static HttpClient CreateClient()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("GameOptimizer/0.1 (+https://github.com/yourbirthcertificate/game-optimizer)");
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return h;
    }

    /// <summary>Steam first, PCGamingWiki when Steam lists no graphics card. Null when nothing usable was found.</summary>
    public static async Task<GameRequirements?> ForSteamAppAsync(string appId, CancellationToken ct = default)
    {
        int? year = null;
        string? rec = null, min = null;
        var (steam, steamAt) = await CachedAsync($"steamgame2:{appId}", GameTtl, () => FetchSteamAsync(appId, ct), ct);
        if (steam is not null && steam["missing"] is null)
        {
            year = Year(steam["release"]?.GetValue<string>());
            var pc = steam["pc"] as JsonObject;
            rec = SteamGpu(pc?["recommended"]?.GetValue<string>());
            min = SteamGpu(pc?["minimum"]?.GetValue<string>());
            if (rec is not null) return new("Steam", rec, min, year) { FetchedUtc = steamAt };
        }
        var title = (await CachedAsync($"pcgwappid:{appId}", MapTtl, () => FetchPcgwTitleAsync(appId, ct), ct)).node?["title"]?.GetValue<string>();
        if (title is not null)
        {
            var (page, pageAt) = await CachedAsync($"pcgwgame3:{title.ToLowerInvariant()}", GameTtl, () => FetchPcgwPageAsync(title, ct), ct);
            var text = page?["wikitext"]?.GetValue<string>();
            if (text is not null)
            {
                var (r, m) = PcgwGpu(text);
                if (r is not null || m is not null) return new("PCGamingWiki", r, m ?? min, year) { FetchedUtc = pageAt };
            }
        }
        return min is not null || year is not null ? new("Steam", null, min, year) { FetchedUtc = steamAt } : null;
    }

    // ------------------------------------------------------------------ cache

    static string CacheName(string key) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant() + ".json";

    static async Task<(JsonNode? node, DateTime? at)> CachedAsync(string key, TimeSpan ttl, Func<Task<JsonNode?>> fetch, CancellationToken ct)
    {
        var name = CacheName(key);
        foreach (var dir in SharedCacheDirs.Prepend(CacheDir))
        {
            var path = Path.Combine(dir, name);
            try
            {
                if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < ttl)
                    return (JsonNode.Parse(await File.ReadAllTextAsync(path, ct)), File.GetLastWriteTimeUtc(path));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        if (!AllowNetwork) return (null, null);
        JsonNode? value;
        try { value = await fetch(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        { return (null, null); } // offline or the source is down: fall back, do not cache
        if (value is null) return (null, null);
        try
        {
            Directory.CreateDirectory(CacheDir);
            var tmp = Path.Combine(CacheDir, name + ".tmp");
            await File.WriteAllTextAsync(tmp, value.ToJsonString(), ct);
            File.Move(tmp, Path.Combine(CacheDir, name), true);
        }
        catch (IOException) { }
        return (value, DateTime.UtcNow);
    }

    // ------------------------------------------------------------------ fetching

    sealed class Host(TimeSpan interval)
    {
        readonly SemaphoreSlim _gate = new(1, 1);
        DateTime _next = DateTime.MinValue;

        public async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    var wait = _next - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                    _next = DateTime.UtcNow + interval;
                    var resp = await Http.GetAsync(url, ct);
                    if (resp.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) || attempt >= 2)
                        return resp;
                    var retry = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10 * (attempt + 1));
                    _next = DateTime.UtcNow + (retry > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : retry);
                    resp.Dispose();
                }
            }
            finally { _gate.Release(); }
        }
    }

    static async Task<JsonNode?> FetchSteamAsync(string appId, CancellationToken ct)
    {
        using var resp = await Steam.SendAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&cc=US", ct);
        resp.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        var node = root?[appId];
        if (node?["success"]?.GetValue<bool>() != true) return new JsonObject { ["missing"] = true };
        var d = node["data"]!;
        var pc = d["pc_requirements"] as JsonObject;
        // Same shape as Game Spec Check's cache entry, so either tool can read the other's.
        return new JsonObject
        {
            ["name"] = d["name"]?.DeepClone(),
            ["image"] = d["header_image"]?.DeepClone(),
            ["type"] = d["type"]?.DeepClone(),
            ["windows"] = d["platforms"]?["windows"]?.DeepClone() ?? true,
            ["release"] = d["release_date"]?["date"]?.DeepClone(),
            ["pc"] = pc?.DeepClone() ?? new JsonObject(),
        };
    }

    static async Task<JsonNode?> FetchPcgwTitleAsync(string appId, CancellationToken ct)
    {
        // The wiki's Cargo query API maps a Steam app id to its page. The appid.php redirect is the fallback.
        try
        {
            var where = Uri.EscapeDataString($"Infobox_game.Steam_AppID HOLDS \"{appId}\"");
            using var q = await Pcgw.SendAsync("https://www.pcgamingwiki.com/w/api.php?action=cargoquery&format=json&limit=1" +
                                               "&tables=Infobox_game&fields=Infobox_game._pageName%3DPage&where=" + where, ct);
            if (q.IsSuccessStatusCode && JsonNode.Parse(await q.Content.ReadAsStringAsync(ct))?["cargoquery"] is JsonArray rows)
                return new JsonObject { ["title"] = rows.Count > 0 ? rows[0]?["title"]?["Page"]?.GetValue<string>() : null };
        }
        catch (JsonException) { }
        using var resp = await Pcgw.SendAsync($"https://www.pcgamingwiki.com/api/appid.php?appid={appId}", ct);
        // Only a redirect or a plain answer means "this is (or is not) the page". A refusal or a server
        // error must not be cached as "no page", so it counts as a failed lookup.
        int code = (int)resp.StatusCode;
        if (code is not ((>= 200 and < 400) or 404))
            throw new HttpRequestException($"PCGamingWiki answered {code}", null, resp.StatusCode);
        var loc = resp.Headers.Location?.ToString() ?? "";
        int at = loc.IndexOf("/wiki/", StringComparison.Ordinal);
        string? title = at < 0 ? null : Uri.UnescapeDataString(loc[(at + 6)..]).Replace('_', ' ');
        if (title is not null && title.StartsWith("Special:", StringComparison.OrdinalIgnoreCase)) title = null;
        return new JsonObject { ["title"] = title };
    }

    static async Task<JsonNode?> FetchPcgwPageAsync(string title, CancellationToken ct)
    {
        var url = "https://www.pcgamingwiki.com/w/api.php?action=parse&prop=wikitext&redirects=1&format=json&page=" + Uri.EscapeDataString(title);
        using var resp = await Pcgw.SendAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var data = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (data?["parse"] is not JsonObject parse) return new JsonObject { ["missing"] = true };
        var text = parse["wikitext"]?["*"]?.GetValue<string>() ?? "";
        var m = InfoboxAppId().Match(text);
        return new JsonObject
        {
            ["name"] = parse["title"]?.DeepClone() ?? title,
            ["image"] = null,
            ["steam_appid"] = m.Success ? m.Groups[1].Value : null,
            ["wikitext"] = string.Join("\n", PcgwBlock().Matches(text).Select(b => b.Value)),
        };
    }

    // ------------------------------------------------------------------ parsing

    static int? Year(string? date)
    {
        var m = date is null ? null : YearRx().Match(date);
        return m is { Success: true } ? int.Parse(m.Value) : null;
    }

    /// <summary>The graphics line of one Steam requirements block (HTML).</summary>
    public static string? SteamGpu(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        foreach (Match m in SteamField().Matches(html))
        {
            var label = Clean(m.Groups[1].Value).Trim(' ', '*', ':').ToLowerInvariant();
            if (label is "graphics" or "video card" or "video" or "graphics card" or "gpu")
            {
                var v = Clean(m.Groups[2].Value).Trim();
                if (v.Length > 0) return v;
            }
        }
        // Older listings: plain "Graphics: ..." lines.
        foreach (var line in WebUtility.HtmlDecode(TagRx().Replace(BrRx().Replace(html, "\n"), "")).Split('\n'))
        {
            var lm = PlainLine().Match(line.Trim());
            if (lm.Success && lm.Groups[1].Value.Trim().ToLowerInvariant() is "graphics" or "video card" or "video" or "graphics card" or "gpu")
                return lm.Groups[2].Value.Trim();
        }
        return null;
    }

    static string Clean(string s) => WebUtility.HtmlDecode(TagRx().Replace(BrRx().Replace(s, "\n"), "")).Replace('\n', ' ').Trim();

    /// <summary>(recommended, minimum) graphics text from PCGamingWiki's Windows requirements template.</summary>
    public static (string? rec, string? min) PcgwGpu(string wikitext)
    {
        foreach (Match block in PcgwBlock().Matches(wikitext))
        {
            var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in Regex.Split("\n" + block.Groups[1].Value, @"\n\s*\|"))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) p[line[..eq].Trim()] = WikiClean(line[(eq + 1)..]);
            }
            if (!(p.GetValueOrDefault("OSfamily") ?? "").Contains("windows", StringComparison.OrdinalIgnoreCase)) continue;
            string? Alts(string prefix)
            {
                var vals = new[] { "", "2", "3" }.Select(n => p.GetValueOrDefault(prefix + "GPU" + n) ?? "").Where(v => v.Length > 0).ToList();
                return vals.Count > 0 ? string.Join(" / ", vals) : null;
            }
            return (Alts("rec"), Alts("min"));
        }
        return (null, null);
    }

    static string WikiClean(string v)
    {
        v = Regex.Replace(v, "<ref[^>]*/>", "");
        v = Regex.Replace(v, "<ref[^>]*>.*?</ref>", "", RegexOptions.Singleline);
        v = Regex.Replace(v, @"\{\{[^{}]*\}\}", "");
        v = Regex.Replace(v, @"\[\[(?:[^|\]]*\|)?([^\]]*)\]\]", "$1");
        v = Regex.Replace(v, @"\[https?://\S+\s+([^\]]*)\]", "$1");
        v = v.Replace("'''", "").Replace("''", "");
        return WebUtility.HtmlDecode(TagRx().Replace(v, "")).Trim();
    }

    [GeneratedRegex(@"<strong>\s*([^<]{1,40}?)\s*\*?\s*:\s*\*?\s*</strong>(.*?)(?=<strong>|</li>|</ul>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SteamField();
    [GeneratedRegex(@"^(?:required|minimum|recommended)?\s*([A-Za-z][A-Za-z ]{1,24}?)\s*[®™]?\s*:\s*(\S.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlainLine();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex TagRx();
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex BrRx();
    [GeneratedRegex(@"\{\{System requirements(.*?)\n\}\}", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex PcgwBlock();
    [GeneratedRegex(@"\|\s*steam appid\s*=\s*([0-9]+)", RegexOptions.IgnoreCase)] private static partial Regex InfoboxAppId();
    [GeneratedRegex(@"\b(19[89]\d|20\d\d)\b")] private static partial Regex YearRx();
}
