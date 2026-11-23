using System.Globalization;
using OptiGame.Core.Formats;
using OptiGame.Core.Hardware;
using OptiGame.Core.Optimize;
using OptiGame.Core.Recommend;

namespace OptiGame.Tests;

public sealed class PerSettingRecommendationTests
{
    static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "cyberpunk-UserSettings.json"));
    static HardwareInfo Hardware() => new() { Gpu = "GeForce RTX 2060 Super", GpuVramGb = 8, RamGb = 16 };
    static OptimizeRequest Request(double? fps, bool keep = false) => new()
    {
        Display = new DisplayInfo { DeviceName = "fixture", CurrentHz = 59.94 },
        Resolution = (1920, 1080),
        RecommendationTargetFps = fps,
        Preserve = keep ? [Categories.Quality] : [],
    };

    [Fact]
    public void Increasing_the_fps_target_recommends_lower_verified_options_without_editing_the_input()
    {
        var original = Fixture();
        var document = RedSettingsFile.Parse(original);
        var standard = CyberpunkSettingRecommender.Recommend(Hardware(), Request(60), document);
        var faster = CyberpunkSettingRecommender.Recommend(Hardware(), Request(90), document);
        Assert.Equal(2, standard.Settings.Count);
        Assert.All(standard.Settings, setting => Assert.Equal("High", setting.SuggestedValue));
        Assert.Equal(2, faster.Settings.Count);
        Assert.All(faster.Settings, setting => Assert.Equal("Medium", setting.SuggestedValue));
        Assert.Contains(faster.Evidence, item => item.Source == "CD PROJEKT RED" && item.Url is not null);
        Assert.Contains(faster.Evidence, item => item.Source == "PC Gamer" && item.Url is not null);
        Assert.Equal(original, document.ToBytes());
    }

    [Fact]
    public void Reported_memory_rounds_to_the_nearest_whole_gigabyte()
    {
        var file = RedSettingsFile.Parse(Fixture());
        var hardware = Hardware();
        hardware.RamGb = 15.7;
        hardware.GpuVramGb = 7.8;
        var report = CyberpunkSettingRecommender.Recommend(hardware, Request(60), file);
        Assert.Null(report.FallbackReason);
        Assert.NotEmpty(report.Settings);

        hardware.RamGb = 15.4;
        var lowMemory = CyberpunkSettingRecommender.Recommend(hardware, Request(60), file);
        Assert.Empty(lowMemory.Settings);
        Assert.Contains("memory", lowMemory.FallbackReason);
    }

    [Fact]
    public void The_cli_labels_the_reason_that_per_setting_advice_is_unavailable()
    {
        var report = new SettingRecommendationReport(null, null, null) { FallbackReason = "The FPS target is unknown." };
        Assert.Contains("Per-setting advice unavailable: The FPS target is unknown.",
            OptiGame.Cli.Cli.SettingAdviceLines(report));
    }

    [Fact]
    public void Insufficient_memory_unknown_gpu_and_kept_settings_use_the_fallback()
    {
        var file = RedSettingsFile.Parse(Fixture());
        var hardware = Hardware();
        hardware.GpuVramGb = 4;
        var memory = CyberpunkSettingRecommender.Recommend(hardware, Request(60), file);
        Assert.Empty(memory.Settings);
        Assert.Contains("memory", memory.FallbackReason);

        hardware.GpuVramGb = 8;
        hardware.Gpu = "Unknown GPU";
        var gpu = CyberpunkSettingRecommender.Recommend(hardware, Request(60), file);
        Assert.Empty(gpu.Settings);
        Assert.Contains("specific score", gpu.FallbackReason);

        var kept = CyberpunkSettingRecommender.Recommend(Hardware(), Request(60, keep: true), file);
        Assert.Empty(kept.Settings);
        Assert.Equal(2, kept.Skipped.Count);
    }

    [Fact]
    public void The_cli_accepts_and_formats_fractional_targets_under_dutch_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            var target = OptiGame.Cli.Cli.ReadRecommendationTarget(["--target-fps", "59.94"]);
            Assert.Equal(59.94, target);
            Assert.Throws<ArgumentException>(() => OptiGame.Cli.Cli.ReadRecommendationTarget(["--target-fps", "59,94"]));
            var report = CyberpunkSettingRecommender.Recommend(Hardware(), Request(target), RedSettingsFile.Parse(Fixture()));
            var lines = OptiGame.Cli.Cli.SettingAdviceLines(report).ToArray();
            Assert.Contains("59.94 FPS", lines[0]);
            Assert.Contains("at 1920x1080", lines[0]);
            Assert.Contains(lines, line => line.StartsWith("Evidence (CD PROJEKT RED)", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
