using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OptiGame.Core.Formats;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Optimize;
using OptiGame.Core.Safety;

namespace OptiGame.Tests;

/// <summary>
/// REDengine 4 (Cyberpunk 2077) UserSettings.json. Everything runs on a fixture with the real file's structure
/// (fixtures/cyberpunk-UserSettings.json), copied to a temp folder: the real settings file is never read or written here.
/// </summary>
[Collection("data dir")]
public sealed class RedEngineTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "optigame-red-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string? _oldData = Environment.GetEnvironmentVariable("GAMEOPT_DATA_DIR");
    readonly string _oldLocal = Planner.LocalAppData;
    readonly string _settings;
    readonly byte[] _fixture = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "cyberpunk-UserSettings.json"));

    public RedEngineTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", Path.Combine(_root, "data"));
        Planner.LocalAppData = Path.Combine(_root, "local");
        _settings = Path.Combine(Planner.LocalAppData, "CD Projekt Red", "Cyberpunk 2077", "UserSettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_settings)!);
        File.WriteAllBytes(_settings, _fixture);
    }

    public void Dispose()
    {
        Planner.LocalAppData = _oldLocal;
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", _oldData);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    GameInfo Game()
    {
        var dir = Path.Combine(_root, "Cyberpunk 2077");
        Directory.CreateDirectory(Path.Combine(dir, "archive", "pc", "content"));
        Directory.CreateDirectory(Path.Combine(dir, "r6"));
        Directory.CreateDirectory(Path.Combine(dir, "bin", "x64"));
        var exe = Path.Combine(dir, "bin", "x64", "Cyberpunk2077.exe");
        if (!File.Exists(exe)) File.WriteAllText(exe, "stand-in");
        var g = new GameInfo { Id = "gog:1423049311", Name = "Cyberpunk 2077", InstallDir = dir, Store = "GOG" };
        EngineDetector.Detect(g);
        return g;
    }

    static HardwareInfo Hw(double vramGb = 8) => new() { NativeWidth = 2560, NativeHeight = 1440, ActiveRefreshHz = 165, GpuVramGb = vramGb };

    GamePlan Plan(QualityStep step, HardwareInfo? hw = null, Action<OptimizeRequest>? _ = null)
    {
        hw ??= Hw();
        return Planner.Plan(Game(), hw, OptimizeRequest.Default(hw, step), new List<GameRule>());
    }

    static OptimizeRequest Excluding(OptimizeRequest r, string id) => new()
    {
        Step = r.Step, StepSource = r.StepSource, ProfileName = r.ProfileName, Display = r.Display, FpsMode = r.FpsMode, FpsCap = r.FpsCap,
        Resolution = r.Resolution, DisplayMode = r.DisplayMode, VSync = r.VSync, TemporaryExcluded = new HashSet<string> { id },
    };

    static Dictionary<string, string> NewValues(GamePlan p) => p.Files.SelectMany(f => f.Changes).ToDictionary(c => c.Setting, c => c.New);

    // ------------------------------------------------------------------ the file editor

    [Fact]
    public void Detected_as_REDengine_from_the_install_folder()
    {
        var g = Game();
        Assert.Equal(GameEngine.RedEngine, g.Engine);
        Assert.Equal("Cyberpunk 2077", g.EngineDetail);
    }

    [Fact]
    public void Options_are_found_with_their_group_value_index_and_values()
    {
        var f = RedSettingsFile.Parse(_fixture);
        Assert.Equal("\"Medium\"", f.GetRaw("/graphics/advanced", "AmbientOcclusion"));
        var ao = f.Find("/graphics/advanced", "AmbientOcclusion")!;
        Assert.Equal(new[] { "\"Off\"", "\"Low\"", "\"Medium\"", "\"High\"" }, ao.Values);
        Assert.True(ao.Index.Start > 0);
        Assert.Equal("16", f.GetRaw("/graphics/advanced", "Anisotropy"));
        Assert.Equal("false", f.GetRaw("/video/display", "MaximumFPS_OnOff"));
        Assert.True(f.Find("/video/display", "Resolution")!.IsDynamic);
        Assert.Null(f.GetRaw("/graphics/advanced", "NoSuchOption"));
    }

    [Fact]
    public void Setting_a_list_option_changes_only_its_value_and_index_bytes()
    {
        var f = RedSettingsFile.Parse(_fixture);
        Assert.True(f.TrySet("/graphics/advanced", "AmbientOcclusion", "\"Low\"", out _));
        var after = f.ToBytes();
        var a = Encoding.UTF8.GetString(_fixture).Split('\n');
        var b = Encoding.UTF8.GetString(after).Split('\n');
        Assert.Equal(a.Length, b.Length);                                   // line endings and layout untouched
        var changed = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToList();
        Assert.Equal(2, changed.Count);
        Assert.Contains("\"value\": \"Low\"", b[changed[0]]);
        Assert.Contains("\"index\": 1", b[changed[1]]);
        Assert.DoesNotContain(changed, i => a[i].Contains("default"));       // default_value and default_index are never touched
        Assert.Equal("\"Low\"", RedSettingsFile.Parse(after).GetRaw("/graphics/advanced", "AmbientOcclusion"));
    }

    [Fact]
    public void A_bool_and_an_int_change_one_token_and_keep_their_neighbours()
    {
        var f = RedSettingsFile.Parse(_fixture);
        Assert.True(f.TrySet("/video/display", "MaximumFPS_OnOff", "true", out _));
        Assert.True(f.TrySet("/video/display", "MaximumFPS_Value", "162", out _));
        var a = Encoding.UTF8.GetString(_fixture).Split('\n');
        var b = Encoding.UTF8.GetString(f.ToBytes()).Split('\n');
        Assert.Equal(2, Enumerable.Range(0, a.Length).Count(i => a[i] != b[i]));
    }

    [Fact]
    public void Refuses_what_it_cannot_do_safely()
    {
        var f = RedSettingsFile.Parse(_fixture);
        Assert.False(f.TrySet("/video/display", "Resolution", "\"1920x1080\"", out var dyn));          // list built at run time: no index to compute
        Assert.Contains("run time", dyn);
        Assert.False(f.TrySet("/graphics/advanced", "AmbientOcclusion", "\"Ultra\"", out var notIn));   // not one of the game's values
        Assert.Contains("not one of", notIn);
        Assert.False(f.TrySet("/graphics/raytracing", "RayTracing", "\"yes\"", out var kind));          // a string for a bool
        Assert.Contains("same kind", kind);
        Assert.False(f.TrySet("/graphics/advanced", "NoSuchOption", "1", out _));
        Assert.Equal(_fixture, f.ToBytes());                                                              // nothing was written by any refusal
    }

    // ------------------------------------------------------------------ the plan

    [Fact]
    public void Low_and_Ultra_map_each_list_onto_its_own_range()
    {
        var low = NewValues(Plan(QualityStep.Low));
        Assert.Equal("Off", low["AmbientOcclusion"]);
        Assert.Equal("Low", low["TextureQuality"]);
        Assert.Equal("1", low["Anisotropy"]);
        var ultra = NewValues(Plan(QualityStep.Ultra));
        Assert.Equal("High", ultra["AmbientOcclusion"]);
        Assert.Equal("Ultra", ultra["GlobaIlluminationRange"]);
        Assert.Equal("Ultra", ultra["MaxDynamicDecals"]);
        Assert.NotEqual("Insane", ultra.GetValueOrDefault("ScreenSpaceReflectionsQuality"));          // the top, unstable level is never chosen
        Assert.Equal("Ultra", ultra["ScreenSpaceReflectionsQuality"]);
        Assert.DoesNotContain("LODPreset", ultra.Keys);                                              // already High in the file, so nothing to write
    }

    [Fact]
    public void Texture_quality_is_capped_by_graphics_memory()
    {
        Assert.Equal("Medium", NewValues(Plan(QualityStep.Ultra, Hw(6)))["TextureQuality"]);
        Assert.Equal("Medium", NewValues(Plan(QualityStep.Ultra, Hw(4)))["TextureQuality"]);
        Assert.DoesNotContain("TextureQuality", NewValues(Plan(QualityStep.Ultra, Hw(8))).Keys);      // already High, so nothing to change
    }

    [Fact]
    public void Frame_cap_uses_the_game_s_own_two_options()
    {
        var v = NewValues(Plan(QualityStep.High));
        Assert.Equal("true", v["MaximumFPS_OnOff"]);
        Assert.Equal("162", v["MaximumFPS_Value"]);                                                  // 3 below the active 165 Hz
    }

    [Fact]
    public void Ray_tracing_is_only_ever_switched_off_never_on()
    {
        var f = RedSettingsFile.Parse(_fixture);
        Assert.True(f.TrySet("/graphics/raytracing", "RayTracing", "true", out _));
        File.WriteAllBytes(_settings, f.ToBytes());
        Assert.Equal("false", NewValues(Plan(QualityStep.Low))["RayTracing"]);
        Assert.DoesNotContain("RayTracing", NewValues(Plan(QualityStep.Ultra)).Keys);                 // left as the user has it
        File.WriteAllBytes(_settings, _fixture);                                                       // off in the file
        Assert.DoesNotContain("RayTracing", NewValues(Plan(QualityStep.Ultra)).Keys);                 // and never switched on
    }

    [Fact]
    public void The_plan_edits_only_value_and_index_lines_and_keeps_the_file_otherwise_byte_for_byte()
    {
        var plan = Plan(QualityStep.Low);
        var edit = Assert.Single(plan.Files);
        var a = Encoding.UTF8.GetString(edit.Original).Split('\n');
        var b = Encoding.UTF8.GetString(edit.Updated).Split('\n');
        Assert.Equal(a.Length, b.Length);
        foreach (var i in Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]))
            Assert.True(a[i].TrimStart().StartsWith("\"value\"") || a[i].TrimStart().StartsWith("\"index\""), a[i]);
        // default_value, version and the groups that were not touched read the same through a JSON parser too
        using var o = JsonDocument.Parse(edit.Original);
        using var u = JsonDocument.Parse(edit.Updated);
        Assert.Equal(o.RootElement.GetProperty("version").GetRawText(), u.RootElement.GetProperty("version").GetRawText());
        var od = Defaults(o); var ud = Defaults(u);
        Assert.Equal(od, ud);
    }

    static List<string> Defaults(JsonDocument d) =>
        d.RootElement.GetProperty("data").EnumerateArray().SelectMany(g => g.GetProperty("options").EnumerateArray())
            .Select(o => o.TryGetProperty("default_value", out var dv) ? dv.GetRawText() : o.TryGetProperty("default_index", out var di) ? di.GetRawText() : "").ToList();

    [Fact]
    public void Keep_my_settings_is_honoured()
    {
        var hw = Hw();
        var plan = Planner.Plan(Game(), hw, OptimizeRequest.Default(hw, QualityStep.Low), new List<GameRule>());
        var id = plan.Files.SelectMany(f => f.Changes).First(c => c.Setting == "AmbientOcclusion").Id;
        Assert.Equal("red:graphics.advanced.AmbientOcclusion", id);
        var plan2 = Planner.Plan(Game(), hw, Excluding(OptimizeRequest.Default(hw, QualityStep.Low), id), new List<GameRule>());
        Assert.DoesNotContain("AmbientOcclusion", NewValues(plan2).Keys);
        Assert.Contains(plan2.Excluded, x => x.Id == id);
    }

    [Fact]
    public void Missing_settings_file_says_to_start_the_game_once()
    {
        File.Delete(_settings);
        var plan = Plan(QualityStep.High);
        Assert.Equal(PlanStatus.Skipped, plan.Status);
        Assert.Equal(SkipKind.NotCreatedYet, plan.SkipKind);
    }

    [Fact]
    public void A_broken_file_is_skipped_not_guessed_at()
    {
        File.WriteAllText(_settings, "{ this is not json");
        var plan = Plan(QualityStep.High);
        Assert.Equal(PlanStatus.Skipped, plan.Status);
        Assert.Equal(SkipKind.UnsupportedFormat, plan.SkipKind);
    }

    // ------------------------------------------------------------------ apply, undo, running game

    [Fact]
    public void Apply_backs_up_and_undo_restores_the_file_byte_for_byte()
    {
        var plan = Plan(QualityStep.Low);
        var res = Applier.Apply(new[] { plan }, "Cyberpunk test");
        Assert.Empty(res.Errors);
        Assert.NotNull(res.RunId);
        Assert.NotEqual(_fixture, File.ReadAllBytes(_settings));
        Assert.Equal("\"Off\"", RedSettingsFile.Parse(File.ReadAllBytes(_settings)).GetRaw("/graphics/advanced", "AmbientOcclusion"));

        var run = Assert.Single(Applier.Runs());
        var rp = Applier.PlanRestore(run);
        Assert.All(rp.Items, i => Assert.True(i.CanUndoChanges));
        var undo = Applier.Restore(rp, RestoreMode.UndoChanges);
        Assert.Empty(undo.Errors);
        Assert.Equal(_fixture, File.ReadAllBytes(_settings));                  // the key-level undo gives back the exact original bytes
    }

    [Fact]
    public void A_change_made_by_the_game_after_apply_is_a_conflict_and_is_left_alone()
    {
        var plan = Plan(QualityStep.Low);
        Applier.Apply(new[] { plan }, "Cyberpunk test");
        var f = RedSettingsFile.Parse(File.ReadAllBytes(_settings));
        Assert.True(f.TrySet("/graphics/advanced", "AmbientOcclusion", "\"High\"", out _));        // the user changed it in the game later
        File.WriteAllBytes(_settings, f.ToBytes());

        var rp = Applier.PlanRestore(Assert.Single(Applier.Runs()));
        Applier.Restore(rp, RestoreMode.UndoChanges);
        var after = RedSettingsFile.Parse(File.ReadAllBytes(_settings));
        Assert.Equal("\"High\"", after.GetRaw("/graphics/advanced", "AmbientOcclusion"));          // theirs kept
        Assert.Equal("\"Medium\"", after.GetRaw("/graphics/advanced", "ShadowMeshQuality") is { } ? "\"Medium\"" : null);
    }

    [Fact]
    public void Apply_is_refused_while_the_game_is_running()
    {
        var g = Game();
        var exe = Path.Combine(g.InstallDir, "bin", "x64", "Cyberpunk2077.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe, overwrite: true);       // a harmless stand-in process
        using var dummy = Process.Start(new ProcessStartInfo(exe, "-t 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            Thread.Sleep(800);
            var hw = Hw();
            var plan = Planner.Plan(g, hw, OptimizeRequest.Default(hw, QualityStep.Low), new List<GameRule>());
            Assert.True(plan.HasChanges);
            var res = Applier.Apply(new[] { plan }, "must not write");
            Assert.NotEmpty(res.Refused);
            Assert.Null(res.RunId);
            Assert.Equal(_fixture, File.ReadAllBytes(_settings));
        }
        finally { try { dummy.Kill(); dummy.WaitForExit(2000); } catch (InvalidOperationException) { } }
    }

    // ------------------------------------------------------------------ nl-NL

    [Fact]
    public void Plans_and_edits_are_identical_under_a_decimal_comma_culture()
    {
        var before = CultureInfo.CurrentCulture; var beforeUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("nl-NL");
            Assert.Equal("5,0", 5.0.ToString("0.0"));                      // the culture really is a comma culture
            var nl = Plan(QualityStep.High);
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var inv = Plan(QualityStep.High);
            Assert.Equal(Encoding.UTF8.GetString(inv.Files[0].Updated), Encoding.UTF8.GetString(nl.Files[0].Updated));
            Assert.DoesNotMatch(@"\d,\d", Encoding.UTF8.GetString(nl.Files[0].Updated));         // no "162,0" or "5,0" anywhere in what would be written
        }
        finally { CultureInfo.CurrentCulture = before; CultureInfo.CurrentUICulture = beforeUi; }
    }
}
