using System.Diagnostics;
using System.Globalization;
using System.Text;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Library.ConfigDiscovery;
using OptiGame.Core.Optimize;
using OptiGame.Core.Recommend;
using OptiGame.Core.Safety;

namespace OptiGame.Tests;

[Collection("data dir")]
public sealed class EldenRingTests : IDisposable
{
    const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <config data-flavor='fixture'>
          <!-- Untouched settings: café -->
          <ScreenMode>FULLSCREEN</ScreenMode>
          <Resolution-FullScreenWidth> 1920 </Resolution-FullScreenWidth>
          <Resolution-FullScreenHeight>1080</Resolution-FullScreenHeight>
          <Resolution-BorderlessScreenWidth>1366</Resolution-BorderlessScreenWidth>
          <Resolution-BorderlessScreenHeight>768</Resolution-BorderlessScreenHeight>
          <TextureQuality>HIGH</TextureQuality>
        </config>
        """;

    readonly string _root = Path.Combine(Path.GetTempPath(), "optigame-elden-" + Guid.NewGuid().ToString("N"));
    readonly string? _oldData = Environment.GetEnvironmentVariable("GAMEOPT_DATA_DIR");
    readonly string _oldRoaming = Planner.RoamingAppData;
    readonly string _path;
    readonly GameInfo _game;

    public EldenRingTests()
    {
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", Path.Combine(_root, "data"));
        Planner.RoamingAppData = Path.Combine(_root, "roaming");
        _path = Path.Combine(Planner.RoamingAppData, "EldenRing", "GraphicsConfig.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _game = new GameInfo
        {
            Id = "steam:1245620", SteamAppId = "1245620", Name = "Elden Ring", Store = "Steam",
            InstallDir = Directory.CreateDirectory(Path.Combine(_root, "install")).FullName,
        };
    }

    GamePlan Preview() => Planner.Plan(_game, new HardwareInfo { NativeWidth = 2560, NativeHeight = 1440 },
        QualityStep.High, []);

    [Fact]
    public void Inspection_exposes_loaded_quality_evidence_advice_and_a_confirmed_candidate()
    {
        var input = Xml.Replace("</config>", """
            <RaytracingQuality>DISABLE</RaytracingQuality>
            <DepthOfField>MAX</DepthOfField>
            <MotionBlur>HIGH</MotionBlur>
            </config>
            """);
        File.WriteAllText(_path, input);
        var hardware = new HardwareInfo
        {
            Gpu = "Radeon RX 580",
            Displays = [new DisplayInfo { DeviceName = "fixture", CurrentWidth = 1920, CurrentHeight = 1080, CurrentHz = 60 }],
        };
        var recommendation = Recommender.Recommend(hardware, new("Fixture publisher", "Radeon RX 580", null, null), (1920, 1080));
        var session = new OptimizerSession { Hw = hardware, Prefs = new(), Scan = new() };
        session.Recs[_game.Id] = recommendation;
        var roots = new ConfigRoots("", "", Planner.RoamingAppData, "", "", "", "", Path.Combine(_root, "data"));
        var finder = new ConfigFinder(roots, _ => throw new InvalidOperationException("Unexpected registry read."));

        var ordinary = session.PlanFor(_game, forcedStep: QualityStep.Low);
        Assert.Equal(QualityStep.Low, ordinary.Step);
        Assert.Same(recommendation, ordinary.Recommendation);
        Assert.Null(ordinary.SettingsDiscovery);
        Assert.Empty(ordinary.SettingsCandidates);

        var inspected = session.InspectPlan(_game, finder, recommendationTargetFps: 59.94);
        Assert.Same(recommendation, inspected.Recommendation);
        Assert.Equal(new RecommendationEvidence("Fixture publisher", recommendation.Reason), inspected.Evidence[0]);
        var advice = Assert.IsType<SettingRecommendationReport>(inspected.SettingAdvice);
        Assert.Equal(59.94, advice.TargetFps);
        Assert.Equal(advice.Evidence, inspected.Evidence.Skip(1));
        Assert.Contains(inspected.Evidence, item => item.Source == "Digital Trends" && item.Url is not null);
        var candidate = Assert.Single(inspected.SettingsCandidates);
        Assert.Equal(_path, candidate.Path);
        Assert.Equal(ConfigConfidence.Confirmed, candidate.Confidence);
        Assert.Equal(input, File.ReadAllText(_path));
    }

    [Fact]
    public void Advisory_options_follow_the_target_and_leave_the_xml_untouched()
    {
        var input = Xml.Replace("</config>", """
            <RaytracingQuality>DISABLE</RaytracingQuality>
            <DepthOfField>MAX</DepthOfField>
            <MotionBlur>HIGH</MotionBlur>
            </config>
            """);
        File.WriteAllText(_path, input);
        var hardware = new HardwareInfo { Gpu = "Radeon RX 580", GpuVramGb = 8, RamGb = 16 };
        GamePlan Plan(double fps, params string[] preserve) => PlanWithPriority(fps, RecommendationPriority.Balanced, preserve);
        GamePlan PlanWithPriority(double fps, RecommendationPriority priority, params string[] preserve) =>
            Planner.Plan(_game, hardware, new OptimizeRequest
        {
            Display = new DisplayInfo { DeviceName = "fixture", CurrentHz = 60 },
            RecommendationTargetFps = fps,
            RecommendationPriority = priority,
            Preserve = [.. preserve],
        }, []);

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            var plan = Plan(59.94);
            var advice = Assert.IsType<OptiGame.Core.Recommend.SettingRecommendationReport>(plan.SettingAdvice);
            Assert.Equal((1920, 1080), advice.Resolution);
            Assert.Equal(2, advice.Settings.Count);
            Assert.All(advice.Settings, option => Assert.Equal("Off", option.SuggestedValue));
            Assert.Contains(advice.Evidence, item => item.Source == "Elden Ring player XML" && item.Url is not null);
            Assert.Contains("59.94 FPS", OptiGame.Cli.Cli.SettingAdviceLines(advice).First());
            Assert.Empty(plan.Files);
            Assert.Equal(input, File.ReadAllText(_path));

            Assert.Contains("fits", Plan(20).SettingAdvice!.FallbackReason);
            var speed = PlanWithPriority(20, RecommendationPriority.Speed).SettingAdvice!;
            Assert.Equal(2, speed.Settings.Count);
            Assert.All(speed.Settings, option => Assert.Contains("Speed priority", option.Reason));
            Assert.Contains("Look priority", PlanWithPriority(59.94, RecommendationPriority.Look).SettingAdvice!.FallbackReason);
            Assert.Contains("noise or power data", PlanWithPriority(59.94, RecommendationPriority.Quiet).SettingAdvice!.FallbackReason);
            Assert.Contains("60 FPS cap", Plan(90).SettingAdvice!.FallbackReason);
            Assert.Empty(Plan(59.94, Categories.Quality).SettingAdvice!.Settings);

            File.WriteAllText(_path, input.Replace("<MotionBlur>HIGH</MotionBlur>",
                "<MotionBlur>HIGH</MotionBlur><MotionBlur>HIGH</MotionBlur>"));
            var duplicate = Plan(59.94).SettingAdvice!;
            Assert.Equal("Depth of field", Assert.Single(duplicate.Settings).Name);
            Assert.Contains(duplicate.Skipped, line => line.StartsWith("Motion blur", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preview_apply_and_undo_preserve_fixture_bytes_under_dutch_culture(bool utf16)
    {
        var encoding = utf16 ? (Encoding)new UnicodeEncoding(false, true, true) : new UTF8Encoding(true, true);
        var original = WithEncoding(Xml, encoding);
        var expected = WithEncoding(Xml.Replace("> 1920 <", "> 2560 <").Replace(">1080<", ">1440<"), encoding);
        File.WriteAllBytes(_path, original);

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            Assert.Equal("5,0", 5.0.ToString("0.0", CultureInfo.CurrentCulture));
            var plan = Preview();
            Assert.Equal(PlanStatus.Ready, plan.Status);
            var edit = Assert.Single(plan.Files);
            Assert.Equal("2560x1440", Assert.Single(edit.Changes).NewRaw);
            Assert.Equal(original, File.ReadAllBytes(_path));
            Assert.Equal(expected, edit.Updated);

            var applied = Applier.Apply([plan], "fixture");
            Assert.Single(applied.Applied);
            Assert.Equal(expected, File.ReadAllBytes(_path));
            var undo = Applier.PlanRestore(Applier.Runs().Single(r => r.Id == applied.RunId));
            Assert.Single(Applier.Restore(undo, RestoreMode.UndoChanges).Applied);
            Assert.Equal(original, File.ReadAllBytes(_path));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Key_level_undo_keeps_unrelated_later_xml_edits()
    {
        File.WriteAllText(_path, Xml);
        var applied = Applier.Apply([Preview()], "fixture");
        var edited = File.ReadAllText(_path).Replace(">HIGH<", ">LOW<");
        File.WriteAllText(_path, edited);
        var restore = Applier.PlanRestore(Applier.Runs().Single(r => r.Id == applied.RunId));
        Assert.True(Assert.Single(restore.Items).ChangedSinceOptimize);
        Assert.Single(Applier.Restore(restore, RestoreMode.UndoChanges).Applied);
        Assert.Equal(Encoding.UTF8.GetBytes(Xml.Replace(">HIGH<", ">LOW<")), File.ReadAllBytes(_path));
    }

    [Fact]
    public void Borderless_undo_treats_a_later_dimension_edit_as_a_conflict_for_the_pair()
    {
        var input = Xml.Replace(">FULLSCREEN<", ">BORDERLESS<");
        File.WriteAllText(_path, input);
        var result = Applier.Apply([Preview()], "fixture");
        Assert.Single(result.Applied);
        var expected = input.Replace(">1366<", ">2560<").Replace(">768<", ">1440<");
        Assert.Equal(expected, File.ReadAllText(_path));

        var edited = expected.Replace(">1440<", ">1200<");
        File.WriteAllText(_path, edited);
        var restore = Applier.PlanRestore(Applier.Runs().Single(run => run.Id == result.RunId));
        Assert.Equal(KeyState.Conflict, Assert.Single(Assert.Single(restore.Items).Keys).State);
        Assert.Empty(Applier.Restore(restore, RestoreMode.UndoChanges).Applied);
        Assert.Equal(edited, File.ReadAllText(_path));
    }

    [Fact]
    public void Apply_refuses_a_stale_preview()
    {
        File.WriteAllText(_path, Xml);
        var plan = Preview();
        var updated = Xml.Replace(">HIGH<", ">LOW<");
        File.WriteAllText(_path, updated);
        var result = Applier.Apply([plan], "fixture");
        Assert.Contains("changed since the preview", Assert.Single(result.Refused));
        Assert.Equal(updated, File.ReadAllText(_path));
    }

    [Fact]
    public void Apply_refuses_a_running_dummy_in_the_install_folder()
    {
        File.WriteAllText(_path, Xml);
        var plan = Preview();
        var exe = Path.Combine(_game.InstallDir, "elden-fixture.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        using var dummy = Process.Start(new ProcessStartInfo(exe, "-t 127.0.0.1")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
        })!;
        try
        {
            var result = Applier.Apply([plan], "fixture");
            Assert.Equal(PlanStatus.Running, plan.Status);
            Assert.Single(result.Refused);
            Assert.Equal(Xml, File.ReadAllText(_path));
        }
        finally
        {
            if (!dummy.HasExited) dummy.Kill();
            dummy.WaitForExit(2000);
        }
    }

    [Theory]
    [InlineData("<!DOCTYPE config [<!ENTITY x '100'>]><config>&x;</config>")]
    [InlineData("<config><ScreenMode>FULLSCREEN</ScreenMode><Resolution-FullScreenWidth>1920</Resolution-FullScreenWidth><Resolution-FullScreenWidth>1280</Resolution-FullScreenWidth><Resolution-FullScreenHeight>1080</Resolution-FullScreenHeight></config>")]
    [InlineData("<config><ScreenMode>WINDOW</ScreenMode></config>")]
    public void Unsafe_or_unrecognized_xml_is_skipped(string input)
    {
        File.WriteAllText(_path, input);
        var plan = Preview();
        Assert.Equal(PlanStatus.Skipped, plan.Status);
        Assert.NotEmpty(plan.Skipped);
        Assert.Empty(plan.Files);
        Assert.Equal(input, File.ReadAllText(_path));
    }

    [Fact]
    public void Reviewed_location_is_confirmed_by_the_same_parser()
    {
        File.WriteAllText(_path, Xml);
        var roots = new ConfigRoots("", "", Planner.RoamingAppData, "", "", "", "", Path.Combine(_root, "data"));
        var result = new ConfigFinder(roots, _ => throw new InvalidOperationException("Unexpected registry read.")).Find(_game);
        Assert.Equal(_path, Assert.Single(result.Candidates, c => c.Confidence == ConfigConfidence.Confirmed).Path);
    }

    static byte[] WithEncoding(string xml, Encoding encoding)
    {
        var text = xml.Replace("utf-8", encoding.WebName).Replace("\r\n", "\n").Replace("\n", "\r\n");
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
    }

    public void Dispose()
    {
        Planner.RoamingAppData = _oldRoaming;
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", _oldData);
        Directory.Delete(_root, recursive: true);
    }
}
