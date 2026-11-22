using System.Globalization;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;

namespace OptiGame.Core.Recommend;

/// <summary>What a recommendation rests on, strongest first. No percentages: these are kinds of evidence.</summary>
public enum RecommendationBasis { PublishedRecommended, MinimumEstimate, ReleaseYear, PcWide, UnknownGpu }

/// <summary>The slider step suggested for one game on this PC, why, and how much to trust it.</summary>
public sealed record GameRecommendation(QualityStep Step, string Reason, string Basis, double? Headroom = null)
{
    public RecommendationBasis Kind { get; init; } = RecommendationBasis.PcWide;
    /// <summary>Assumptions the number depends on.</summary>
    public List<string> Assumptions { get; init; } = new();
    /// <summary>Things that could make the step wrong and that the data cannot rule out.</summary>
    public List<string> Caveats { get; init; } = new();
    public DateTime? DataFetchedUtc { get; init; }
    /// <summary>A measured benchmark for this game, if one is marked preferred. Shown beside the estimate; never replaces it.</summary>
    public string? Measured { get; set; }

    public string KindLabel => Kind switch
    {
        RecommendationBasis.PublishedRecommended => "Publisher's recommended card",
        RecommendationBasis.MinimumEstimate => "Estimate from minimum specs",
        RecommendationBasis.ReleaseYear => "Release-year fallback",
        RecommendationBasis.PcWide => "PC-wide fallback",
        _ => "GPU not recognized",
    };

    /// <summary>Short chip text: "Published", "Min. spec", "Year", "PC-wide", "Unknown GPU".</summary>
    public string Chip => Kind switch
    {
        RecommendationBasis.PublishedRecommended => "Published",
        RecommendationBasis.MinimumEstimate => "Min. spec",
        RecommendationBasis.ReleaseYear => "Year",
        RecommendationBasis.PcWide => "PC-wide",
        _ => "Unknown GPU",
    };

    public string DataAge => DataFetchedUtc is { } t
        ? (DateTime.UtcNow - t).TotalDays switch { < 1 => "fetched today", var d => $"fetched {Math.Floor(d)} day(s) ago" }
        : "no cached data";

    public string Details => string.Join("\n", new[] { $"{KindLabel}. {Reason}", $"Data: {Basis}, {DataAge}." }
        .Concat(Assumptions.Select(a => "Assumes: " + a))
        .Concat(Caveats.Select(c => "Cannot tell: " + c))
        .Concat(Measured is null ? Array.Empty<string>() : new[] { "Measured on this PC: " + Measured }));
}

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

    public static GameRecommendation Recommend(HardwareInfo hw, GameRequirements? req) =>
        Recommend(hw, req, (hw.NativeWidth, hw.NativeHeight));

    public static GameRecommendation Recommend(HardwareInfo hw, GameRequirements? req, (int w, int h) target)
    {
        var mine = GpuMatcher.Identify(hw.Gpu);
        var caveats = new List<string>
        {
            "whether the game is limited by the processor rather than the graphics card",
            "heavy optional features such as ray tracing; requirement lists rarely cover them",
        };
        if (hw.IsLaptop || hw.Gpu.Contains("Laptop", StringComparison.OrdinalIgnoreCase))
            caveats.Add("the laptop GPU's power limit; the score assumes a typical one for this chip");
        var assume = new List<string>
        {
            "the publisher's 'recommended' spec means high settings at 1080p",
            $"{target.w}x{target.h} costs {ResolutionFactor(target.w, target.h).ToString("0.00", Inv)}x the work of 1080p",
        };

        if (mine is null)
            return new(hw.Recommended, $"This PC's graphics card ({hw.Gpu}) is not in the score table, so no per-game comparison is possible; using the PC-wide step.",
                       req?.Source ?? "none")
            { Kind = RecommendationBasis.UnknownGpu, Caveats = caveats, DataFetchedUtc = req?.FetchedUtc };
        if (mine.Kind == "generic")
            caveats.Add($"this PC's card was only recognized as '{mine.Name}'");

        if (req is not null)
        {
            var res = ResolutionFactor(target.w, target.h);
            var recCands = req.RecommendedGpu is null ? new() : GpuMatcher.Match(req.RecommendedGpu);
            var rec = GpuMatcher.PickRequirement(recCands, mine.Vendor);
            if (rec is not null)
                return Make(mine.Score / (rec.Score * res), $"{req.Source} recommends {rec.Name}", RecommendationBasis.PublishedRecommended, recCands, rec);
            var minCands = req.MinimumGpu is null ? new() : GpuMatcher.Match(req.MinimumGpu);
            var min = GpuMatcher.PickRequirement(minCands, mine.Vendor);
            if (min is not null)
            {
                assume.Add("about 1.6x the minimum card stands in for the missing recommended card");
                return Make(mine.Score / (min.Score * 1.6 * res), $"{req.Source} lists {min.Name} as the minimum", RecommendationBasis.MinimumEstimate, minCands, min);
            }

            GameRecommendation Make(double h, string why, RecommendationBasis kind, List<GpuMatch> cands, GpuMatch picked)
            {
                if (picked.Kind == "generic") caveats.Add($"the listed card was only matched as '{picked.Name}'");
                if (cands.Select(c => c.Vendor).Distinct().Count() > 1)
                    assume.Add($"the {picked.Vendor.ToUpperInvariant()} card in the list is the fair comparison for this PC");
                var step = Cap(FromHeadroom(h), hw);
                return new(step, $"{why}; this PC has {h.ToString("0.00", Inv)}x that at {target.w}x{target.h}", req.Source, h)
                { Kind = kind, Assumptions = assume, Caveats = caveats, DataFetchedUtc = req.FetchedUtc };
            }
        }
        if (req?.ReleaseYear is int year)
        {
            int bump = year <= 2015 ? 2 : year <= 2019 ? 1 : 0;
            var step = Cap((QualityStep)Math.Min((int)QualityStep.Ultra, (int)hw.Recommended + bump), hw);
            return new(step, bump == 0 ? $"Released {year}; no graphics card listed, so the PC-wide step"
                                       : $"Released {year} with no graphics card listed; older games get {bump} step(s) above the PC-wide step", "Release year")
            {
                Kind = RecommendationBasis.ReleaseYear, DataFetchedUtc = req.FetchedUtc, Caveats = caveats,
                Assumptions = new() { "older games ask less of current graphics cards" },
            };
        }
        return new(hw.Recommended, "No requirement data; using the PC-wide step", "This PC")
        { Kind = RecommendationBasis.PcWide, Caveats = caveats };
    }

    /// <summary>Very small graphics memory limits every game, however old.</summary>
    static QualityStep Cap(QualityStep s, HardwareInfo hw) =>
        hw.GpuVramGb is > 0 and <= 4.5 && s > QualityStep.Balanced ? QualityStep.Balanced : s;

    /// <summary>
    /// Recommendations for many games. Network lookups are paced; failures fall back quietly.
    /// appIdFor lets a manual or GOG game use a Steam app id the user linked to it.
    /// </summary>
    public static async Task<Dictionary<string, GameRecommendation>> RecommendAllAsync(
        HardwareInfo hw, IEnumerable<GameInfo> games, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default,
        Func<GameInfo, string?>? appIdFor = null, Func<GameInfo, (int w, int h)>? targetFor = null)
    {
        var list = games.ToList();
        var result = new Dictionary<string, GameRecommendation>();
        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var g = list[i];
            GameRequirements? req = null;
            var appId = appIdFor?.Invoke(g) ?? g.SteamAppId;
            if (appId is not null) req = await RequirementSource.ForSteamAppAsync(appId, ct);
            result[g.Id] = Recommend(hw, req, targetFor?.Invoke(g) ?? (hw.NativeWidth, hw.NativeHeight));
            progress?.Report((i + 1, list.Count));
        }
        return result;
    }
}
