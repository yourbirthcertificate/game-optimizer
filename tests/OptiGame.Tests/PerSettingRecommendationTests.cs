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
    static OptimizeRequest Request(double? fps, bool keep = false, RecommendationPriority priority = RecommendationPriority.Balanced) => new()
    {
        Display = new DisplayInfo { DeviceName = "fixture", CurrentHz = 59.94 },
        Resolution = (1920, 1080),
        RecommendationTargetFps = fps,
        RecommendationPriority = priority,
        Preserve = keep ? [Categories.Quality] : [],
    };

    [Fact]
    public void Increasing_the_fps_target_recommends_lower_verified_options_without_editing_the_input()
    {
        var original = Fixture();
        var document = RedSettingsFile.Parse(original);
        var standard = CyberpunkSettingRecommender.Recommend(Hardware(), Request(60), document);
        var faster = CyberpunkSettingRecommender.Recommend(Hardware(), Request(90), document);
        Assert.Equal(15, standard.Settings.Count);
        Assert.Equal("8", Assert.Single(standard.Settings, setting => setting.Name == "Anisotropy").SuggestedValue);
        Assert.Equal("High", Assert.Single(standard.Settings, setting => setting.Name == "Screen space reflections").SuggestedValue);
        Assert.Equal("Medium", Assert.Single(standard.Settings, setting => setting.Name == "Cascaded shadows range").SuggestedValue);
        Assert.Equal("High", Assert.Single(standard.Settings, setting => setting.Name == "Ambient occlusion").SuggestedValue);

        Assert.Equal(5, faster.Settings.Count);
        Assert.All(faster.Settings, setting => Assert.Equal("Medium", setting.SuggestedValue));
        Assert.Contains("after fog and clouds", Assert.Single(faster.Settings, setting => setting.Name == "Screen space reflections").Reason);
        Assert.Contains(faster.Evidence, item => item.Source == "Tom's Hardware" && item.Detail.Contains("2020", StringComparison.Ordinal));
        Assert.Contains(faster.Skipped, skip => skip.StartsWith("Ambient occlusion", StringComparison.Ordinal));
        Assert.Contains(standard.Skipped, skip => skip.StartsWith("Crowd density", StringComparison.Ordinal));
        Assert.Contains(faster.Evidence, item => item.Source == "CD PROJEKT RED" && item.Url is not null);
        Assert.Contains(faster.Evidence, item => item.Source == "PC Gamer" && item.Url is not null);
        Assert.Equal(original, document.ToBytes());
    }

    [Fact]
    public void Explicit_priorities_respect_the_available_evidence()
    {
        var document = RedSettingsFile.Parse(Fixture());
        var look = CyberpunkSettingRecommender.Recommend(Hardware(), Request(60, priority: RecommendationPriority.Look), document);
        var anisotropy = Assert.Single(look.Settings, option => option.Name == "Anisotropy");
        Assert.Equal("16", anisotropy.CurrentValue);
        Assert.Equal(anisotropy.CurrentValue, anisotropy.SuggestedValue);
        var highTarget = CyberpunkSettingRecommender.Recommend(Hardware(), Request(90, priority: RecommendationPriority.Look), document);
        Assert.Empty(highTarget.Settings);
        Assert.Contains("Look priority", highTarget.FallbackReason);

        var speed = CyberpunkSettingRecommender.Recommend(Hardware(), Request(60, priority: RecommendationPriority.Speed), document);
        Assert.Equal(5, speed.Settings.Count);
        Assert.All(speed.Settings, option => Assert.Equal("Medium", option.SuggestedValue));
        Assert.Contains(OptiGame.Cli.Cli.SettingAdviceLines(speed), line => line == "Advisory priority: speed.");

        var quiet = CyberpunkSettingRecommender.Recommend(Hardware(), Request(60, priority: RecommendationPriority.Quiet), document);
        Assert.Empty(quiet.Settings);
        Assert.Contains("noise or power data", quiet.FallbackReason);
        Assert.Equal(RecommendationPriority.Look, OptiGame.Cli.Cli.ReadRecommendationPriority(["--priority", "LOOK"]));
        Assert.Throws<ArgumentException>(() => OptiGame.Cli.Cli.ReadRecommendationPriority(["--priority", "1"]));
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
        Assert.Contains(kept.Skipped, skip => skip.StartsWith("Screen space reflections", StringComparison.Ordinal));
        Assert.NotNull(kept.FallbackReason);
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
