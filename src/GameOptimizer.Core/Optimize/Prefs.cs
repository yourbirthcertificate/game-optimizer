using System.Text.Json;
using GameOptimizer.Core.Hardware;

namespace GameOptimizer.Core.Optimize;

/// <summary>Slider choices: one global step (null = recommended) and steps you set by hand per game.</summary>
public sealed class Prefs
{
    public QualityStep? GlobalStep { get; set; }
    public Dictionary<string, QualityStep> PerGame { get; set; } = new();

    public QualityStep StepFor(string gameId, QualityStep recommended) =>
        PerGame.TryGetValue(gameId, out var s) ? s : GlobalStep ?? recommended;

    public static Prefs Load()
    {
        try
        {
            if (File.Exists(AppPaths.PrefsFile))
                return JsonSerializer.Deserialize<Prefs>(File.ReadAllText(AppPaths.PrefsFile)) ?? new Prefs();
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return new Prefs();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(AppPaths.PrefsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
