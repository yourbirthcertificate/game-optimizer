using System.Text.Json;
using GameOptimizer.Core.Formats;
using Microsoft.Win32;

namespace GameOptimizer.Core.Library;

public sealed class ScanResult
{
    public List<GameInfo> Games { get; } = new();
    /// <summary>Things worth telling the user, such as a Steam library on a drive that is not connected.</summary>
    public List<string> Notes { get; } = new();
}

/// <summary>Finds installed games from Steam and Epic. Read-only: never runs or opens game files for writing.</summary>
public static class LibraryScanner
{
    // Steam tools and runtimes that are not games.
    static readonly HashSet<string> SteamSkip = new(StringComparer.OrdinalIgnoreCase)
    { "228980", "250820", "1070560", "1391110", "1628350", "1493710", "2180100" };

    public static ScanResult Scan()
    {
        var r = new ScanResult();
        ScanSteam(r);
        ScanEpic(r);
        foreach (var g in r.Games) EngineDetector.Detect(g);
        r.Games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return r;
    }

    public static string? SteamRoot()
    {
        foreach (var (hive, path) in new[] { (Registry.CurrentUser, @"Software\Valve\Steam"), (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam") })
        {
            using var k = hive.OpenSubKey(path);
            var v = (k?.GetValue("SteamPath") ?? k?.GetValue("InstallPath")) as string;
            if (!string.IsNullOrEmpty(v) && Directory.Exists(v)) return Path.GetFullPath(v);
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    static void ScanSteam(ScanResult r)
    {
        var root = SteamRoot();
        if (root is null) { r.Notes.Add("Steam is not installed."); return; }
        var libs = new List<string> { root };
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        try
        {
            var kv = KvNode.Parse(File.ReadAllText(vdf));
            var lf = kv["libraryfolders"];
            if (lf is not null)
                foreach (var lib in lf.Children)
                    if (lib.Get("path") is { } p && !libs.Any(l => PathEq(l, p))) libs.Add(p);
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            r.Notes.Add($"Could not read Steam's library list: {e.Message}");
        }

        var seen = new HashSet<string>();
        foreach (var lib in libs)
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) { r.Notes.Add($"Steam library {lib} is not reachable (drive not connected?). Skipped."); continue; }
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var st = KvNode.Parse(File.ReadAllText(acf))["AppState"];
                    var id = st?.Get("appid");
                    var name = st?.Get("name");
                    var dir = st?.Get("installdir");
                    if (id is null || name is null || dir is null || SteamSkip.Contains(id) || !seen.Add(id)) continue;
                    var full = Path.Combine(apps, "common", dir);
                    if (!Directory.Exists(full)) continue;
                    r.Games.Add(new GameInfo { Id = "steam:" + id, Name = name, InstallDir = full, Store = "Steam", SteamAppId = id });
                }
                catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
                {
                    r.Notes.Add($"Skipped unreadable Steam manifest {Path.GetFileName(acf)}: {e.Message}");
                }
            }
        }
    }

    static void ScanEpic(ScanResult r)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) return;
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var e = doc.RootElement;
                string? S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var name = S("DisplayName"); var loc = S("InstallLocation"); var app = S("AppName");
                if (name is null || loc is null || app is null || !Directory.Exists(loc)) continue;
                if (e.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                if (e.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array &&
                    !cats.EnumerateArray().Any(c => c.GetString() == "games")) continue;
                r.Games.Add(new GameInfo { Id = "epic:" + app, Name = name, InstallDir = loc, Store = "Epic" });
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                r.Notes.Add($"Skipped unreadable Epic manifest {Path.GetFileName(item)}: {ex.Message}");
            }
        }
    }

    static bool PathEq(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
