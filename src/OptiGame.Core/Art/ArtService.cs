using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using OptiGame.Core.Library;

namespace OptiGame.Core.Art;

public sealed record ArtResult(string Path, string Source);

public enum ArtSlot { Cover, SmallIcon }

/// <summary>
/// Game art for the list and detail header. Order: your own image, the cache, Steam's local library cache (no network),
/// then (when network art is allowed) Epic's catalog image, GOG's product lookup, Steam's image server by app id, a
/// Steam store search by exact title, and SteamGridDB when a key is set. The game's own exe icon is the last resort
/// and is read by the app, not here. Only ids go out, or the title for a game that has no id. Images are accepted only
/// from the known image hosts and only when the bytes are a JPEG, PNG or WebP.
/// </summary>
public static class ArtService
{
    public static string Dir => Path.Combine(AppPaths.DataDir, "art");
    static readonly string[] AllowedHosts = { "epicgames.com", "gog.com", "steamstatic.com", "steamgriddb.com", "akamaihd.net", "gog-statics.com" };
    static readonly TimeSpan MissTtl = TimeSpan.FromDays(7);
    const long MaxBytes = 8 * 1024 * 1024;

    static string Safe(string id) => new string(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
    public static string CachePath(GameInfo g) => Path.Combine(Dir, Safe(g.Id) + ".img");
    public static string UserPath(GameInfo g) => UserPath(g, ArtSlot.Cover);
    public static string UserPath(GameInfo g, ArtSlot slot) =>
        Path.Combine(Dir, Safe(g.Id) + (slot == ArtSlot.SmallIcon ? ".icon.user.img" : ".user.img"));
    static string MissPath(GameInfo g) => Path.Combine(Dir, Safe(g.Id) + ".none");

    /// <summary>The art already on disk for this game without any network: your image, the cache, or Steam's own cache.</summary>
    public static ArtResult? Local(GameInfo g, string? steamRoot = null)
    {
        if (File.Exists(UserPath(g))) return new(UserPath(g), "your image");
        if (File.Exists(CachePath(g))) return new(CachePath(g), "cache");
        if (g.SteamAppId is { } id && SteamLocal(id, steamRoot) is { } p) return new(p, "Steam library cache");
        return null;
    }

    public static string? SteamRoot()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"); return k?.GetValue("SteamPath") as string; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Steam keeps header images in appcache\librarycache, in a folder per app (newer clients) or as {id}_header.jpg (older).</summary>
    public static string? SteamLocal(string appId, string? root = null)
    {
        root ??= SteamRoot();
        if (root is null) return null;
        var lib = Path.Combine(root, "appcache", "librarycache");
        foreach (var rel in new[] { Path.Combine(appId, "header.jpg"), appId + "_header.jpg", Path.Combine(appId, "library_header.jpg") })
            if (File.Exists(Path.Combine(lib, rel))) return Path.Combine(lib, rel);
        var dir = Path.Combine(lib, appId);
        try
        {
            if (Directory.Exists(dir))
                foreach (var sub in Directory.GetDirectories(dir))
                    if (Directory.GetFiles(sub, "header.jpg").FirstOrDefault() is { } f) return f;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    public static bool HostAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        return AllowedHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True for the first bytes of a JPEG, PNG or WebP file.</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> b) =>
        b.Length > 12 && ((b[0] == 0xFF && b[1] == 0xD8) || (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||
                          (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P'));

    static HttpClient NewClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("OptiGame-art");
        return h;
    }

    /// <summary>Local art first; then the online chain when <paramref name="network"/> is true. Null when nothing was found.</summary>
    public static async Task<ArtResult?> GetAsync(GameInfo g, bool network, string? sgdbKey = null, HttpClient? http = null, CancellationToken ct = default)
    {
        if (Local(g) is { } local) return local;
        if (!network) return null;
        if (File.Exists(MissPath(g)) && DateTime.UtcNow - File.GetLastWriteTimeUtc(MissPath(g)) < MissTtl) return null;

        var own = http is null;
        http ??= NewClient();
        try
        {
            foreach (var (source, urlTask) in Candidates(g, sgdbKey, http, ct))
            {
                string? url;
                try { url = await urlTask(); } catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException) { continue; }
                if (url is null || !HostAllowed(url)) continue;
                var bytes = await DownloadAsync(http, url, ct);
                if (bytes is null) continue;
                Directory.CreateDirectory(Dir);
                await File.WriteAllBytesAsync(CachePath(g), bytes, ct);
                return new(CachePath(g), source);
            }
            Directory.CreateDirectory(Dir);
            File.WriteAllText(MissPath(g), DateTime.UtcNow.ToString("o"));
            return null;
        }
        finally { if (own) http.Dispose(); }
    }

    static async Task<byte[]?> DownloadAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!r.IsSuccessStatusCode || (r.Content.Headers.ContentLength ?? 0) > MaxBytes) return null;
            var bytes = await r.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length is > 0 and <= (int)MaxBytes && LooksLikeImage(bytes) ? bytes : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return null; }
    }

    static IEnumerable<(string, Func<Task<string?>>)> Candidates(GameInfo g, string? sgdbKey, HttpClient http, CancellationToken ct)
    {
        if (g.Id.StartsWith("epic:", StringComparison.OrdinalIgnoreCase) || g.Sources.Contains("Epic"))
            yield return ("Epic catalog", () => Task.FromResult(EpicImageUrl(g.Name)));
        if (g.GogId is { Length: > 0 } gog)
            yield return ("GOG", async () => GogImageUrl(await http.GetStringAsync($"https://api.gog.com/products/{Uri.EscapeDataString(gog)}", ct)));
        if (g.SteamAppId is { } id)
            yield return ("Steam image server", () => Task.FromResult<string?>(SteamHeaderUrl(id)));
        else
        {
            yield return ("Steam store (exact title)", async () =>
                SteamSearchAppId(await http.GetStringAsync($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(g.Name)}&cc=us&l=en", ct), g.Name) is { } found ? SteamHeaderUrl(found) : null);
            if (!string.IsNullOrWhiteSpace(sgdbKey))
                yield return ("SteamGridDB", () => SteamGridUrlAsync(http, g.Name, sgdbKey!, ct));
        }
    }

    public static string SteamHeaderUrl(string appId) => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";

    /// <summary>The app id of a store-search result whose title equals the game's name (case-insensitive); never a near match.</summary>
    public static string? SteamSearchAppId(string json, string title)
    {
        try
        {
            var items = JsonNode.Parse(json)?["items"]?.AsArray();
            if (items is null) return null;
            var hit = items.FirstOrDefault(i => string.Equals(i?["name"]?.GetValue<string>()?.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));
            var id = hit?["id"]?.ToString();
            return id is { Length: > 0 } && id.All(char.IsAsciiDigit) ? id : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    public static string? GogImageUrl(string json)
    {
        try
        {
            var img = JsonNode.Parse(json)?["images"];
            var u = img?["background"]?.GetValue<string>() ?? img?["logo2x"]?.GetValue<string>();
            return u is null ? null : u.StartsWith("//") ? "https:" + u : u;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    static async Task<string?> SteamGridUrlAsync(HttpClient http, string name, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(name)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var r = await http.SendAsync(req, ct);
        if (!r.IsSuccessStatusCode) return null;
        var data = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))?["data"]?.AsArray();
        var hit = data?.FirstOrDefault(i => string.Equals(i?["name"]?.GetValue<string>()?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        var id = hit?["id"]?.ToString();
        if (id is null || !id.All(char.IsAsciiDigit)) return null;
        using var req2 = new HttpRequestMessage(HttpMethod.Get, $"https://www.steamgriddb.com/api/v2/grids/game/{id}?dimensions=460x215,920x430");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var r2 = await http.SendAsync(req2, ct);
        if (!r2.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await r2.Content.ReadAsStringAsync(ct))?["data"]?.AsArray().FirstOrDefault()?["url"]?.GetValue<string>();
    }

    // ------------------------------------------------------------------ Epic's own catalog (a local file; no network to read it)

    public static string EpicCatalogFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");

    /// <summary>The wide key art URL of the catalog entry whose title equals <paramref name="title"/>, or null.</summary>
    public static string? EpicImageUrl(string title, string? file = null)
    {
        try
        {
            file ??= EpicCatalogFile;
            if (!File.Exists(file)) return null;
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(file).Trim()));
            var items = JsonNode.Parse(json)?.AsArray();
            var hit = items?.FirstOrDefault(i => string.Equals(i?["title"]?.GetValue<string>()?.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));
            var images = hit?["keyImages"]?.AsArray();
            string? Pick(string type) => images?.FirstOrDefault(i => i?["type"]?.GetValue<string>() == type)?["url"]?.GetValue<string>();
            return Pick("OfferImageWide") ?? Pick("DieselGameBoxWide") ?? Pick("Thumbnail");
        }
        catch (Exception e) when (e is IOException or FormatException or JsonException or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }

    // ------------------------------------------------------------------ your own choices

    static bool LooksLikeUserImage(ReadOnlySpan<byte> bytes, ArtSlot slot) =>
        LooksLikeImage(bytes) || (slot == ArtSlot.SmallIcon && bytes.Length > 22 &&
            bytes.StartsWith(new byte[] { 0, 0, 1, 0 }) && (bytes[4] != 0 || bytes[5] != 0));

    public static byte[]? ReadUserImage(string file, ArtSlot slot)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            if (length is <= 0 or > MaxBytes) return null;
            var bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            return stream.ReadByte() == -1 && LooksLikeUserImage(bytes, slot) ? bytes : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Uses an image you picked for this game (copied into the art folder).</summary>
    public static bool SetUserImage(GameInfo g, string file) =>
        ReadUserImage(file, ArtSlot.Cover) is { } bytes && SetUserImage(g, ArtSlot.Cover, bytes);

    public static bool SetUserImage(GameInfo g, ArtSlot slot, byte[] bytes)
    {
        if (bytes.Length > MaxBytes || !LooksLikeUserImage(bytes, slot)) return false;
        var path = UserPath(g, slot);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        finally { DeleteImage(temporary); }
    }

    public static bool ClearUserSmallIcon(GameInfo g) => DeleteImage(UserPath(g, ArtSlot.SmallIcon));

    static bool DeleteImage(string path)
    {
        try { File.Delete(path); return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Forgets the cached image, the miss marker and your own image so the next look-up starts fresh.</summary>
    public static void Reset(GameInfo g, bool keepUserImage = false)
    {
        foreach (var f in new[] { CachePath(g), MissPath(g) }.Concat(keepUserImage ? Array.Empty<string>() : new[] { UserPath(g) }))
            DeleteImage(f);
    }
}
