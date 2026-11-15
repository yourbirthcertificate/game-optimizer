using GameOptimizer.Core.Hardware;
using GameOptimizer.Core.Optimize;
using GameOptimizer.Core.Recommend;

namespace GameOptimizer.Tests;

public class GpuMatcherTests
{
    [Fact]
    public void Table_is_embedded() => Assert.True(GpuMatcher.Count > 300);

    [Fact]
    public void Laptop_chips_get_the_laptop_factor()
    {
        var m = GpuMatcher.Identify("NVIDIA GeForce RTX 3060 Laptop GPU")!;
        Assert.Equal("GeForce RTX 3060 (laptop)", m.Name);
        Assert.Equal((int)Math.Round(16900 * 0.87), m.Score);
    }

    [Fact]
    public void Longest_name_wins()
    {
        Assert.Equal("GeForce RTX 3060 Ti", GpuMatcher.Identify("NVIDIA GeForce RTX 3060 Ti")!.Name);
    }

    [Fact]
    public void Alternatives_are_compared_with_the_same_vendor()
    {
        var cands = GpuMatcher.Match("NVIDIA GeForce GTX 970 / AMD Radeon R9 290");
        Assert.Equal(2, cands.Count);
        Assert.Equal("GeForce GTX 970", GpuMatcher.PickRequirement(cands, "nvidia")!.Name);
        Assert.StartsWith("Radeon", GpuMatcher.PickRequirement(cands, "amd")!.Name);
    }

    [Fact]
    public void Bare_numbers_after_a_vendor_word_match_but_resolutions_do_not()
    {
        Assert.Contains(GpuMatcher.Match("GeForce 1060 6GB"), c => c.Name == "GeForce GTX 1060");
        Assert.Empty(GpuMatcher.Match("NVIDIA card at 1920 x 1080"));
    }
}

public class RequirementParsingTests
{
    [Fact]
    public void Steam_block_graphics_line_is_found()
    {
        const string html = "<strong>Recommended:</strong><br><ul class=\"bb_ul\"><li><strong>OS:</strong> Windows 10<br></li>" +
                            "<li><strong>Graphics:</strong> NVIDIA GeForce RTX 2070 or AMD RX 5700<br></li></ul>";
        Assert.Equal("NVIDIA GeForce RTX 2070 or AMD RX 5700", RequirementSource.SteamGpu(html));
    }

    [Fact]
    public void Old_style_plain_lines_are_read()
    {
        Assert.Equal("GeForce 8800", RequirementSource.SteamGpu("OS: XP<br>Video Card: GeForce 8800<br>"));
    }

    [Fact]
    public void Pcgw_windows_block_gives_both_levels()
    {
        const string wiki = "{{System requirements\n|OSfamily = Windows\n|minGPU = [[GeForce GTX 660]]\n|minGPU2 = Radeon HD 7850\n|recGPU = GeForce GTX 970<ref>x</ref>\n}}";
        var (rec, min) = RequirementSource.PcgwGpu(wiki);
        Assert.Equal("GeForce GTX 970", rec);
        Assert.Equal("GeForce GTX 660 / Radeon HD 7850", min);
    }
}

public class RecommenderTests
{
    static HardwareInfo Laptop() => new()
    {
        Gpu = "NVIDIA GeForce RTX 3080 Laptop GPU", GpuVramGb = 8, NativeWidth = 2560, NativeHeight = 1440,
        CpuThreads = 20, RamGb = 16, Recommended = QualityStep.High,
    };

    [Theory]
    [InlineData("NVIDIA GeForce RTX 2070", QualityStep.High)]     // demanding: about 0.78x after 1440p
    [InlineData("NVIDIA GeForce GTX 780", QualityStep.Ultra)]     // older game: lots of headroom
    [InlineData("NVIDIA GeForce RTX 4080", QualityStep.Medium)]
    public void Steps_follow_headroom_over_the_recommended_card(string recGpu, QualityStep expected)
    {
        var r = Recommender.Recommend(Laptop(), new GameRequirements("Steam", recGpu, null, 2022));
        Assert.Equal(expected, r.Step);
        Assert.Contains("Steam recommends", r.Reason);
    }

    [Fact]
    public void Release_year_is_the_fallback()
    {
        var r = Recommender.Recommend(Laptop(), new GameRequirements("Steam", null, null, 2014));
        Assert.Equal(QualityStep.Ultra, r.Step);
        Assert.Equal(QualityStep.High, Recommender.Recommend(Laptop(), null).Step);
    }

    [Fact]
    public void Small_vram_caps_even_old_games()
    {
        var hw = Laptop();
        hw.GpuVramGb = 4;
        Assert.Equal(QualityStep.Balanced, Recommender.Recommend(hw, new GameRequirements("Steam", "GeForce 8800 GT", null, 2008)).Step);
    }

    [Theory]
    [InlineData(QualityStep.High, 0, QualityStep.High)]
    [InlineData(QualityStep.High, 2, QualityStep.Ultra)]
    [InlineData(QualityStep.High, 3, QualityStep.Ultra)]
    [InlineData(QualityStep.Medium, -3, QualityStep.Low)]
    public void Global_offset_shifts_and_clamps(QualityStep rec, int offset, QualityStep expected) =>
        Assert.Equal(expected, Prefs.Shift(rec, offset));

    [Fact]
    public void Hand_set_games_ignore_the_offset()
    {
        var p = new Prefs { GlobalOffset = -2 };
        p.PerGame["steam:1"] = QualityStep.Ultra;
        Assert.Equal(QualityStep.Ultra, p.StepFor("steam:1", QualityStep.Low));
        Assert.Equal(QualityStep.Balanced, p.StepFor("steam:2", QualityStep.VeryHigh));
    }
}
