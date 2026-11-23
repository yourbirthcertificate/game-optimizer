using OptiGame.Core.Formats;
using OptiGame.Core.Hardware;
using OptiGame.Core.Optimize;

namespace OptiGame.Core.Recommend;

internal static class EldenRingSettingRecommender
{
    const int NativeFpsCap = 60;
    const int ReferenceFps = 29;
    static readonly RecommendationEvidence Benchmarks = new("Digital Trends",
        "The 2024 test averaged 29 FPS at 1440p Maximum on an 8 GB Radeon RX 580, Ryzen 7 1700X and 16 GB RAM. Its settings guide turns motion blur and depth of field off and lists a 60 FPS game cap.",
        "https://www.digitaltrends.com/computing/best-settings-for-elden-ring-pc-benchmarks-performance/");
    static readonly RecommendationEvidence OptionTokens = new("Elden Ring player XML",
        "A published GraphicsConfig.xml lists MotionBlur and DepthOfField with the DISABLE value.",
        "https://steamcommunity.com/app/1245620/discussions/0/3183488724676524144/");

    internal static SettingRecommendationReport Recommend(HardwareInfo hardware, OptimizeRequest request,
        ISettingsDocument settings, (int Width, int Height) currentResolution)
    {
        var (fps, source) = SettingRecommendationReport.ResolveTarget(request);
        (int Width, int Height) resolution = request.Resolution ?? currentResolution;
        if (fps > NativeFpsCap && request.RecommendationTargetFps is null && request.FpsMode != FpsMode.Custom)
        {
            fps = NativeFpsCap;
            source = "native game cap";
        }

        var report = new SettingRecommendationReport(fps, source, resolution) { Evidence = [Benchmarks] };
        if (fps is null)
            return report with { FallbackReason = "The FPS target is unknown. Choose a target explicitly or use a detected display." };
        if (fps > NativeFpsCap)
            return report with { FallbackReason = "The requested FPS is above the game's native 60 FPS cap." };
        if (resolution is not { Width: > 0, Height: > 0 })
            return report with { FallbackReason = "The target resolution could not be verified." };
        if (settings.GetRaw("xml|config|RaytracingQuality") != "DISABLE")
            return report with { FallbackReason = "Ray tracing off could not be verified in the current XML." };
        if (GpuMatcher.Identify(hardware.Gpu) is not { Score: > 0 } gpu || gpu.Kind == "generic" ||
            GpuMatcher.Identify("Radeon RX 580") is not { Kind: "exact", Score: > 0 } reference)
            return report with { FallbackReason = "A specific graphics score is unavailable for this PC or the tested card." };

        var work = fps.Value / ReferenceFps * Recommender.ResolutionFactor(resolution.Width, resolution.Height) /
            Recommender.ResolutionFactor(2560, 1440);
        var capacity = (double)gpu.Score / reference.Score;
        report = report with
        {
            Evidence = [Benchmarks, new("OptiGame+ GPU table", FormattableString.Invariant(
                $"{gpu.Name} has {capacity:0.###}x the score of the tested Radeon RX 580. The target implies {work:0.###}x its modeled GPU work."))],
            Assumptions =
            [
                "GPU scores compare relative graphics throughput, including across vendors. The target FPS and resolution scale the modeled work.",
                "The comparison uses a Maximum preset run as its reference. CPU, memory, laptop power limits and stutter are outside this estimate.",
                "Try the suggested options in game to check their effect. The comparison does not measure the FPS gain on this PC.",
            ],
        };
        if (work <= capacity)
            return report with { FallbackReason = "Modeled GPU work fits the published reference. The comparison gives no reason to lower these settings." };

        var advice = new List<SettingRecommendation>();
        var skipped = new List<string>();
        Add("MotionBlur", "Motion blur");
        Add("DepthOfField", "Depth of field");
        return report with
        {
            Settings = advice.ToArray(),
            Evidence = [.. report.Evidence, OptionTokens],
            Skipped = skipped.ToArray(),
            FallbackReason = advice.Count == 0 ? "No documented option could be verified for this configuration." : null,
        };

        void Add(string key, string label)
        {
            var id = "elden-ring:" + key;
            if (request.WhyKept(id, Categories.Quality) is { } kept)
            {
                skipped.Add(label + " is " + kept + ".");
                return;
            }

            var current = settings.GetRaw("xml|config|" + key);
            var currentLevel = current switch
            {
                "DISABLE" => "Off",
                "HIGH" => "High",
                "MAX" => "Maximum",
                _ => null,
            };
            if (currentLevel is null)
            {
                skipped.Add(label + " has no unique recognised value in the current GraphicsConfig.xml.");
                return;
            }

            advice.Add(new(id, label, currentLevel, "Off",
                "Modeled GPU work exceeds the reference. Digital Trends recommends turning this effect off."));
        }
    }
}
