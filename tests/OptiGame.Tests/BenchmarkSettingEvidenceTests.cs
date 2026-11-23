using System.Globalization;
using OptiGame.Core;
using OptiGame.Core.Bench;
using OptiGame.Core.Recommend;

namespace OptiGame.Tests;

[Collection("data dir")]
public sealed class BenchmarkSettingEvidenceTests
{
    const string Option = "red:graphics.advanced.VolumetricFogResolution";
    const string ContextJson = """
        {
          "pairId":"fixture-a", "gameBuild":"2.0", "scenario":"Built-in benchmark", "hardware":"Fixture laptop",
          "width":1920, "height":1080, "frameCap":"uncapped", "renderMode":"Raster, native, AC power",
          "settings":{"red:graphics.advanced.VolumetricFogResolution":"High","rayTracing":"Off"}
        }
        """;

    [Fact]
    public void Matching_capture_pair_adds_evidence_for_the_exact_advice_under_dutch_culture()
    {
        var beforeContext = BenchmarkCaptureContext.Parse(ContextJson);
        var afterContext = BenchmarkCaptureContext.Parse(ContextJson.Replace("High", "Medium", StringComparison.Ordinal));
        var comparison = Assert.IsType<BenchmarkSettingComparison>(BenchmarkSettingEvidence.Compare(
            Run("1", "before", beforeContext, 59.5), Run("2", "after", afterContext, 62.7)));
        var report = new SettingRecommendationReport(60, "fixture", (1920, 1080))
        {
            Settings = [new(Option, "Fog", "High", "Medium", "Guide")],
            Evidence = [new("Fixture guide", "Recorded menu settings.")],
        };

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            var annotated = BenchmarkSettingEvidence.Include(report, "Fixture laptop", [comparison]);
            Assert.Equal(2, annotated.Evidence.Count);
            Assert.Contains("59.5 → 62.7", annotated.Evidence[1].Detail);
            Assert.Contains("build 2.0", annotated.Evidence[1].Detail);
            Assert.Equal(report.Settings, annotated.Settings);
            Assert.Same(report, BenchmarkSettingEvidence.Include(report, "Other hardware", [comparison]));
            var otherResolution = report with { Resolution = (2560, 1440) };
            Assert.Same(otherResolution, BenchmarkSettingEvidence.Include(otherResolution, "Fixture laptop", [comparison]));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Legacy_captures_and_multiple_setting_or_context_changes_are_ineligible()
    {
        var high = BenchmarkCaptureContext.Parse(ContextJson);
        var medium = BenchmarkCaptureContext.Parse(ContextJson.Replace("High", "Medium", StringComparison.Ordinal));
        var before = Run("1", "before", high, 60);
        Assert.Null(BenchmarkSettingEvidence.Compare(before, Run("2", "after", null, 65)));
        Assert.Null(BenchmarkSettingEvidence.Compare(before, Run("2", "after", medium with { Width = 2560 }, 65)));
        Assert.Null(BenchmarkSettingEvidence.Compare(before, Run("2", "after", medium with { GameBuild = "2.1" }, 65)));
        Assert.Null(BenchmarkSettingEvidence.Compare(before, Run("2", "after", medium with
        {
            Settings = new Dictionary<string, string> { [Option] = "Medium", ["rayTracing"] = "On" },
        }, 65)));
        Assert.Throws<FormatException>(() => BenchmarkCaptureContext.Parse(ContextJson.Replace("uncapped", "unknown", StringComparison.Ordinal)));
    }

    [Fact]
    public void Import_persists_capture_context_without_replacing_legacy_preference_fields()
    {
        var root = Directory.CreateTempSubdirectory("optigame-bench-context-").FullName;
        var old = AppPaths.Override;
        try
        {
            AppPaths.Override = root;
            var csv = Path.Combine(root, "fixture.csv");
            File.WriteAllLines(csv, ["frametime_ms", .. Enumerable.Repeat("16.5", 120)]);
            var context = BenchmarkCaptureContext.Parse(ContextJson);
            var imported = BenchmarkStore.Import(csv, "fixture", "Fixture", "before", "Import PC", resolution: "kept", captureContext: context);
            var stored = Assert.IsType<BenchResult>(BenchmarkStore.Find(imported.Id));
            Assert.Equal("kept", stored.Resolution);
            Assert.Equal("Fixture laptop", stored.CaptureContext!.Hardware);
            Assert.Equal("High", stored.CaptureContext.Settings[Option]);
        }
        finally
        {
            AppPaths.Override = old;
            Directory.Delete(root, recursive: true);
        }
    }

    static BenchResult Run(string id, string label, BenchmarkCaptureContext? context, double fps) => new()
    {
        Id = id, GameId = "fixture", Label = label, CaptureContext = context,
        Metrics = new BenchMetrics { AverageFps = fps, OnePercentLowFps = fps - 5, DurationSeconds = 30 },
    };
}
