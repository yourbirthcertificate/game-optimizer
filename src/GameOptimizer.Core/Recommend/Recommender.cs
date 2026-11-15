using System.Globalization;
using GameOptimizer.Core.Hardware;
using GameOptimizer.Core.Library;

namespace GameOptimizer.Core.Recommend;

/// <summary>The slider step suggested for one game on this PC, and why.</summary>
public sealed record GameRecommendation(QualityStep Step, string Reason, string Basis, double? Headroom = null);

/// <summary>
/// Per-game recommendation: how much faster this PC's graphics card is than the card the game's
/// publisher recommends, after allowing for a screen larger than the 1080p most requirement lists
/// assume. Without requirement data it falls back to the game's release year, then to the PC-wide step.
/// </summary>
public static class Recommender
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Headroom thresholds, highest step first. Calibrated so a card at about 0.75x the publisher's
    /// recommended card (after the resolution factor) gets High, which matched a hand-tuned
    /// Unreal 5 game on a 1440p laptop.
    /// </summary>
    static readonly (double min, QualityStep step)[] Bands =
    {
        (1.4, QualityStep.Ultra), (1.1, QualityStep.VeryHigh), (0.75, QualityStep.High),
        (0.55, QualityStep.Balanced), (0.35, QualityStep.Medium),
    };

    /// <summary>GPU work grows a bit slower than pixel count; 1440p costs about 1.5x 1080p.</summary>
    public static double ResolutionFactor(int w, int h) => Math.Pow(w * (double)h / (1920 * 1080), 0.75);

    public static QualityStep FromHeadroom(double h) =>
        Bands.FirstOrDefault(b => h >= b.min) is { min: > 0 } b ? b.step : QualityStep.Low;

    public static GameRecommendation Recommend(HardwareInfo hw, GameRequirements? req)
    {
        var mine = GpuMatcher.Identify(hw.Gpu);
        if (req is not null && mine is not null)
        {
            var res = ResolutionFactor(hw.NativeWidth, hw.NativeHeight);
            var rec = req.RecommendedGpu is null ? null : GpuMatcher.PickRequirement(GpuMatcher.Match(req.RecommendedGpu), mine.Vendor);
            if (rec is not null)
                return Make(mine.Score / (rec.Score * res), $"{req.Source} recommends {rec.Name}");
            // A minimum spec runs low settings; about 1.6x its card is a fair stand-in for "recommended".
            var min = req.MinimumGpu is null ? null : GpuMatcher.PickRequirement(GpuMatcher.Match(req.MinimumGpu), mine.Vendor);
            if (min is not null)
                return Make(mine.Score / (min.Score * 1.6 * res), $"{req.Source} lists {min.Name} as the minimum");

            GameRecommendation Make(double h, string why)
            {
                var step = Cap(FromHeadroom(h), hw);
                return new(step, $"{why}; this PC has {h.ToString("0.00", Inv)}x that at {hw.NativeWidth}x{hw.NativeHeight}", req.Source, h);
            }
        }
        if (req?.ReleaseYear is int year)
        {
            int bump = year <= 2015 ? 2 : year <= 2019 ? 1 : 0;
            var step = Cap((QualityStep)Math.Min((int)QualityStep.Ultra, (int)hw.Recommended + bump), hw);
            return new(step, bump == 0 ? $"Released {year}; no graphics card listed, so the PC-wide step"
                                       : $"Released {year} with no graphics card listed; older games get {bump} step(s) above the PC-wide step", "Release year");
        }
        return new(hw.Recommended, "No requirement data; using the PC-wide step", "This PC");
    }

    /// <summary>Very small graphics memory limits every game, however old.</summary>
    static QualityStep Cap(QualityStep s, HardwareInfo hw) =>
        hw.GpuVramGb is > 0 and <= 4.5 && s > QualityStep.Balanced ? QualityStep.Balanced : s;

    /// <summary>Recommendations for many games. Network lookups are paced; failures fall back quietly.</summary>
    public static async Task<Dictionary<string, GameRecommendation>> RecommendAllAsync(
        HardwareInfo hw, IEnumerable<GameInfo> games, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var list = games.ToList();
        var result = new Dictionary<string, GameRecommendation>();
        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var g = list[i];
            GameRequirements? req = null;
            if (g.SteamAppId is not null) req = await RequirementSource.ForSteamAppAsync(g.SteamAppId, ct);
            result[g.Id] = Recommend(hw, req);
            progress?.Report((i + 1, list.Count));
        }
        return result;
    }
}
