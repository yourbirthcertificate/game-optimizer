using System.Globalization;
using OptiGame.Core.Formats;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;

namespace OptiGame.Core.Optimize;

/// <summary>
/// REDengine 4 (Cyberpunk 2077): %LOCALAPPDATA%\CD Projekt Red\Cyberpunk 2077\UserSettings.json. Only options the file
/// describes with a "values" list (or a plain bool or int) are changed, so no index is ever guessed. The graphics
/// quality options follow the six steps; ray tracing is only ever switched off (at Low and Medium), never on; frame cap
/// uses MaximumFPS_*. Resolution, window mode, VSync, upscaling and frame generation are lists the game builds at run
/// time for this PC, so they stay as they are and the plan says so.
/// </summary>
public static partial class Planner
{
    /// <summary>The LocalAppData folder used to find settings files. Tests point it at a temporary folder.</summary>
    internal static string LocalAppData { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string RedSettingsPath(GameInfo g) => RedSettingsPath(g, LocalAppData);

    internal static string RedSettingsPath(GameInfo g, string local) =>
        Path.Combine(local, "CD Projekt Red", string.IsNullOrEmpty(g.EngineDetail) ? "Cyberpunk 2077" : g.EngineDetail, "UserSettings.json");

    /// <summary>(group, option, category) for every list option that follows the quality step. Anisotropy is a list of numbers.</summary>
    static readonly (string Group, string Name, string Category, int Skip)[] RedQualityOptions =
    {
        ("/graphics/presets", "TextureQuality", Categories.Texture, 0),
        ("/graphics/advanced", "Anisotropy", Categories.Quality, 0),
        ("/graphics/advanced", "ShadowMeshQuality", Categories.Quality, 0),
        ("/graphics/advanced", "LocalShadowsQuality", Categories.Quality, 0),
        ("/graphics/advanced", "CascadedShadowsRange", Categories.Quality, 0),
        ("/graphics/advanced", "CascadedShadowsResolution", Categories.Quality, 0),
        ("/graphics/advanced", "DistantShadowsResolution", Categories.Quality, 0),
        ("/graphics/advanced", "VolumetricFogResolution", Categories.Quality, 0),
        ("/graphics/advanced", "VolumetricCloudsQuality", Categories.Quality, 0),
        ("/graphics/advanced", "MaxDynamicDecals", Categories.Quality, 0),
        ("/graphics/advanced", "ScreenSpaceReflectionsQuality", Categories.Quality, 1),   // never "Insane"
        ("/graphics/advanced", "SubsurfaceScatteringQuality", Categories.Quality, 0),
        ("/graphics/advanced", "AmbientOcclusion", Categories.Quality, 0),
        ("/graphics/advanced", "ColorPrecision", Categories.Quality, 0),
        ("/graphics/advanced", "GlobaIlluminationRange", Categories.Quality, 0),          // (sic) the game's own spelling
        ("/graphics/advanced", "MirrorQuality", Categories.Quality, 0),
        ("/graphics/advanced", "LODPreset", Categories.Quality, 0),
        ("/graphics/performance", "CrowdDensity", Categories.Quality, 0),
        ("/gameplay/performance", "CrowdDensity", Categories.Quality, 0),                 // the game keeps a copy here too
    };

    static string RedId(string group, string name) => "red:" + group.Trim('/').Replace('/', '.') + "." + name;

    static string Unquote(string raw) => raw.Length >= 2 && raw[0] == '"' ? raw[1..^1] : raw;

    static void RedEngine(GamePlan plan, HardwareInfo hw, OptimizeRequest req)
    {
        var g = plan.Game;
        var path = RedSettingsPath(g);
        if (!File.Exists(path))
        {
            var folder = Path.GetDirectoryName(path)!;
            plan.Skip(SkipKind.NotCreatedYet, "No UserSettings.json yet. Start the game once so it creates its settings.", folder);
            return;
        }
        plan.SettingsFolder = Path.GetDirectoryName(path);
        byte[] original = File.ReadAllBytes(path);
        RedSettingsFile file;
        try { file = FormatHandlers.Red.Read(original).Content; }
        catch (FormatException e) { plan.Skip(SkipKind.UnsupportedFormat, "UserSettings.json could not be read: " + e.Message, plan.SettingsFolder); return; }
        if (file.Options.Count == 0) { plan.Skip(SkipKind.UnsupportedFormat, "UserSettings.json has no options this tool recognises", plan.SettingsFolder); return; }

        plan.Examined = true;
        var edit = new FileEdit { Path = path, Original = original, Updated = original, Format = "red" };

        void Apply(string group, string name, string newRaw, string label, string category)
        {
            var oldRaw = file.GetRaw(group, name);
            if (oldRaw is null) return;                                   // this build of the game has no such option
            if (oldRaw == newRaw) return;
            var id = RedId(group, name);
            if (!Want(plan, req, id, category, label, Unquote(oldRaw), Unquote(newRaw))) return;
            if (!file.TrySet(group, name, newRaw, out var why)) { plan.Skipped.Add("Kept: " + why); return; }
            edit.Changes.Add(new SettingChange(label, Unquote(oldRaw), Unquote(newRaw))
            { Id = id, Category = category, Locator = $"red|{group}|{name}", OldRaw = oldRaw, NewRaw = newRaw });
        }

        // quality options: spread the six steps over each option's own list
        long vramMb = (long)(hw.GpuVramGb * 1024);
        foreach (var (group, name, category, skipTop) in RedQualityOptions)
        {
            var opt = file.Find(group, name);
            if (opt is null || opt.Values.Count == 0) continue;
            int levels = Math.Max(1, opt.Values.Count - skipTop);
            int idx = req.Step.MapToLevels(levels);
            if (name == "TextureQuality")
            {
                // Textures cost memory, not much speed: only Low and Medium go below the top level, and graphics memory caps it.
                idx = req.Step == QualityStep.Low ? 0 : req.Step == QualityStep.Medium ? Math.Min(1, levels - 1) : levels - 1;
                if (vramMb > 0 && vramMb <= 6656) idx = Math.Min(idx, Math.Max(0, levels - 2));      // 6.5 GB or less: not the top level
                if (vramMb > 0 && vramMb <= 4608) idx = Math.Min(idx, 1);                              // 4.5 GB or less: at most Medium
            }
            Apply(group, name, opt.Values[idx], group == "/gameplay/performance" ? name + " (copy in gameplay settings)" : name, category);
        }

        // frame cap
        if (file.Find("/video/display", "MaximumFPS_OnOff") is not null && req.FpsMode != FpsMode.Keep)
        {
            if (req.FpsMode == FpsMode.Uncapped) Apply("/video/display", "MaximumFPS_OnOff", "false", "MaximumFPS_OnOff", Categories.FpsCap);
            else if (req.FpsCap is { } cap)
            {
                int fps = (int)Math.Clamp(Math.Round(cap), 10, 240);
                Apply("/video/display", "MaximumFPS_OnOff", "true", "MaximumFPS_OnOff", Categories.FpsCap);
                Apply("/video/display", "MaximumFPS_Value", fps.ToString(CultureInfo.InvariantCulture), "MaximumFPS_Value", Categories.FpsCap);
            }
        }

        // ray tracing: only ever switched off, and only at the two lowest steps
        if (req.Step <= QualityStep.Medium)
        {
            Apply("/graphics/raytracing", "RayTracing", "false", "RayTracing", Categories.Quality);
            Apply("/graphics/raytracing", "RayTracedPathTracing", "false", "RayTracedPathTracing", Categories.Quality);
        }

        plan.NotControlled.Add("Resolution, window mode, VSync, upscaling (DLSS, FSR, XeSS) and frame generation: the game builds those lists for this PC, so they are left as they are. Ray tracing is only ever switched off at Low and Medium, never on.");
        plan.Warnings.Add("Cyberpunk 2077 rewrites UserSettings.json when it closes, so apply while the game is not running.");

        if (edit.Changes.Count > 0) { edit.Updated = file.ToBytes(); plan.Files.Add(edit); }
        else plan.Files.Add(edit);       // an examined file with no changes: lets the plan say "Already set"
    }
}
