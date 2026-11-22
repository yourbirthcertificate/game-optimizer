namespace OptiGame.Core.Hardware;

/// <summary>The six slider positions, from fastest to best looking.</summary>
public enum QualityStep
{
    Low = 0,
    Medium = 1,
    Balanced = 2,
    High = 3,
    VeryHigh = 4,
    Ultra = 5,
}

public static class QualitySteps
{
    public const int Count = 6;

    public static string Label(this QualityStep s) => s switch
    {
        QualityStep.VeryHigh => "Very high",
        _ => s.ToString(),
    };

    public static bool TryParse(string text, out QualityStep step)
    {
        var t = text.Replace(" ", "").Replace("-", "").Replace("_", "");
        if (int.TryParse(t, out var i) && i >= 0 && i < Count) { step = (QualityStep)i; return true; }
        return Enum.TryParse(t, ignoreCase: true, out step) && Enum.IsDefined(step);
    }

    /// <summary>Spread the six steps over a game's own level count (e.g. 4 Unity quality levels).</summary>
    public static int MapToLevels(this QualityStep s, int levelCount)
    {
        if (levelCount <= 1) return 0;
        return (int)Math.Round((int)s / 5.0 * (levelCount - 1), MidpointRounding.AwayFromZero);
    }
}
