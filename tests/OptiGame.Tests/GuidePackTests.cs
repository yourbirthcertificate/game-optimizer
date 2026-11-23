using System.Globalization;
using OptiGame.Core.Optimize;
using OptiGame.Core.Recommend;

namespace OptiGame.Tests;

public sealed class GuidePackTests
{
    const string Sample = """
        {
          "schemaVersion": 1,
          "sources": [{
            "id": "example", "name": "Example", "url": "https://www.pcgamer.com/example",
            "retrieved": "2026-11-20", "verified": "2026-11-23", "appliesTo": "Fixture build",
            "summary": "Observed a setting.", "confidence": "high"
          }],
          "advice": [{ "game": "fixture", "option": "Quality", "source": "example", "value": "High" }]
        }
        """;

    [Fact]
    public void Embedded_pack_links_both_games_to_reviewed_values()
    {
        var pack = Assert.IsType<GuidePack>(GuidePack.ReadBuiltIn(out var problems));
        Assert.Empty(problems);
        Assert.Equal("High", pack.FindReviewedValue("cyberpunk-2077", "VolumetricFogResolution", "pcgamer"));
        Assert.Equal("Medium", pack.FindReviewedValue("cyberpunk-2077", "ScreenSpaceReflectionsQuality", "toms"));
        Assert.Equal("Off", pack.FindReviewedValue("elden-ring", "DepthOfField", "digital-trends"));
        Assert.Equal("DISABLE", pack.FindReviewedValue("elden-ring", "MotionBlur", "elden-xml"));
        Assert.Null(pack.FindReviewedValue("elden-ring", "ShadowQuality", "digital-trends"));
    }

    [Theory]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 2", "schemaVersion")]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1", "schemaVersion")]
    [InlineData("https://www.pcgamer.com/example", "https://www.pcgamer.com.evil.invalid/example", "url")]
    [InlineData("https://www.pcgamer.com/example", "http://www.pcgamer.com/example", "url")]
    [InlineData("https://www.pcgamer.com/example", "https://fake@www.pcgamer.com/example", "url")]
    [InlineData("2026-11-23", "2026-11-19", "verified")]
    [InlineData("2026-11-23", "2026-02-30", "verified")]
    [InlineData("\"Fixture build\"", "\"\"", "sources")]
    [InlineData("\"source\": \"example\"", "\"source\": \"missing\"", "source")]
    [InlineData("\"value\": \"High\"", "\"value\": \"High\", \"direction\": \"lower\"", "advice")]
    [InlineData("\"value\": \"High\"", "\"confidence\": \"high\"", "advice")]
    [InlineData("\"High\" }", "\"High\" }, { \"game\": \"fixture\", \"option\": \"Quality\", \"source\": \"example\", \"value\": \"Low\" }", "advice")]
    public void Invalid_packs_report_the_problem(string oldValue, string replacement, string location)
    {
        var json = Sample.Replace(oldValue, replacement, StringComparison.Ordinal);
        Assert.Null(GuidePack.TryParse(json, out var problems));
        Assert.Contains(problems, problem => problem.Severity == RuleProblemSeverity.Error &&
            problem.Where.Contains(location, StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_keeps_conflicting_build_observations_and_can_filter_by_exact_scope()
    {
        var pack = Assert.IsType<GuidePack>(GuidePack.ReadBuiltIn(out _));
        var reflections = Assert.Single(pack.ForGame("Cyberpunk-2077", "ScreenSpaceReflectionsQuality"));
        Assert.True(reflections.HasDisagreement);
        Assert.Contains(reflections.Observations, item => item.Advice.Value == "High" && item.Source.Name == "PC Gamer");
        var launch = Assert.Single(reflections.Observations, item => item.Advice.Value == "Medium");
        var filtered = Assert.Single(pack.ForGame("cyberpunk-2077", reflections.Option, launch.Source.AppliesTo));
        Assert.False(filtered.HasDisagreement);
        Assert.Equal(launch, Assert.Single(filtered.Observations));

        var helldivers = Assert.Single(pack.ForGame("helldivers-2", "menu.texture-quality"));
        var observation = Assert.Single(helldivers.Observations);
        Assert.Equal("High", observation.Advice.Value);
        Assert.Contains("2024", observation.Source.AppliesTo);
    }

    [Fact]
    public void Unreviewed_advice_is_valid_but_excluded_from_reviewed_lookups()
    {
        var json = Sample.Replace("\"2026-11-23\"", "null", StringComparison.Ordinal)
            .Replace("\"value\": \"High\"", "\"direction\": \"lower\"", StringComparison.Ordinal);
        var pack = Assert.IsType<GuidePack>(GuidePack.TryParse(json, out var problems));
        Assert.Equal("lower", Assert.Single(pack.Advice).Direction);
        Assert.Equal(RuleProblemSeverity.Warning, Assert.Single(problems).Severity);
        Assert.Null(pack.FindReviewedSource("example"));
    }

    [Fact]
    public void Evidence_dates_are_invariant_under_dutch_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            var pack = Assert.IsType<GuidePack>(GuidePack.TryParse(Sample, out _));
            var evidence = Assert.Single(pack.Sources).ToEvidence();
            Assert.Contains("Fixture build", evidence.Detail);
            Assert.Contains("2026-11-23", evidence.Detail);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
