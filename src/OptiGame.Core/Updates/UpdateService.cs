using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace OptiGame.Core.Updates;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Name, string Notes, string PageUrl,
                                string? AssetUrl, string? AssetName, long Size, string? Sha256);

/// <summary>
/// The updater. It asks GitHub's latest-release API for this project's newest release and, on a click, downloads the
/// setup program from that release, checks its SHA-256 against the digest GitHub lists for the asset, and runs it.
/// Nothing about the PC is sent (a plain GET with a generic user agent); a download without a digest, or from any other
/// address than this project's releases, is refused. Release names and notes are untrusted text.
/// </summary>
public static class UpdateService
{
    public const string Repo = "yourbirthcertificate/game-optimizer";
    public const string AssetPrefix = "OptiGame-Setup";
    public static string LatestUrl => $"https://api.github.com/repos/{Repo}/releases/latest";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";
    static string DownloadPrefix => $"https://github.com/{Repo}/releases/download/";

    public static Version Current
    {
        get
        {
            var info = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (info is not null && TryParseVersion(info, out var v)) return v;
            return typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);
        }
    }

    /// <summary>"v0.3.0", "0.3.0+abc123" or "0.3.0-beta" become 0.3.0.</summary>
    public static bool TryParseVersion(string text, out Version version)
    {
        var t = text.Trim().TrimStart('v', 'V');
        int cut = t.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut > 0) t = t[..cut];
        if (Version.TryParse(t, out var v) && v is not null)
        {
            version = v.Build < 0 ? new Version(v.Major, v.Minor, 0) : v;
            return true;
        }
        version = new Version(0, 0);
        return false;
    }

    /// <summary>Reads GitHub's release JSON. Returns null when it has no usable version or no setup asset is attached.</summary>
    public static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pr) && pr.GetBoolean()) return null;
            string tag = Str(root, "tag_name");
            if (!TryParseVersion(tag, out var version)) return null;

            string? url = null, name = null, sha = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var n = Str(a, "name");
                    if (!n.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) || !n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    url = Str(a, "browser_download_url"); name = n;
                    size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var l) ? l : 0;
                    var digest = Str(a, "digest");
                    if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = digest[7..].Trim().ToLowerInvariant();
                    break;
                }
            return new UpdateInfo(version, tag, Str(root, "name"), Str(root, "body"), Str(root, "html_url"), url, name, size, sha);
        }
        catch (JsonException) { return null; }
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static bool IsNewer(UpdateInfo info, Version? current = null) => info.Version > (current ?? Current);

    /// <summary>True when the setup asset can be installed: it is from this project's releases and carries a SHA-256 digest.</summary>
    public static bool CanInstall(UpdateInfo info) =>
        info.AssetUrl is { } u && u.StartsWith(DownloadPrefix, StringComparison.Ordinal) && info.Sha256 is { Length: 64 };

    static HttpClient NewClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("OptiGame-update-check");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>The newest release if it is newer than this build, else null. Network errors return null.</summary>
    public static async Task<(UpdateInfo? update, string status)> CheckAsync(HttpClient? http = null, string? url = null, Version? current = null, CancellationToken ct = default)
    {
        var own = http is null;
        http ??= NewClient();
        try
        {
            using var r = await http.GetAsync(url ?? LatestUrl, ct);
            if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, "No release has been published yet.");
            if (!r.IsSuccessStatusCode) return (null, $"GitHub answered {(int)r.StatusCode}. Try again later.");
            var info = Parse(await r.Content.ReadAsStringAsync(ct));
            if (info is null) return (null, "The latest release has no installer attached.");
            return IsNewer(info, current) ? (info, $"Version {info.Version} is available.") : (null, $"You have the latest version ({(current ?? Current)}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (null, "Could not reach GitHub to check for updates.");
        }
        finally { if (own) http.Dispose(); }
    }

    /// <summary>Downloads the setup to <paramref name="dir"/> and verifies its SHA-256. Throws InvalidDataException when it does not match.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, string dir, IProgress<double>? progress = null, HttpClient? http = null, CancellationToken ct = default)
    {
        if (!CanInstall(info)) throw new InvalidDataException("This release has no verifiable installer (it needs a SHA-256 digest and must come from this project's releases).");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Path.GetFileName(info.AssetName!));
        var own = http is null;
        http ??= NewClient();
        try
        {
            using var r = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            r.EnsureSuccessStatusCode();
            long total = r.Content.Headers.ContentLength ?? info.Size;
            using (var src = await r.Content.ReadAsStreamAsync(ct))
            using (var dst = File.Create(file))
            {
                var buf = new byte[81920]; long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            if (!Sha256Matches(file, info.Sha256!))
            {
                File.Delete(file);
                throw new InvalidDataException("The downloaded file does not match the checksum GitHub lists, so it was deleted.");
            }
            return file;
        }
        finally { if (own) http.Dispose(); }
    }

    public static bool Sha256Matches(string file, string expectedHex)
    {
        using var fs = File.OpenRead(file);
        var hex = Convert.ToHexString(SHA256.HashData(fs));
        return hex.Equals(expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where the app was installed by its setup program (%LOCALAPPDATA%\Programs\OptiGame), for per-user installs.</summary>
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OptiGame");

    /// <summary>True when this exe runs from the folder the setup installs to, so an update can replace it in place.</summary>
    public static bool RunsFromInstall(string? exeDir = null)
    {
        var dir = Path.GetFullPath(exeDir ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        return dir.Equals(Path.GetFullPath(InstallDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    public static string UpdateDir => Path.Combine(AppPaths.DataDir, "update");

    /// <summary>Runs the setup quietly. The caller must exit afterwards so the setup can replace the files; the setup starts OptiGame+ again.</summary>
    public static Process? RunSetup(string setupPath) =>
        Process.Start(new ProcessStartInfo(setupPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });

    /// <summary>Deletes setup files left from an earlier update.</summary>
    public static void CleanUp()
    {
        try { if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
