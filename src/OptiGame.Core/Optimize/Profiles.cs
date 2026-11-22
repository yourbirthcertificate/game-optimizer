using OptiGame.Core.Hardware;

namespace OptiGame.Core.Optimize;

/// <summary>Engine profiles: what each slider step means for each engine on a given PC.</summary>
public static class Profiles
{
    /// <summary>Unreal scalability groups in GameUserSettings.ini order, values 0 (low) to 3 (epic).</summary>
    public static readonly string[] UnrealGroups =
    { "ViewDistance", "AntiAliasing", "Shadow", "GlobalIllumination", "Reflection", "PostProcess", "Texture", "Effects", "Foliage", "Shading", "Landscape" };

    //                                         VD AA Sh GI Re PP Tx Ef Fo Sd La
    static readonly int[][] UnrealTable =
    {
        /* Low      */ new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        /* Medium   */ new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
        /* Balanced */ new[] { 2, 2, 1, 1, 1, 1, 2, 1, 1, 2, 2 },
        /* High     */ new[] { 3, 2, 2, 2, 2, 2, 3, 2, 2, 3, 3 },
        /* VeryHigh */ new[] { 3, 3, 3, 2, 3, 3, 3, 3, 3, 3, 3 },
        /* Ultra    */ new[] { 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3 },
    };

    /// <summary>Highest texture level that fits in this GPU's memory.</summary>
    public static int TextureCap(HardwareInfo hw) => hw.GpuVramGb switch
    {
        <= 0 => 2,      // unknown: stay safe
        <= 4.5 => 1,
        <= 6.5 => 2,
        _ => 3,
    };

    public static Dictionary<string, int> Unreal(QualityStep step, HardwareInfo hw)
    {
        var row = UnrealTable[(int)step];
        var d = new Dictionary<string, int>();
        for (int i = 0; i < UnrealGroups.Length; i++) d[UnrealGroups[i]] = row[i];
        d["Texture"] = Math.Min(d["Texture"], TextureCap(hw));
        return d;
    }

    /// <summary>Source 1 video.txt levels per step.</summary>
    public static (int cpu, int gpu, int gpuMem) Source(QualityStep step, HardwareInfo hw)
    {
        int s = (int)step;
        int cpu = new[] { 0, 1, 1, 2, 2, 2 }[s];
        int gpu = new[] { 0, 1, 2, 2, 3, 3 }[s];
        int mem = Math.Min(new[] { 0, 1, 2, 2, 2, 2 }[s], hw.GpuVramGb is > 0 and <= 2 ? 1 : 2);
        return (cpu, gpu, mem);
    }

    public static readonly string[] KnownVariables =
    {
        "step", "fpsCap", "refreshHz", "activeRefreshHz", "nativeWidth", "nativeHeight", "nativeResolution",
        "resolution", "width", "height", "vsync", "displayMode", "ueWindowMode", "textureCap",
        "ue.ViewDistance", "ue.AntiAliasing", "ue.Shadow", "ue.GlobalIllumination", "ue.Reflection", "ue.PostProcess",
        "ue.Texture", "ue.Effects", "ue.Foliage", "ue.Shading", "ue.Landscape",
    };

    /// <summary>Variables at a step with the default (Balanced) preferences.</summary>
    public static Dictionary<string, object> Variables(QualityStep step, HardwareInfo hw) =>
        Variables(OptimizeRequest.Default(hw, step), hw, null);

    /// <summary>
    /// Variables rule files can refer to as "$name". Display and frame-rate variables follow the user's
    /// preferences: when a preference is "keep", the variable is a Keep marker and the key is left alone.
    /// $nativeResolution/$nativeWidth/$nativeHeight are kept as aliases of the chosen resolution.
    /// </summary>
    public static Dictionary<string, object> Variables(OptimizeRequest req, HardwareInfo hw, GameRule? rule)
    {
        object fps = req.FpsMode switch
        {
            FpsMode.Keep => new Keep("frame-rate cap set to keep"),
            FpsMode.Uncapped when rule?.FpsUncappedValue is { } u => u.TryGetInt32(out var ui) ? ui : u.GetDouble(),
            FpsMode.Uncapped => new Keep("this rule does not say how the game writes 'no cap', so the cap is left alone"),
            _ => req.FpsCap is { } c && c == Math.Floor(c) ? (int)c : (object)req.FpsCap!.Value,
        };
        object res = req.Resolution is var (w, h) ? new[] { w, h } : new Keep("resolution set to keep");
        object resW = req.Resolution is var (w2, _) ? w2 : new Keep("resolution set to keep");
        object resH = req.Resolution is var (_, h2) ? h2 : new Keep("resolution set to keep");
        object vsync = req.VSync switch { VSyncChoice.On => true, VSyncChoice.Off => false, _ => new Keep("VSync set to keep") };
        object mode = req.DisplayMode switch
        {
            DisplayModeChoice.Keep => new Keep("display mode set to keep"),
            DisplayModeChoice.Borderless => "borderless",
            DisplayModeChoice.Windowed => "windowed",
            _ => "fullscreen",
        };
        object ueMode = req.DisplayMode switch
        {
            DisplayModeChoice.Keep => new Keep("display mode set to keep"),
            DisplayModeChoice.Borderless => "EWindowMode::WindowedFullscreen",
            DisplayModeChoice.Windowed => "EWindowMode::Windowed",
            _ => "EWindowMode::Fullscreen",
        };
        var v = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["step"] = (int)req.Step,
            ["fpsCap"] = fps,
            ["refreshHz"] = req.Display.CurrentHz,
            ["activeRefreshHz"] = req.Display.CurrentHz,
            ["nativeWidth"] = resW, ["width"] = resW,
            ["nativeHeight"] = resH, ["height"] = resH,
            ["nativeResolution"] = res, ["resolution"] = res,
            ["vsync"] = vsync,
            ["displayMode"] = mode,
            ["ueWindowMode"] = ueMode,
            ["textureCap"] = TextureCap(hw),
        };
        foreach (var (k, val) in Unreal(req.Step, hw)) v["ue." + k] = val;
        return v;
    }
}
