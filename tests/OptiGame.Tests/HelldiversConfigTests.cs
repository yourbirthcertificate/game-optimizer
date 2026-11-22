using System.Diagnostics;
using System.Globalization;
using System.Text;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Library.ConfigDiscovery;
using OptiGame.Core.Optimize;
using OptiGame.Core.Safety;

namespace OptiGame.Tests;

[Collection("data dir")]
public sealed class HelldiversConfigTests : IDisposable
{
    // Synthetic data with the observed bare assignments, objects, and whitespace-separated arrays.
    const string Config = """
        adapter_index = 0
        /* café: vsync = false */
        render_settings = {
            resolution = [1920 1080]
            sharpening_amount = 0.8500000000000001
            vsync = true
            label = "quote \" then } and // inside the string"
        }
        vsync  =  true // preserve this comment
        borderless_fullscreen = true
        """;

    readonly string _root = Path.Combine(Path.GetTempPath(), "optigame-hd-" + Guid.NewGuid().ToString("N"));
    readonly string? _oldData = Environment.GetEnvironmentVariable("GAMEOPT_DATA_DIR");
    readonly string _oldRoaming = Planner.RoamingAppData;
    readonly string _path;
    readonly GameInfo _game;

    public HelldiversConfigTests()
    {
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", Path.Combine(_root, "data"));
        Planner.RoamingAppData = Path.Combine(_root, "roaming");
        _path = Path.Combine(Planner.RoamingAppData, "Arrowhead", "Helldivers2", "user_settings.config");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _game = new GameInfo
        {
            Id = "steam:553850", SteamAppId = "553850", Name = "Helldivers 2", Store = "Steam",
            InstallDir = Directory.CreateDirectory(Path.Combine(_root, "install")).FullName,
        };
    }

    GamePlan Preview(VSyncChoice choice = VSyncChoice.Off, bool preserve = false)
    {
        var hardware = new HardwareInfo { NativeWidth = 1920, NativeHeight = 1080 };
        return Planner.Plan(_game, hardware, new OptimizeRequest
        {
            Display = hardware.Primary, VSync = choice, Preserve = preserve ? [Categories.VSync] : [],
        }, []);
    }

    [Theory]
    [InlineData(false, VSyncChoice.Off)]
    [InlineData(true, VSyncChoice.On)]
    public void Preview_apply_and_undo_preserve_all_other_bytes_under_dutch_culture(bool bom, VSyncChoice choice)
    {
        var old = choice == VSyncChoice.Off ? "true" : "false";
        var next = choice == VSyncChoice.Off ? "false" : "true";
        var encoding = new UTF8Encoding(bom, true);
        byte[] Bytes(string token)
        {
            var text = Config.Replace("vsync  =  true", "vsync  =  " + token)
                .Replace("\r\n", "\n").Replace("\n", "\r\n");
            return [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
        }

        var original = Bytes(old);
        var expected = Bytes(next);
        File.WriteAllBytes(_path, original);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            Assert.Equal("5,0", 5.0.ToString("0.0", CultureInfo.CurrentCulture));
            var plan = Preview(choice);
            Assert.Equal(PlanStatus.Ready, plan.Status);
            var edit = Assert.Single(plan.Files);
            Assert.Equal(next, Assert.Single(edit.Changes).NewRaw);
            Assert.Equal(original, File.ReadAllBytes(_path));
            Assert.Equal(expected, edit.Updated);
            var result = Applier.Apply([plan], "fixture");
            Assert.Single(result.Applied);
            Assert.Equal(expected, File.ReadAllBytes(_path));
            var undo = Applier.PlanRestore(Applier.Runs().Single(run => run.Id == result.RunId));
            Assert.Single(Applier.Restore(undo, RestoreMode.UndoChanges).Applied);
            Assert.Equal(original, File.ReadAllBytes(_path));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Key_level_undo_keeps_later_changes_outside_vsync()
    {
        File.WriteAllText(_path, Config);
        var result = Applier.Apply([Preview()], "fixture");
        Assert.Single(result.Applied);
        var edited = File.ReadAllText(_path).Replace("0.8500000000000001", "0.625");
        File.WriteAllText(_path, edited);
        var undo = Applier.PlanRestore(Applier.Runs().Single(run => run.Id == result.RunId));
        Assert.True(Assert.Single(undo.Items).ChangedSinceOptimize);
        Assert.Single(Applier.Restore(undo, RestoreMode.UndoChanges).Applied);
        Assert.Equal(Encoding.UTF8.GetBytes(Config.Replace("0.8500000000000001", "0.625")), File.ReadAllBytes(_path));
    }

    [Fact]
    public void Apply_refuses_a_stale_config()
    {
        File.WriteAllText(_path, Config);
        var plan = Preview();
        File.AppendAllText(_path, "\n// updated after preview");
        var before = File.ReadAllBytes(_path);
        Assert.Contains("changed since the preview", Assert.Single(Applier.Apply([plan], "fixture").Refused));
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void Apply_refuses_a_running_dummy_from_the_install_folder()
    {
        File.WriteAllText(_path, Config);
        var plan = Preview();
        var exe = Path.Combine(_game.InstallDir, "test-game.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        using var dummy = Process.Start(new ProcessStartInfo(exe, "-t 127.0.0.1")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
        })!;
        try
        {
            Assert.Single(Applier.Apply([plan], "fixture").Refused);
            Assert.Equal(PlanStatus.Running, plan.Status);
            Assert.Equal(Config, File.ReadAllText(_path));
        }
        finally
        {
            if (!dummy.HasExited) dummy.Kill();
            dummy.WaitForExit(2000);
        }
    }

    [Theory]
    [InlineData("render_settings = { vsync = true }")]
    [InlineData("render_settings = {} vsync = 1")]
    [InlineData("render_settings = {} vsync = true \"vsync\" = false")]
    [InlineData("render_settings = {} vsync = true another = [")]
    [InlineData("render_settings = {}vsync = true")]
    public void Unsupported_or_ambiguous_configs_are_skipped(string input)
    {
        File.WriteAllText(_path, input);
        var plan = Preview();
        Assert.Equal(PlanStatus.Skipped, plan.Status);
        Assert.NotEmpty(plan.Skipped);
        Assert.Empty(plan.Files);
        Assert.Equal(input, File.ReadAllText(_path));
    }

    [Fact]
    public void A_config_that_has_not_been_created_is_left_missing()
    {
        var plan = Preview();
        Assert.Equal(SkipKind.NotCreatedYet, plan.SkipKind);
        Assert.Null(Applier.Apply([plan], "fixture").RunId);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Keep_preference_and_preserve_category_leave_vsync_unchanged()
    {
        File.WriteAllText(_path, Config);
        Assert.Empty(Preview(VSyncChoice.Keep).Files);
        var preserved = Preview(preserve: true);
        Assert.Empty(preserved.Files);
        Assert.Single(preserved.Excluded);
        Assert.Equal(Config, File.ReadAllText(_path));
    }

    [Fact]
    public void The_reviewed_location_is_confirmed_with_the_planners_parser()
    {
        File.WriteAllText(_path, Config);
        var roots = new ConfigRoots("", "", Planner.RoamingAppData, "", "", "", "", Path.Combine(_root, "data"));
        var result = new ConfigFinder(roots, _ => throw new InvalidOperationException("Unexpected registry read.")).Find(_game);
        Assert.Equal(_path, Assert.Single(result.Candidates, candidate => candidate.Confidence == ConfigConfidence.Confirmed).Path);
    }

    public void Dispose()
    {
        Planner.RoamingAppData = _oldRoaming;
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", _oldData);
        Directory.Delete(_root, recursive: true);
    }
}
