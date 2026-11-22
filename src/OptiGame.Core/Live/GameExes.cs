namespace OptiGame.Core.Live;

/// <summary>
/// Finds the executables of an installed game, for the Windows per-app graphics preference
/// (HKCU\Software\Microsoft\DirectX\UserGpuPreferences is keyed by exe path). Looks at most three folders deep and
/// never opens or runs anything.
/// </summary>
public static class GameExes
{
    static readonly string[] SkipWords =
    {
        "crash", "helper", "webhelper", "launcher", "unins", "setup", "redist", "vc_redist", "easyanticheat", "battleye", "benchmark",
        "dxsetup", "dotnet", "overlay", "bootstrap", "updater", "cefsubprocess", "eosoverlay", "installer", "prereq", "report", "service",
        "ue4prereq", "unitycrashhandler", "crashpad", "gamingservices",
    };

    const long BigExe = 20L * 1024 * 1024;

    /// <summary>The game's main executables: Unreal "-Shipping" builds, large exes, and Unity players. At most <paramref name="max"/>.</summary>
    public static List<string> Find(string installDir, int max = 6)
    {
        var found = new List<(string path, int score)>();
        try
        {
            Walk(new DirectoryInfo(installDir), 0, found);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found.OrderByDescending(f => f.score).ThenBy(f => f.path.Length).Take(max).Select(f => f.path).ToList();
    }

    static void Walk(DirectoryInfo dir, int depth, List<(string, int)> found)
    {
        if (depth > 3) return;
        FileInfo[] files;
        try { files = dir.GetFiles("*.exe"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        bool unity = Directory.Exists(System.IO.Path.Combine(dir.FullName, "MonoBleedingEdge")) || File.Exists(System.IO.Path.Combine(dir.FullName, "UnityPlayer.dll"));
        foreach (var f in files)
        {
            var lower = f.Name.ToLowerInvariant();
            if (SkipWords.Any(lower.Contains)) continue;
            if (lower.EndsWith("-shipping.exe") || lower.EndsWith("-wingdk-shipping.exe")) found.Add((f.FullName, 3));
            else if (unity) found.Add((f.FullName, 2));
            else if (f.Length >= BigExe) found.Add((f.FullName, 2));
            else if (depth == 0) found.Add((f.FullName, 1));
        }
        DirectoryInfo[] subs;
        try { subs = dir.GetDirectories(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        foreach (var s in subs)
        {
            var n = s.Name.ToLowerInvariant();
            if (n is "_commonredist" or "redist" or "directx" or "vcredist" or "__installer" or "engine" or "crashreportclient") continue;
            Walk(s, depth + 1, found);
        }
    }
}
