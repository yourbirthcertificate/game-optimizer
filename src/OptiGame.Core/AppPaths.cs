namespace OptiGame.Core;

/// <summary>
/// Where OptiGame+ keeps its own data: %LOCALAPPDATA%\GameOpt. The folder keeps the name the optimizer
/// always used, so existing backups, history, preferences and benchmarks are found without any move.
/// --data-dir (or GAMEOPT_DATA_DIR / OPTIGAME_DATA_DIR) points it somewhere else, which is how tests run.
/// </summary>
public static class AppPaths
{
    /// <summary>Set by the app's --data-dir switch; wins over the environment variables.</summary>
    public static string? Override { get; set; }

    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameOpt");

    public static string DataDir =>
        Override is { Length: > 0 } o ? o
        : Environment.GetEnvironmentVariable("GAMEOPT_DATA_DIR") is { Length: > 0 } d ? d
        : Environment.GetEnvironmentVariable("OPTIGAME_DATA_DIR") is { Length: > 0 } d2 ? d2
        : DefaultDataDir;

    /// <summary>True when the data folder is the real one (not a test or --data-dir folder).</summary>
    public static bool IsDefault => string.Equals(
        Path.GetFullPath(DataDir).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(DefaultDataDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    public static string BackupsDir => Path.Combine(DataDir, "backups");
    public static string PrefsFile => Path.Combine(DataDir, "prefs.json");
}
