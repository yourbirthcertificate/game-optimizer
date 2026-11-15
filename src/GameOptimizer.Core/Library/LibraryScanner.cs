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

    public static ScanResult Scan(IEnumerable<GameOptimizer.Core.Optimize.ManualGame>? manual = null)
    {
        var r = new ScanResult();
        ScanSteam(r);
        ScanEpic(r);
        ScanGog(r);
        if (manual is not null) AddManual(r, manual);
        var merged = Deduplicate(r.Games);
        r.Games.Clear();
        r.Games.AddRange(merged);
        foreach (var g in r.Games) EngineDetector.Detect(g);
        r.Games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return r;
    }

    public static string NormalizeDir(string dir)
    {
        try { return Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant(); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant(); }
    }

    static int Priority(string store) => store switch { "Steam" => 0, "Epic" => 1, "GOG" => 2, _ => 3 };

    /// <summary>
    /// One entry per install folder. When several sources report the same folder, the first by
    /// Steam, Epic, GOG, manual order keeps its id and name, and the entry lists every source.
    /// </summary>
    public static List<GameInfo> Deduplicate(IEnumerable<GameInfo> games)
    {
        var result = new List<GameInfo>();
        foreach (var grp in games.GroupBy(g => NormalizeDir(g.InstallDir)))
        {
            var ordered = grp.OrderBy(g => Priority(g.Store)).ToList();
            var keep = ordered[0];
            keep.Sources = ordered.Select(g => g.Store == "Manual" ? "Added by you" : g.Store).Distinct().ToList();
            result.Add(keep);
        }
        return result;
    }

    public static string ManualId(string path) => "manual:" + NormalizeDir(path);

    static void AddManual(ScanResult r, IEnumerable<GameOptimizer.Core.Optimize.ManualGame> manual)
    {
        foreach (var m in manual)
        {
            if (string.IsNullOrWhiteSpace(m.Path)) continue;
            if (!Directory.Exists(m.Path)) { r.Notes.Add($"Folder you added is not reachable: {m.Path} (drive not connected?). Skipped."); continue; }
            r.Games.Add(new GameInfo
            {
                Id = ManualId(m.Path), Name = string.IsNullOrWhiteSpace(m.Name) ? Path.GetFileName(m.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : m.Name!,
                InstallDir = m.Path, Store = "Manual",
            });
        }
    }

    /// <summary>GOG Galaxy and GOG offline installers register games under HKLM\SOFTWARE\WOW6432Node\GOG.com\Games.</summary>
    static void ScanGog(ScanResult r)
    {
        foreach (var root in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
        {
            using var games = Registry.LocalMachine.OpenSubKey(root);
            if (games is null) continue;
            foreach (var id in games.GetSubKeyNames())
            {
                try
                {
                    using var k = games.OpenSubKey(id);
                    var name = k?.GetValue("gameName") as string ?? k?.GetValue("GAMENAME") as string;
                    var path = k?.GetValue("path") as string ?? k?.GetValue("PATH") as string;
                    // DLC entries point at their base game's folder and carry a dependsOn value.
                    if (k?.GetValue("dependsOn") is string dep && dep.Length > 0) continue;
                    if (name is null || path is null || !Directory.Exists(path)) continue;
                    r.Games.Add(new GameInfo { Id = "gog:" + id, Name = name, InstallDir = path, Store = "GOG", GogId = id });
                }
                catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
            }
        }
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
        var before = r.Games.Count;
        if (Directory.Exists(dir)) ReadEpicItems(r, dir);
        var listed = r.Games.Skip(before).ToList();

        // The launcher sometimes fails to write its install records (a missing or unwritable
        // Manifests folder), so also look for installs by the .egstore folder each one carries.
        var roots = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games") };
        roots.AddRange(listed.Select(g => Path.GetDirectoryName(g.InstallDir.TrimEnd('/', Path.DirectorySeparatorChar)) ?? ""));
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.DriveType != DriveType.Fixed || !d.IsReady) continue; }
            catch (IOException) { continue; }
            roots.Add(Path.Combine(d.RootDirectory.FullName, "Epic Games"));
            roots.Add(Path.Combine(d.RootDirectory.FullName, "Program Files", "Epic Games"));
        }
        foreach (var g in FindEpicFolders(roots, listed.Select(g => g.InstallDir)))
        {
            r.Games.Add(g);
            r.Notes.Add($"Found {g.Name} by its Epic store folder; the Epic launcher has no install record for it.");
        }
    }

    /// <summary>
    /// Epic installs that the launcher has no record for: folders under the given library roots
    /// holding a .egstore folder with a build manifest. Folders in <paramref name="known"/> are skipped.
    /// </summary>
    public static List<GameInfo> FindEpicFolders(IEnumerable<string> roots, IEnumerable<string> known)
    {
        var seen = new HashSet<string>(known.Select(NormalizeDir));
        var found = new List<GameInfo>();
        foreach (var root in roots.Where(s => s.Length > 0).Select(NormalizeDir).Distinct())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(root).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var sub in subs)
            {
                try
                {
                    var store = Path.Combine(sub, ".egstore");
                    if (!Directory.Exists(store) || !Directory.EnumerateFiles(store, "*.manifest").Any()) continue;
                    // A download in progress keeps its staging data here; the build is not complete yet.
                    if (Directory.Exists(Path.Combine(store, "bps")) || Directory.Exists(Path.Combine(store, "Pending"))) continue;
                    if (!seen.Add(NormalizeDir(sub))) continue;
                    var name = Path.GetFileName(sub);
                    found.Add(new GameInfo { Id = "epic:" + name, Name = name, InstallDir = sub, Store = "Epic" });
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return found;
    }

    static void ReadEpicItems(ScanResult r, string dir)
    {
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
