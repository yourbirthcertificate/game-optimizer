using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OptiGame.Core.Bench;
using OptiGame.Core.Formats;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Optimize;
using OptiGame.Core.Recommend;

namespace OptiGame.Tests;

/// <summary>
/// This PC's regional format is nl-NL (decimal comma) while Windows runs in English. Every place that turns a number into
/// text that is written to a settings file, a rule, an export, a benchmark record or a request must give the same bytes
/// under nl-NL as under the invariant culture: 59.94 is never "59,94".
/// </summary>
[Collection("data dir")]
public sealed class CultureTests : IDisposable
{
    static readonly CultureInfo Dutch = new("nl-NL");
    static readonly Regex CommaDecimal = new(@"\d,\d", RegexOptions.Compiled);
    readonly string _root = Path.Combine(Path.GetTempPath(), "optigame-culture-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string? _oldData = Environment.GetEnvironmentVariable("GAMEOPT_DATA_DIR");
    readonly string _oldLocal = Planner.LocalAppData;

    public CultureTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", Path.Combine(_root, "data"));
        Planner.LocalAppData = Path.Combine(_root, "local");
    }

    public void Dispose()
    {
        Planner.LocalAppData = _oldLocal;
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", _oldData);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>Runs <paramref name="make"/> under nl-NL and under the invariant culture, and requires the same result.</summary>
    static T SameUnderDutch<T>(Func<T> make)
    {
        var (c, u) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var inv = make();
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = Dutch;
            Assert.Equal("5,0", 5.0.ToString("0.0", CultureInfo.CurrentCulture));                          // the test really runs under a comma culture
            var nl = make();
            Assert.Equal(inv, nl);
            return nl;
        }
        finally { CultureInfo.CurrentCulture = c; CultureInfo.CurrentUICulture = u; }
    }

    static OptimizeRequest Request(HardwareInfo hw, double? cap, FpsMode mode = FpsMode.Custom) => new()
    {
        Step = QualityStep.High, StepSource = "test", ProfileName = "Balanced", Display = hw.Primary, FpsMode = mode, FpsCap = cap,
        Resolution = (hw.NativeWidth, hw.NativeHeight), DisplayMode = DisplayModeChoice.Recommended, VSync = VSyncChoice.Off,
    };

    static HardwareInfo Hw() => new() { NativeWidth = 2560, NativeHeight = 1440, ActiveRefreshHz = 59.94, GpuVramGb = 7.5, RamGb = 15.7 };

    // ------------------------------------------------------------------ settings files

    GameInfo UnrealGame()
    {
        var dir = Path.Combine(_root, "Game");
        Directory.CreateDirectory(Path.Combine(dir, "Proj", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(dir, "Proj", "Content"));
        File.WriteAllText(Path.Combine(dir, "Proj", "Binaries", "Win64", "Proj-Win64-Shipping.exe"), "x");
        var cfg = Path.Combine(Planner.LocalAppData, "Proj", "Saved", "Config", "WindowsNoEditor");
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(cfg, "GameUserSettings.ini"),
            "[/Script/Engine.GameUserSettings]\nbUseVSync=True\nResolutionSizeX=1920\nResolutionSizeY=1080\nLastUserConfirmedResolutionSizeX=1920\nLastUserConfirmedResolutionSizeY=1080\n" +
            "FullscreenMode=1\nLastConfirmedFullscreenMode=1\nPreferredFullscreenMode=1\nFrameRateLimit=60.000000\n\n[ScalabilityGroups]\nsg.ResolutionQuality=100.000000\nsg.ShadowQuality=1\n");
        var g = new GameInfo { Id = "steam:1", Name = "Proj", InstallDir = dir, Store = "Steam" };
        EngineDetector.Detect(g);
        return g;
    }

    [Fact]
    public void Unreal_ini_gets_a_dotted_frame_cap_under_nl_NL()
    {
        var g = UnrealGame();
        var hw = Hw();
        var text = SameUnderDutch(() => Encoding.UTF8.GetString(Planner.Plan(g, hw, Request(hw, 59.94), new List<GameRule>()).Files[0].Updated));
        Assert.Contains("FrameRateLimit=59.940000", text);
        Assert.DoesNotMatch(CommaDecimal, text);
    }

    [Fact]
    public void Unreal_automatic_cap_from_a_fractional_refresh_rate_is_a_whole_dotted_number()
    {
        var g = UnrealGame();
        var hw = Hw();
        var text = SameUnderDutch(() => Encoding.UTF8.GetString(Planner.Plan(g, hw, OptimizeRequest.Default(hw, QualityStep.High), new List<GameRule>()).Files[0].Updated));
        Assert.Contains("FrameRateLimit=56.000000", text);          // 59.94 Hz minus 3, floored
        Assert.DoesNotMatch(CommaDecimal, text);
    }

    static GameRule RuleFor(string format, string path, string setBlock) => RuleSet.TryParse($$"""
        { "id": "t", "name": "T", "match": { "installDirs": ["game"] },
          "files": [ { "format": "{{format}}", "path": "{{path}}", "set": {{setBlock}} } ] }
        """, out var problems) ?? throw new Exception(string.Join("; ", problems));

    [Theory]
    [InlineData("ini", "{install}/s.ini", "{\"Video/MaxFps\": \"$fpsCap\", \"Video/Scale\": 59.94}", "[Video]\nMaxFps=60\nScale=1.5\n")]
    [InlineData("json", "{install}/s.json", "{\"video.maxFps\": \"$fpsCap\", \"video.scale\": 59.94}", "{\"video\":{\"maxFps\":60.0,\"scale\":1.5}}")]
    [InlineData("kv", "{install}/s.txt", "{\"maxFps\": \"$fpsCap\", \"scale\": 59.94}", "\"maxFps\"\t\t\"60\"\n\"scale\"\t\t\"1.5\"\n")]
    public void Rule_files_get_dotted_numbers_in_every_format(string format, string path, string set, string initial)
    {
        var dir = Path.Combine(_root, format, "game");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Path.GetFileName(path));
        File.WriteAllText(file, initial);
        var g = new GameInfo { Id = "x:" + format, Name = "T", InstallDir = dir, Store = "Test" };
        var hw = Hw();
        var rule = RuleFor(format, path, set);
        var text = SameUnderDutch(() => Encoding.UTF8.GetString(Planner.Plan(g, hw, Request(hw, 59.94), new[] { rule }).Files.Single().Updated));
        Assert.Contains("59.94", text);
        Assert.DoesNotMatch(CommaDecimal, text);
    }

    [Fact]
    public void Unity_registry_names_and_quality_labels_do_not_depend_on_the_culture()
    {
        Assert.Matches(@"^Screenmanager Resolution Width_h\d+$", SameUnderDutch(() => UnityQuality.PrefName("Screenmanager Resolution Width")));
        Assert.Equal(3, SameUnderDutch(() => QualitySteps.TryParse("3", out var s) ? (int)s : -1));
    }

    // ------------------------------------------------------------------ rules, exports, benchmarks, lookups

    [Fact]
    public void Exported_preferences_keep_dotted_numbers()
    {
        var p = new Prefs { GlobalOffset = -1 };
        p.Game("steam:9").Fps = new FpsPolicy { Mode = FpsMode.Custom, Value = 59.94 };
        var json = SameUnderDutch(() => PrefsTransfer.Export(p, includeRules: false).Split('\n').Where(l => !l.Contains("exported")).Aggregate((a, b) => a + "\n" + b));
        Assert.Contains("59.94", json);
        Assert.DoesNotContain("59,94", json);
    }

    [Fact]
    public void Saved_preferences_keep_dotted_numbers()
    {
        var p = new Prefs();
        p.Game("steam:9").Fps = new FpsPolicy { Mode = FpsMode.Custom, Value = 59.94 };
        p.Save();
        var text = File.ReadAllText(AppPathsFile());
        Assert.Contains("59.94", text);
        var (c, u) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = Dutch;
            p.Save();
            Assert.Equal(text, File.ReadAllText(AppPathsFile()));
            var back = Prefs.Load();
            Assert.Equal(59.94, back.Game("steam:9").Fps!.Value);
        }
        finally { CultureInfo.CurrentCulture = c; CultureInfo.CurrentUICulture = u; }
    }

    static string AppPathsFile() => OptiGame.Core.AppPaths.PrefsFile;

    [Fact]
    public void Frame_times_parse_and_import_the_same_under_nl_NL()
    {
        var csv = "frametime_ms\n" + string.Join("\n", Enumerable.Range(0, 200).Select(i => (16.6 + (i % 5) * 0.1).ToString("0.0", CultureInfo.InvariantCulture)));
        var res = SameUnderDutch(() =>
        {
            var (frames, fmt) = FrameTimeCsv.Parse(csv);
            var m = BenchMetrics.From(frames);
            return $"{fmt}|{frames.Count}|{frames.Sum().ToString("R", CultureInfo.InvariantCulture)}|{m.AverageFps.ToString("R", CultureInfo.InvariantCulture)}";
        });
        Assert.StartsWith("GameOptimizer CSV|200|", res);
    }

    [Fact]
    public void A_stored_benchmark_record_and_its_hardware_text_use_dots()
    {
        var csv = Path.Combine(_root, "frames.csv");
        File.WriteAllText(csv, "frametime_ms\n" + string.Join("\n", Enumerable.Repeat("16.6", 150)));
        var hw = Hw();
        var rec = SameUnderDutch(() => hw.ToString());
        Assert.Contains("15.7 GB RAM", rec);
        Assert.Contains("(7.5 GB)", rec);
        var (c, u) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = Dutch;
            var r = BenchmarkStore.Import(csv, "steam:1", "Game", "before", hw.ToString(), "Balanced", "High", "2560x1440", "59.94", null);
            var stored = File.ReadAllText(Directory.GetFiles(BenchmarkStore.Dir, "*.json").Single());
            Assert.DoesNotContain("59,94", stored);
            Assert.DoesNotContain("15,7", stored);
            Assert.DoesNotContain("16,6", stored);
            Assert.Contains("59.94", stored);
            Assert.Equal("59.94", BenchmarkStore.Find(r.Id)!.FpsCap);
        }
        finally { CultureInfo.CurrentCulture = c; CultureInfo.CurrentUICulture = u; }
    }

    [Fact]
    public void Requirement_lookups_and_gpu_matching_ignore_the_culture()
    {
        SameUnderDutch(() => GpuMatcher.Identify("NVIDIA GeForce RTX 3070 Ti Laptop GPU") is { } m ? m.Score.ToString("R", CultureInfo.InvariantCulture) : "none");
        SameUnderDutch(() => GpuMatcher.Identify("GTX 900 series") is { } m ? m.Score.ToString("R", CultureInfo.InvariantCulture) : "none");
        Assert.Equal("59.94", SameUnderDutch(() => DisplayMath.Hz(59.94)));          // a refresh rate is shown with a dot everywhere, as in the preview and the files
    }

    [Fact]
    public void Profile_values_and_cli_style_settings_parse_with_dots()
    {
        var s = new SettingsPrefs();
        SameUnderDutch(() => { var t = new SettingsPrefs(); SettingsParser.Apply(t, "fps=59.94"); return t.Fps!.Value; });
        SettingsParser.Apply(s, "fps=59.94");
        Assert.Equal(59.94, s.Fps!.Value);
        var vars = SameUnderDutch(() => Profiles.Variables(QualityStep.High, Hw()).OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + Convert.ToString(kv.Value, CultureInfo.InvariantCulture)).Aggregate((a, b) => a + ";" + b));
        Assert.DoesNotMatch(CommaDecimal, vars);
    }
}
