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
    const string RequirementsUrl = "https://support.cdprojektred.com/en/cyberpunk/pc/sp-technical/issue/1556/cyberpunk-2077-system-requirements";
    const string OptionTestsUrl = "https://www.pcgamer.com/the-best-settings-for-cyberpunk-2077/";
    static readonly RecommendationEvidence Requirements = new("CD PROJEKT RED",
        "The recommended non-ray-tracing tier for the base game from Update 2.0 and Phantom Liberty lists High at 1080p, 60 expected FPS, 8 GB VRAM, and 16 GB RAM.", RequirementsUrl);
    static readonly RecommendationEvidence ReflectionTests = new("Tom's Hardware",
        "The 2020 launch-version raster test used Medium screen space reflections. Use it as a trial level for Update 2.0 and later.",
        "https://www.tomshardware.com/news/cyberpunk-2077-pc-benchmarks-settings-performance-analysis");
    static readonly RecommendationEvidence OptionTests = new("PC Gamer",
        "The Update 2.0 raster guide supplies the reference values. It suggests turning down fog and clouds first because it found a smaller visual impact than lowering screen space reflections.", OptionTestsUrl);

    internal static SettingRecommendationReport Recommend(HardwareInfo hardware, OptimizeRequest request, RedSettingsFile file)
    {
        var (fps, source) = Target(request);
        var report = new SettingRecommendationReport(fps, source, request.Resolution);
        SettingRecommendationReport Fallback(string reason, params RecommendationEvidence[] evidence) =>
            report with { FallbackReason = reason, Evidence = evidence };

        if (fps is null)
            return Fallback("The FPS target is unknown. Choose a target explicitly or use a detected display.");
        if (request.Resolution is not { w: > 0, h: > 0 } resolution)
            return Fallback("A target resolution is unavailable. The current game's resolution was kept or could not be resolved.");
        if (string.IsNullOrEmpty(request.Display.DeviceName))
            return Fallback("The target display could not be verified.");
        if (!RayTracingIsOff(file))
            return Fallback("The published reference is for ray tracing off, which could not be verified in the current settings.", Requirements);

        if (GpuMatcher.Identify(hardware.Gpu) is not { Score: > 0 } gpu || gpu.Kind == "generic")
            return Fallback("The graphics card has no specific score in the local table.", Requirements);

        var referenceName = gpu.Vendor switch
        {
            "nvidia" => "GeForce RTX 2060 Super",
            "amd" => "Radeon RX 5700 XT",
            "intel" => "Arc A770",
            _ => null,
        };
        if (referenceName is null || GpuMatcher.Identify(referenceName) is not { Kind: "exact", Score: > 0 } reference)
            return Fallback("No same-vendor GPU reference is available in the local table.", Requirements);

        var specs = new RecommendationEvidence("This PC", FormattableString.Invariant(
            $"{hardware.Gpu}, {hardware.GpuVramGb:0.#} GB VRAM, {hardware.RamGb:0.#} GB RAM, CPU {hardware.Cpu}."));
        if (!double.IsFinite(hardware.GpuVramGb) || !double.IsFinite(hardware.RamGb) ||
            hardware.GpuVramGb <= 0 || hardware.RamGb <= 0)
            return Fallback("Detected VRAM and RAM are needed for this comparison.", Requirements, specs);
        // Windows can report slightly less usable memory than the rated capacity.
        if (Math.Round(hardware.GpuVramGb, MidpointRounding.AwayFromZero) < VramMinimum ||
            Math.Round(hardware.RamGb, MidpointRounding.AwayFromZero) < RamMinimum)
            return Fallback("The PC is below the memory listed for the High reference. These option tests do not establish what will make up that deficit.", Requirements, specs);

        // Use the existing resolution model and compare only relative GPU work. Do not predict FPS.
        var work = fps.Value / ReferenceFps * Recommender.ResolutionFactor(resolution.w, resolution.h);
        var capacity = (double)gpu.Score / reference.Score;
        if (!double.IsFinite(work) || work <= 0)
            return Fallback("The requested target is outside the supported comparison range.", Requirements, specs);

        var evidence = new List<RecommendationEvidence>
        {
            Requirements, OptionTests, specs,
            new("OptiGame+ GPU table", FormattableString.Invariant(
                $"{gpu.Name} has {capacity:0.###}x the graphics score of the published {reference.Name} reference. The requested resolution and FPS imply {work:0.###}x its modeled GPU work.")),
        };
        var assumptions = new List<string>
        {
            "The comparison scales GPU work with the target FPS and OptiGame+'s existing resolution model.",
            "The chosen resolution is a scenario. CPU throughput, power limits, upscaling, and frame generation are outside this comparison.",
            "This is a starting point for testing, with no measured FPS from this PC.",
        };
        if (gpu.Kind == "variant")
            assumptions.Add("The GPU score is the existing laptop estimate derived from its desktop counterpart. Actual laptop power limits vary.");
        else if (hardware.IsLaptop)
            assumptions.Add("The graphics score does not account for this laptop's power limit.");

        var settings = new List<SettingRecommendation>();
        var skipped = new List<string>();
        var underPressure = work > capacity;
        const string volumetricTrial = "Try Medium first when modeled GPU work exceeds the reference. PC Gamer recommends reducing fog and clouds before reflections.";
        const string cascadedTrial = "PC Gamer uses Medium in its raster guide. Use that as a trial level when modeled GPU work exceeds the reference.";
        Add("VolumetricFogResolution", "Volumetric fog resolution", "High", volumetricTrial);
        Add("VolumetricCloudsQuality", "Volumetric cloud quality", "High", volumetricTrial);
        Add("ScreenSpaceReflectionsQuality", "Screen space reflections", "High",
            "If you still need performance after fog and clouds, try Medium. PC Gamer found a visible loss of reflection quality when lowering this setting.");
        Add("CascadedShadowsRange", "Cascaded shadows range", "Medium", cascadedTrial);
        Add("CascadedShadowsResolution", "Cascaded shadows resolution", "Medium", cascadedTrial);

        Add("Anisotropy", "Anisotropy", "8", isNumeric: true);
        Add("ShadowMeshQuality", "Local shadow mesh quality", "High");
        Add("LocalShadowsQuality", "Local shadow quality", "High");
        Add("DistantShadowsResolution", "Distant shadows resolution", "High");
        Add("MaxDynamicDecals", "Max dynamic decals", "High");
        Add("SubsurfaceScatteringQuality", "Subsurface scattering quality", "High");
        Add("AmbientOcclusion", "Ambient occlusion", "High");
        Add("ColorPrecision", "Color precision", "Medium");
        Add("MirrorQuality", "Mirror quality", "High");
        Add("LODPreset", "Level of detail", "High");

        skipped.Add("Crowd density has no CPU budget in this estimate.");
        skipped.Add("Texture quality and global illumination range have no verified values in the cited raster guide.");
        return report with
        {
            Settings = settings.ToArray(), Evidence = evidence.ToArray(), Assumptions = assumptions.ToArray(),
            Skipped = skipped.ToArray(),
            FallbackReason = settings.Count == 0 ? "No setting with published guidance could be recommended for this configuration." : null,
        };

        void Add(string name, string label, string referenceValue, string? pressureReason = null, bool isNumeric = false)
        {
            var id = Planner.RedId(Advanced, name);
            if (request.WhyKept(id, Categories.Quality) is { } kept)
            {
                skipped.Add(label + " is " + kept + ".");
                return;
            }

            if (underPressure && pressureReason is null)
            {
                skipped.Add(label + " has only a published reference value for this comparison.");
                return;
            }

            var target = underPressure ? "Medium" : referenceValue;
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
            if (underPressure && selected is ("Off" or "Low"))
            {
                settings.Add(new(id, label, selected, selected, "Keep the current lower choice while modeled GPU work exceeds the reference."));
                return;
            }

            var reason = underPressure
                ? pressureReason!
                : "Modeled GPU work fits the reference. This is the value in PC Gamer's raster settings guide.";
            settings.Add(new(id, label, selected, target, reason));
            if (underPressure && name == "ScreenSpaceReflectionsQuality")
                evidence.Add(ReflectionTests);
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

    static string Quoted(string value) => "\"" + value + "\"";

    static (double? Fps, string? Source) Target(OptimizeRequest request)
    {
        if (request.RecommendationTargetFps is double explicitTarget)
            return double.IsFinite(explicitTarget) && explicitTarget > 0 ? (explicitTarget, "chosen target") : (null, null);
        if (request.FpsMode == FpsMode.Custom && request.FpsCap is double cap && double.IsFinite(cap) && cap > 0)
            return (cap, "custom frame cap");
        if (string.IsNullOrEmpty(request.Display.DeviceName) || !double.IsFinite(request.Display.CurrentHz) ||
            request.Display.CurrentHz <= 3 || request.Display.CurrentHz >= int.MaxValue)
            return (null, null);
        return (request.Display.AutoFpsCap, "active display auto cap");
    }
}
