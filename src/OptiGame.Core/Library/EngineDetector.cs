namespace OptiGame.Core.Library;

/// <summary>Identifies a game's engine from files in its install folder. Looks at most two levels deep.</summary>
public static class EngineDetector
{
    public static void Detect(GameInfo g)
    {
        try { DetectCore(g); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { g.Engine = GameEngine.Unknown; }
    }

    static void DetectCore(GameInfo g)
    {
        var root = g.InstallDir;
        var dirs = Directory.GetDirectories(root);

        // Unreal: <Project>/Binaries/Win64/*.exe plus <Project>/Content (usually Content/Paks).
        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (name.Equals("Engine", StringComparison.OrdinalIgnoreCase)) continue;
            var bin = Path.Combine(d, "Binaries", "Win64");
            if (Directory.Exists(bin) && Directory.Exists(Path.Combine(d, "Content")) &&
                Directory.EnumerateFiles(bin, "*.exe").Any())
            {
                g.Engine = GameEngine.Unreal;
                g.EngineDetail = name;
                return;
            }
        }

        // Unity: <Exe>_Data/app.info holds "Company\nProduct", which names the PlayerPrefs registry key.
        foreach (var where in dirs.Prepend(root))
        {
            foreach (var data in Directory.EnumerateDirectories(where, "*_Data"))
            {
                bool unity = File.Exists(Path.Combine(where, "UnityPlayer.dll")) ||
                             File.Exists(Path.Combine(data, "globalgamemanagers")) ||
                             File.Exists(Path.Combine(data, "mainData"));
                if (!unity) continue;
                g.Engine = GameEngine.Unity;
                var info = Path.Combine(data, "app.info");
                if (File.Exists(info))
                {
                    var lines = File.ReadAllLines(info);
                    if (lines.Length >= 2 && lines[0].Length > 0 && lines[1].Length > 0)
                        g.EngineDetail = lines[0].Trim() + "\\" + lines[1].Trim();
                }
                return;
            }
        }

        // Source 2: game/<mod>/gameinfo.gi. Source 1: <mod>/gameinfo.txt.
        var game = Path.Combine(root, "game");
        if (Directory.Exists(game))
            foreach (var mod in Directory.GetDirectories(game))
                if (File.Exists(Path.Combine(mod, "gameinfo.gi")))
                { g.Engine = GameEngine.Source2; g.EngineDetail = Path.GetFileName(mod); return; }
        foreach (var d in dirs)
            if (File.Exists(Path.Combine(d, "gameinfo.txt")) && Path.GetFileName(d) is not ("platform" or "bin"))
            { g.Engine = GameEngine.Source; g.EngineDetail = Path.GetFileName(d); return; }

        if (File.Exists(Path.Combine(root, "data.win"))) { g.Engine = GameEngine.GameMaker; return; }
        if (Directory.EnumerateFiles(root, "*.pck").Any()) { g.Engine = GameEngine.Godot; return; }
        g.Engine = GameEngine.Unknown;
    }
}
