using System.Text.Json;
using GameOptimizer.Core.Hardware;

namespace GameOptimizer.Core.Optimize;

/// <summary>
/// Slider choices. Each game starts at its own recommended step; the global slider shifts every game
/// that you have not set by hand by the same number of steps. Per-game choices are absolute steps.
/// </summary>
public sealed class Prefs
{
    public const int MaxOffset = 3;

    /// <summary>-3 (more performance) to +3 (more quality); 0 = each game's recommendation.</summary>
    public int GlobalOffset { get; set; }
    public Dictionary<string, QualityStep> PerGame { get; set; } = new();
    /// <summary>Extra Game Spec Check cache folders to read requirement data from (e.g. a source checkout's .cache).</summary>
    public List<string> SharedCacheDirs { get; set; } = new();

    public static QualityStep Shift(QualityStep recommended, int offset) =>
        (QualityStep)Math.Clamp((int)recommended + offset, 0, QualitySteps.Count - 1);

    public QualityStep StepFor(string gameId, QualityStep recommended) =>
        PerGame.TryGetValue(gameId, out var s) ? s : Shift(recommended, GlobalOffset);

    public static string OffsetLabel(int offset) => offset switch
    {
        0 => "Recommended for each game",
        < 0 => $"{-offset} step{(offset < -1 ? "s" : "")} faster than recommended",
        _ => $"{offset} step{(offset > 1 ? "s" : "")} nicer than recommended",
    };

    public static Prefs Load()
    {
        try
        {
            if (File.Exists(AppPaths.PrefsFile))
            {
                var p = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(AppPaths.PrefsFile)) ?? new Prefs();
                p.GlobalOffset = Math.Clamp(p.GlobalOffset, -MaxOffset, MaxOffset);
                return p;
            }
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return new Prefs();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(AppPaths.PrefsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Points the requirement lookup at the shared cache folders saved here.</summary>
    public void ApplySharedCaches()
    {
        foreach (var d in SharedCacheDirs)
            if (!GameOptimizer.Core.Recommend.RequirementSource.SharedCacheDirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                GameOptimizer.Core.Recommend.RequirementSource.SharedCacheDirs.Add(d);
    }
}
