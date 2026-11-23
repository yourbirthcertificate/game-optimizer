using System.Globalization;
using OptiGame.Core.Formats;
using OptiGame.Core.Hardware;
using OptiGame.Core.Optimize;

namespace OptiGame.Core.Recommend;

internal static class CyberpunkSettingRecommender
{
    const int ReferenceFps = 60;
    const int VramMinimum = 8;
    const int RamMinimum = 16;
    const string Advanced = "/graphics/advanced";
    const string Game = "cyberpunk-2077";
    const string RasterGuide = "pcgamer";
    const string LaunchBenchmark = "toms";

    internal static SettingRecommendationReport Recommend(HardwareInfo hardware, OptimizeRequest request, RedSettingsFile file)
    {
        var (fps, source) = SettingRecommendationReport.ResolveTarget(request);
        var report = new SettingRecommendationReport(fps, source, request.Resolution) { Priority = request.RecommendationPriority };
        SettingRecommendationReport Fallback(string reason, params RecommendationEvidence[] evidence) =>
            report with { FallbackReason = reason, Evidence = evidence };

        if (report.Priority == RecommendationPriority.Quiet)
            return Fallback("Quiet priority needs measured noise or power data for these settings.");
        if (fps is null)
            return Fallback("The FPS target is unknown. Choose a target explicitly or use a detected display.");
        if (request.Resolution is not { w: > 0, h: > 0 } resolution)
            return Fallback("A target resolution is unavailable. The current game's resolution was kept or could not be resolved.");
        if (string.IsNullOrEmpty(request.Display.DeviceName))
            return Fallback("The target display could not be verified.");
        var guides = GuidePack.Reviewed;
        if (guides is null || guides.FindReviewedSource("cdpr") is not { } requirements ||
            guides.FindReviewedSource(RasterGuide) is not { } rasterGuide)
            return Fallback("The reviewed Cyberpunk guide sources are unavailable.");
        var requirementsEvidence = requirements.ToEvidence();

        if (!RayTracingIsOff(file))
            return Fallback("The published reference is for ray tracing off, which could not be verified in the current settings.", requirementsEvidence);

        if (GpuMatcher.Identify(hardware.Gpu) is not { Score: > 0 } gpu || gpu.Kind == "generic")
            return Fallback("The graphics card has no specific score in the local table.", requirementsEvidence);

        var referenceName = gpu.Vendor switch
        {
            "nvidia" => "GeForce RTX 2060 Super",
            "amd" => "Radeon RX 5700 XT",
            "intel" => "Arc A770",
            _ => null,
        };
        if (referenceName is null || GpuMatcher.Identify(referenceName) is not { Kind: "exact", Score: > 0 } reference)
            return Fallback("No same-vendor GPU reference is available in the local table.", requirementsEvidence);

        var specs = new RecommendationEvidence("This PC", FormattableString.Invariant(
            $"{hardware.Gpu}, {hardware.GpuVramGb:0.#} GB VRAM, {hardware.RamGb:0.#} GB RAM, CPU {hardware.Cpu}."));
        if (!double.IsFinite(hardware.GpuVramGb) || !double.IsFinite(hardware.RamGb) ||
            hardware.GpuVramGb <= 0 || hardware.RamGb <= 0)
            return Fallback("Detected VRAM and RAM are needed for this comparison.", requirementsEvidence, specs);
        // Windows can report slightly less usable memory than the rated capacity.
        if (Math.Round(hardware.GpuVramGb, MidpointRounding.AwayFromZero) < VramMinimum ||
            Math.Round(hardware.RamGb, MidpointRounding.AwayFromZero) < RamMinimum)
            return Fallback("The PC is below the memory listed for the High reference. These option tests do not establish what will make up that deficit.", requirementsEvidence, specs);

        // Use the existing resolution model and compare only relative GPU work. Do not predict FPS.
        var work = fps.Value / ReferenceFps * Recommender.ResolutionFactor(resolution.w, resolution.h);
        var capacity = (double)gpu.Score / reference.Score;
        if (!double.IsFinite(work) || work <= 0)
            return Fallback("The requested target is outside the supported comparison range.", requirementsEvidence, specs);

        var evidence = new List<RecommendationEvidence>
        {
            requirementsEvidence, rasterGuide.ToEvidence(), specs,
            new("OptiGame+ GPU table", FormattableString.Invariant(
                $"{gpu.Name} has {capacity:0.###}x the graphics score of the published {reference.Name} reference. The requested resolution and FPS imply {work:0.###}x its modeled GPU work.")),
        };
        var assumptions = new List<string>
        {
            "The comparison scales GPU work with the target FPS and OptiGame+'s existing resolution model.",
            "The chosen resolution is a scenario. CPU throughput, power limits, upscaling, and frame generation are outside this comparison.",
            "The modeled comparison is a starting point for testing. Any local benchmark evidence describes its own capture conditions.",
        };
        if (gpu.Kind == "variant")
            assumptions.Add("The GPU score is the existing laptop estimate derived from its desktop counterpart. Actual laptop power limits vary.");
        else if (hardware.IsLaptop)
            assumptions.Add("The graphics score does not account for this laptop's power limit.");

        var settings = new List<SettingRecommendation>();
        var skipped = new List<string>();
        var underPressure = work > capacity;
        if (underPressure && report.Priority == RecommendationPriority.Look)
            return report with
            {
                Evidence = evidence.ToArray(),
                Assumptions = assumptions.ToArray(),
                FallbackReason = "Look priority keeps current visual settings when the modeled FPS target calls for lower settings.",
            };

        var useTrials = underPressure || report.Priority == RecommendationPriority.Speed;
        const string volumetricTrial = "Try Medium first when modeled GPU work exceeds the reference. PC Gamer recommends reducing fog and clouds before reflections.";
        const string cascadedTrial = "PC Gamer uses Medium in its raster guide. Use that as a trial level when modeled GPU work exceeds the reference.";
        Add("VolumetricFogResolution", "Volumetric fog resolution", volumetricTrial, "Medium");
        Add("VolumetricCloudsQuality", "Volumetric cloud quality", volumetricTrial, "Medium");
        const string reflections = "ScreenSpaceReflectionsQuality";
        if (guides.FindReviewedValue(Game, reflections, LaunchBenchmark) is { } reflectionTrial)
            Add(reflections, "Screen space reflections",
                "If you still need performance after fog and clouds, try the launch-test value. PC Gamer found a visible loss of reflection quality when lowering this setting.",
                reflectionTrial, trialEvidence: guides.FindReviewedSource(LaunchBenchmark));
        else
            Add(reflections, "Screen space reflections");

        Add("CascadedShadowsRange", "Cascaded shadows range", cascadedTrial);
        Add("CascadedShadowsResolution", "Cascaded shadows resolution", cascadedTrial);
        Add("Anisotropy", "Anisotropy", isNumeric: true);
        Add("ShadowMeshQuality", "Local shadow mesh quality");
        Add("LocalShadowsQuality", "Local shadow quality");
        Add("DistantShadowsResolution", "Distant shadows resolution");
        Add("MaxDynamicDecals", "Max dynamic decals");
        Add("SubsurfaceScatteringQuality", "Subsurface scattering quality");
        Add("AmbientOcclusion", "Ambient occlusion");
        Add("ColorPrecision", "Color precision");
        Add("MirrorQuality", "Mirror quality");
        Add("LODPreset", "Level of detail");

        skipped.Add("Crowd density has no CPU budget in this estimate.");
        skipped.Add("Texture quality and global illumination range have no verified values in the cited raster guide.");
        return report with
        {
            Settings = settings.ToArray(), Evidence = evidence.ToArray(), Assumptions = assumptions.ToArray(),
            Skipped = skipped.ToArray(),
            FallbackReason = settings.Count == 0 ? "No setting with published guidance could be recommended for this configuration." : null,
        };

        void Add(string name, string label, string? pressureReason = null, string? trialValue = null,
            bool isNumeric = false, GuideSource? trialEvidence = null)
        {
            var id = Planner.RedId(Advanced, name);
            if (request.WhyKept(id, Categories.Quality) is { } kept)
            {
                skipped.Add(label + " is " + kept + ".");
                return;
            }

            if (guides.FindReviewedValue(Game, name, RasterGuide) is not { } referenceValue)
            {
                skipped.Add(label + " has no value in the reviewed raster guide.");
                return;
            }

            if (useTrials && pressureReason is null)
            {
                skipped.Add(label + " has only a published reference value for this comparison.");
                return;
            }

            var target = useTrials ? trialValue ?? referenceValue : referenceValue;
            var targetRaw = isNumeric ? target : Quoted(target);
            var expectedType = isNumeric ? "int_list" : "string_list";
            var matches = file.Options.Where(option => option.Group == Advanced && option.Name == name).ToArray();
            var current = file.GetRaw(Advanced, name);
            if (matches is not [var option] || option.IsDynamic || option.Type != expectedType ||
                current is null || !IsKnownValue(current, isNumeric) ||
                option.Values.Count(v => v == current) != 1 || option.Values.Count(v => v == targetRaw) != 1)
            {
                skipped.Add(label + " has no unique current value and suggested choice in this build.");
                return;
            }

            var selected = isNumeric ? current : current[1..^1];
            if (report.Priority == RecommendationPriority.Look && QualityRank(selected) > QualityRank(target))
            {
                settings.Add(new(id, label, selected, selected, "Look priority keeps the current higher quality setting."));
                return;
            }

            if (useTrials && selected is ("Off" or "Low"))
            {
                settings.Add(new(id, label, selected, selected, "Keep the current lower choice while favoring performance."));
                return;
            }

            var reason = useTrials
                ? underPressure ? pressureReason! : "Speed priority favors this trial value for additional rendering headroom."
                : "Modeled GPU work fits the reference. This is the value in PC Gamer's raster settings guide.";
            settings.Add(new(id, label, selected, target, reason));
            if (useTrials && trialEvidence is not null)
                evidence.Add(trialEvidence.ToEvidence());
        }
    }

    static bool RayTracingIsOff(RedSettingsFile file)
    {
        const string group = "/graphics/raytracing";
        var flags = file.Options.Where(option => option.Group == group).ToArray();
        return flags.Count(option => option.Name == "RayTracing") == 1 &&
            file.GetRaw(group, "RayTracing") == "false" &&
            (flags.Count(option => option.Name == "RayTracedPathTracing") switch
            {
                0 => true,
                1 => file.GetRaw(group, "RayTracedPathTracing") == "false",
                _ => false,
            });
    }

    static bool IsKnownValue(string raw, bool isNumeric) => isNumeric
        ? int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out _)
        : raw is ("\"Off\"" or "\"Low\"" or "\"Medium\"" or "\"High\"" or "\"Ultra\"" or "\"Insane\"");

    static int QualityRank(string value) => value switch
    {
        "Off" => 0, "Low" => 1, "Medium" => 2, "High" => 3, "Ultra" => 4, "Insane" => 5,
        _ => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var level) ? level : -1,
    };

    static string Quoted(string value) => "\"" + value + "\"";

}
