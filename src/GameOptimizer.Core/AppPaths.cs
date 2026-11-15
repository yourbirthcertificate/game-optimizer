namespace GameOptimizer.Core;

/// <summary>Where the optimizer keeps its own data: %LOCALAPPDATA%\GameOpt (GAMEOPT_DATA_DIR overrides it, used by tests).</summary>
public static class AppPaths
{
    public static string DataDir =>
        Environment.GetEnvironmentVariable("GAMEOPT_DATA_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameOpt");

    public static string BackupsDir => Path.Combine(DataDir, "backups");
    public static string PrefsFile => Path.Combine(DataDir, "prefs.json");
}
