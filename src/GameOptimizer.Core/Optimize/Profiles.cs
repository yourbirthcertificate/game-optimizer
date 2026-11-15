using GameOptimizer.Core.Hardware;

namespace GameOptimizer.Core.Optimize;

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

    /// <summary>Variables rule files can refer to as "$name".</summary>
    public static Dictionary<string, object> Variables(QualityStep step, HardwareInfo hw)
    {
        var v = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["step"] = (int)step,
            ["fpsCap"] = hw.FpsCap,
            ["refreshHz"] = hw.MaxRefreshHz,
            ["nativeWidth"] = hw.NativeWidth,
            ["nativeHeight"] = hw.NativeHeight,
            ["nativeResolution"] = new[] { hw.NativeWidth, hw.NativeHeight },
            ["vsync"] = false,
            ["textureCap"] = TextureCap(hw),
        };
        foreach (var (k, val) in Unreal(step, hw)) v["ue." + k] = val;
        return v;
    }
}
