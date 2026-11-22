using System.Text;
using System.Text.Json;
using OptiGame.Core.Formats;
using OptiGame.Core.Hardware;
using OptiGame.Core.Optimize;

namespace OptiGame.Tests;

public class IniTests
{
    const string Sample = "[/Script/Engine.GameUserSettings]\r\nbUseVSync=True\r\nFrameRateLimit=60.000000\r\n+Paths=a\r\n+Paths=b\r\n\r\n[ScalabilityGroups]\r\nsg.ShadowQuality=3\r\n";

    [Fact]
    public void Unchanged_file_is_byte_identical_including_bom_and_crlf()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Sample)).ToArray();
        Assert.Equal(bytes, IniDocument.Parse(bytes).ToBytes());
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(Sample)).ToArray();
        Assert.Equal(utf16, IniDocument.Parse(utf16).ToBytes());
    }

    [Fact]
    public void Set_changes_only_that_line()
    {
        var ini = IniDocument.Parse(Encoding.UTF8.GetBytes(Sample));
        Assert.True(ini.Set("/Script/Engine.GameUserSettings", "bUseVSync", "False", false, out _));
        Assert.Equal(Sample.Replace("bUseVSync=True", "bUseVSync=False"), Encoding.UTF8.GetString(ini.ToBytes()));
    }

    [Fact]
    public void Duplicate_keys_are_refused()
    {
        var ini = IniDocument.Parse(Encoding.UTF8.GetBytes(Sample));
        Assert.False(ini.Set("/Script/Engine.GameUserSettings", "+Paths", "c", false, out var err));
        Assert.Contains("2 times", err);
    }

    [Fact]
    public void Missing_key_is_added_to_its_section_or_refused()
    {
        var ini = IniDocument.Parse(Encoding.UTF8.GetBytes(Sample));
        Assert.False(ini.Set("ScalabilityGroups", "sg.FoliageQuality", "2", addIfMissing: false, out _));
        Assert.True(ini.Set("ScalabilityGroups", "sg.FoliageQuality", "2", addIfMissing: true, out _));
        Assert.Equal("2", ini.Get("ScalabilityGroups", "sg.FoliageQuality"));
        Assert.True(ini.Set("NewSection", "Key", "1", addIfMissing: true, out _));
        Assert.Equal("1", ini.Get("NewSection", "Key"));
    }
}

public class KeyValuesTests
{
    const string Video = "\"VideoConfig\"\n{\n\t\"setting.defaultres\"\t\t\"1920\"\n\t\"setting.mat_vsync\"\t\t\"1\"\n}\n";

    [Fact]
    public void Parses_nested_blocks()
    {
        var kv = KvNode.Parse(Video);
        Assert.Equal("1920", kv["VideoConfig"]!.Get("setting.defaultres"));
    }

    [Fact]
    public void Sets_existing_keys_and_keeps_layout()
    {
        var text = Video;
        Assert.True(KvText.TrySet(ref text, "setting.mat_vsync", "0", out var old));
        Assert.Equal("1", old);
        Assert.Equal(Video.Replace("\"1\"", "\"0\""), text);
        Assert.False(KvText.TrySet(ref text, "setting.missing", "0", out _));
    }

    [Fact]
    public void Rejects_unbalanced_braces()
    {
        Assert.Throws<FormatException>(() => KvNode.Parse("\"a\" { \"b\" \"c\""));
    }
}

public class StepAndRuleTests
{
    [Fact]
    public void Unity_pref_names_use_unity_hash() =>
        Assert.Equal("UnityGraphicsQuality_h1669003810", UnityQuality.PrefName("UnityGraphicsQuality"));

    [Theory]
    [InlineData(QualityStep.Low, 6, 0)]
    [InlineData(QualityStep.High, 6, 3)]
    [InlineData(QualityStep.Ultra, 6, 5)]
    [InlineData(QualityStep.Ultra, 3, 2)]
    [InlineData(QualityStep.Balanced, 4, 1)]
    public void Steps_spread_over_level_counts(QualityStep s, int levels, int expected) =>
        Assert.Equal(expected, s.MapToLevels(levels));

    [Fact]
    public void Rule_values_resolve_per_step_and_from_variables()
    {
        var hw = new HardwareInfo { NativeWidth = 2560, NativeHeight = 1440, ActiveRefreshHz = 165, GpuVramGb = 8 };
        var vars = Profiles.Variables(QualityStep.High, hw);
        using var doc = JsonDocument.Parse("""{"a":[0,1,2,3,4,5],"b":"$fpsCap","c":"$nativeResolution","d":"$ue.Texture","e":"$nope"}""");
        var r = doc.RootElement;
        Assert.Equal(3, RuleSet.Resolve(r.GetProperty("a"), 3, vars, out _));
        Assert.Equal(162, RuleSet.Resolve(r.GetProperty("b"), 3, vars, out _));
        Assert.Equal(new[] { 2560, 1440 }, RuleSet.Resolve(r.GetProperty("c"), 3, vars, out _));
        Assert.Equal(3, RuleSet.Resolve(r.GetProperty("d"), 3, vars, out _));
        Assert.Null(RuleSet.Resolve(r.GetProperty("e"), 3, vars, out var err));
        Assert.Contains("unknown variable", err);
    }

    [Fact]
    public void Texture_level_is_capped_by_vram()
    {
        var small = new HardwareInfo { GpuVramGb = 4 };
        Assert.Equal(1, Profiles.Unreal(QualityStep.Ultra, small)["Texture"]);
    }

    [Fact]
    public void Built_in_rules_all_load()
    {
        var errors = new List<string>();
        var rules = RuleSet.Load(errors);
        Assert.Empty(errors);
        Assert.Contains(rules, r => r.Id == "voidbreaker");
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3080 Laptop GPU", 8.0, 2560, 1440, 16, QualityStep.High)]
    [InlineData("NVIDIA GeForce RTX 4090", 24.0, 2560, 1440, 32, QualityStep.Ultra)]
    [InlineData("NVIDIA GeForce RTX 3050 Laptop GPU", 4.0, 1920, 1080, 12, QualityStep.Medium)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", 0.0, 1920, 1080, 8, QualityStep.Low)]
    public void Recommendation_follows_gpu_screen_and_memory(string gpu, double vram, int w, int h, int threads, QualityStep expected)
    {
        var hw = new HardwareInfo { Gpu = gpu, GpuVramGb = vram, NativeWidth = w, NativeHeight = h, CpuThreads = threads, RamGb = 16 };
        HardwareDetector.Classify(hw);
        Assert.Equal(expected, hw.Recommended);
    }
}
