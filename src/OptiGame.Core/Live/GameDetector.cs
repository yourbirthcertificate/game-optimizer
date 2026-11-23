using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OptiGame.Core.Live;

/// <summary>
/// Confident detection: exe in a known game-library folder, a "-Win64-Shipping" build, the built-in list,
/// or the user's own list. A borderless-fullscreen app is reported as a non-confident "likely game".
/// </summary>
public sealed class GameDetector
{
    static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "cyberpunk2077", "eldenring", "gta5", "gta5_enhanced", "rdr2", "cs2", "csgo", "dota2", "r5apex", "r5apex_dx12", "overwatch",
        "valorant-win64-shipping", "fortniteclient-win64-shipping", "witcher3", "bg3", "bg3_dx11", "hogwartslegacy", "helldivers2",
        "palworld-win64-shipping", "starfield", "ffxiv_dx11", "ffxiv", "wow", "wowclassic", "diablo iv", "d4", "minecraft.windows",
        "baldursgate3", "apexlegends", "rocketleague", "destiny2", "tslgame", "pubg", "rainbowsix", "rainbowsix_vulkan", "forzahorizon5",
        "monsterhunterwilds", "sekiro", "darksoulsiii", "ds3", "nightreign", "hades2", "hollowknight", "stardewvalley", "terraria",
        "lethalcompany", "deadbydaylight-win64-shipping", "league of legends", "leagueclient"
    };

    static readonly string[] Libraries =
    {
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\gog games\", @"\riot games\",
        @"\ubisoft game launcher\games\", @"\ea games\", @"\origin games\", @"\xboxgames\"
    };

    static readonly string[] Excluded =
    {
        "crash", "helper", "webhelper", "launcher", "unins", "setup", "redist", "vc_redist", "easyanticheat", "battleye", "benchmark",
        "dxsetup", "dotnet", "overlay", "bootstrap", "updater", "cefsubprocess", "eosoverlay", "installer", "prereq", "report", "service",
        "leagueclient", "riotclient", "steamservice", "gamingservices",
        "wallpaper", "ui32", "vrserver", "vrmonitor", "vrcompositor"   // Steam-library tools that are not games
    };

    static readonly List<string> TempRoots = Roots(new[] { Path.GetTempPath() });

    readonly Dictionary<int, (string name, string? path)> _paths = new();
    readonly int _session = Process.GetCurrentProcess().SessionId;

    /// <param name="libraryDirs">Install folders of the games found by the library scan (Steam, Epic, GOG).</param>
    /// <param name="userDirs">Folders the user added by hand: anything running from inside counts as "on your list".</param>
    public GameHit? Detect(LiveData data, IReadOnlyCollection<string>? libraryDirs = null, IReadOnlyCollection<string>? userDirs = null)
    {
        var lib = Roots(libraryDirs);
        var usr = Roots(userDirs);
        var hits = new Dictionary<string, GameHit>(StringComparer.OrdinalIgnoreCase);
        var userPaths = new HashSet<string>(data.Games.Select(g => g.Path), StringComparer.OrdinalIgnoreCase);
        var alive = new HashSet<int>();
        IntPtr fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out int fgPid);
        (string? name, string? path, long mem, int pid) fgInfo = (null, null, 0, 0);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id <= 4 || p.SessionId != _session) continue;
                    alive.Add(p.Id);
                    string name = p.ProcessName;
                    if (!_paths.TryGetValue(p.Id, out var c) || !c.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        _paths[p.Id] = c = (name, Native.GetImagePath(p.Id));
                    string? path = c.path;
                    if (path == null) continue;

                    if (p.Id == fgPid) fgInfo = (name, path, p.WorkingSet64, p.Id);

                    var (why, score) = Why(name, path, userPaths, lib, usr);
                    if (why == null) continue;
                    if (!hits.TryGetValue(path, out var h))
                        hits[path] = h = new GameHit { Exe = name, Path = path, Name = NiceName(path, name), Confident = true, Why = why, Score = score };
                    h.Pids.Add(p.Id);
                    h.MemBytes += p.WorkingSet64;
                }
                catch { }
            }
        }
        foreach (var stale in _paths.Keys.Where(k => !alive.Contains(k)).ToList()) _paths.Remove(stale);

        if (hits.Count > 0)
        {
            // Most certain first (your list > known game > library folder), then the foreground window, then the biggest memory user.
            return hits.Values
                .OrderByDescending(h => h.Score)
                .ThenByDescending(h => h.Pids.Contains(fgPid))
                .ThenByDescending(h => h.MemBytes)
                .First();
        }

        // Non-confident: borderless fullscreen app that is not a browser/media player/system thing.
        if (fgInfo.name != null && fgInfo.path != null && fgInfo.mem > 300L * 1048576 && Native.IsFullscreen(fg))
        {
            string key = fgInfo.name.ToLowerInvariant();
            var (tier, _) = Catalog.Classify(key, fgInfo.path, true, fgInfo.mem / 1048576.0);
            if (tier is Tier.Protected or Tier.Keep || Catalog.IsBrowser(key) || Catalog.IsMediaPlayer(key) || key == "explorer") return null;
            return new GameHit
            {
                Exe = fgInfo.name, Path = fgInfo.path, Name = NiceName(fgInfo.path, fgInfo.name),
                Pids = new List<int> { fgInfo.pid }, Confident = false, Why = "Fullscreen app"
            };
        }
        return null;
    }

    static List<string> Roots(IReadOnlyCollection<string>? dirs) =>
        (dirs ?? Array.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.GetFullPath(d).TrimEnd('\\') + "\\").ToList();

    /// <summary>True when <paramref name="path"/> is inside one of the folders (each ending in a backslash).</summary>
    public static bool IsUnder(string path, IEnumerable<string> roots) =>
        roots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));

    internal static (string? why, int score) Why(string name, string path, HashSet<string> userPaths, List<string> lib, List<string> usr)
    {
        string lname = name.ToLowerInvariant();
        // Test runs and installers unpack stand-in programs named after real games into the temp folder. Never a game.
        if (IsUnder(path, TempRoots)) return (null, 0);
        if (userPaths.Contains(path) || IsUnder(path, usr)) return ("On your game list", 3);
        if (Excluded.Any(e => lname.Contains(e))) return (null, 0);
        if (path.Contains(@"\steamworks shared\", StringComparison.OrdinalIgnoreCase)) return (null, 0);
        if (Known.Contains(lname)) return ("Known game", 2);
        if (lname.EndsWith("-win64-shipping") || lname.EndsWith("-wingdk-shipping")) return ("Unreal Engine game", 2);
        if (IsUnder(path, lib)) return ("Installed in a game library", 1);
        foreach (var l in Libraries)
            if (path.Contains(l, StringComparison.OrdinalIgnoreCase)) return ("Installed in a game library", 1);
        return (null, 0);
    }

    public static string NiceName(string path, string exe)
    {
        const string steam = @"\steamapps\common\";
        int i = path.IndexOf(steam, StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            string rest = path[(i + steam.Length)..];
            int s = rest.IndexOf('\\');
            if (s > 0) return rest[..s];
        }
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(vi.ProductName) && vi.ProductName.Length < 40 && !vi.ProductName.Contains("Operating System")) return vi.ProductName.Trim();
            if (!string.IsNullOrWhiteSpace(vi.FileDescription) && vi.FileDescription.Length < 40) return vi.FileDescription.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }
}
