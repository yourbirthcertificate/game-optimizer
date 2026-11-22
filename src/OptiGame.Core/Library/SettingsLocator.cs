using System.Text;

namespace OptiGame.Core.Library;

/// <summary>A folder that probably holds a game's settings, with the settings-like files seen in it.</summary>
public sealed record SettingsCandidate(string Folder, IReadOnlyList<string> Files);

/// <summary>
/// For a game with no handler or rule: looks (read-only) for a folder named after the game in the usual places
/// (Local, LocalLow and Roaming AppData, Documents, My Games, Saved Games), also one level down under a publisher
/// folder, and lists settings-like files in it. Matches the folder name to the game's name or its install folder name
/// after dropping case, spaces and punctuation; never a near match. It only points the way: nothing is read or changed.
/// </summary>
public static class SettingsLocator
{
    static readonly string[] SettingExt = { ".ini", ".json", ".cfg", ".config", ".xml", ".conf", ".settings", ".txt", ".vcfg", ".lua", ".yaml", ".yml", ".toml" };
    static readonly string[] SettingWords = { "setting", "config", "option", "graphic", "video", "display", "preference", "prefs", "user", "game" };

    static Dictionary<string, List<string>>? _index;
    static readonly object Lock = new();

    /// <summary>Forget the folder index (called on rescan, so a game started since is found).</summary>
    public static void Reset() { lock (Lock) _index = null; }

    public static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
        var t = sb.ToString();
        return t.StartsWith("the") && t.Length > 6 ? t[3..] : t;
    }

    public static IEnumerable<string> Roots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return local;
        yield return Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");
        yield return Path.Combine(profile, "Saved Games");
    }

    /// <summary>Index of folder name (normalized) to full paths: each root's children, and their children (publisher\game).</summary>
    static Dictionary<string, List<string>> Index(IEnumerable<string> roots)
    {
        var map = new Dictionary<string, List<string>>();
        void Add(string dir)
        {
            var key = Normalize(Path.GetFileName(dir));
            if (key.Length < 3) return;
            if (!map.TryGetValue(key, out var l)) map[key] = l = new List<string>();
            l.Add(dir);
        }
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var d1 in Directory.EnumerateDirectories(root))
                {
                    Add(d1);
                    string n1 = Path.GetFileName(d1);
                    if (n1 is "Microsoft" or "Packages" or "Temp" or "Programs" or "Google" or "Mozilla" or "NVIDIA" or "Intel" or "Adobe" or "JetBrains" or "npm" or "Claude" or "Discord") continue;
                    try { foreach (var d2 in Directory.EnumerateDirectories(d1)) Add(d2); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return map;
    }

    public static SettingsCandidate? Find(GameInfo g, IEnumerable<string>? roots = null)
    {
        Dictionary<string, List<string>> idx;
        if (roots is not null) idx = Index(roots);
        else lock (Lock) idx = _index ??= Index(Roots());

        foreach (var k in MatchKeys(g))
        {
            if (!idx.TryGetValue(k, out var folders)) continue;
            foreach (var f in folders)
            {
                var files = SettingsFiles(f);
                if (files.Count > 0) return new SettingsCandidate(f, files);
            }
        }
        return null;
    }

    internal static IEnumerable<string> MatchKeys(GameInfo game, int minimumLength = 3) =>
        new[]
        {
            Normalize(game.Name),
            Normalize(Path.GetFileName(game.InstallDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
        }.Where(key => key.Length >= minimumLength).Distinct();

    internal static bool IsSettingsFile(string name)
    {
        var lower = name.ToLowerInvariant();
        return SettingExt.Contains(Path.GetExtension(lower)) && SettingWords.Any(lower.Contains)
            && !lower.Contains("backup") && !lower.Contains(".bak");
    }

    static List<string> SettingsFiles(string folder)
    {
        var found = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }))
            {
                if (!IsSettingsFile(Path.GetFileName(f))) continue;
                found.Add(Path.GetRelativePath(folder, f));
                if (found.Count >= 4) break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }
}
