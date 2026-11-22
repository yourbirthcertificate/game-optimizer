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
public sealed class ZomboidOptionsTests : IDisposable
{
    // Synthetic values following the game's observed sectionless layout.
    const string Ini = "version=7\r\nwidth=1920\nheight=1080\r\n; café remains intact\n" +
        "vsync=true\r\nzoomLevels2x=200;150;100\nminChatOpaque=1.0\r\nseenNews=";

    readonly string _root = Path.Combine(Path.GetTempPath(), "optigame-zomboid-" + Guid.NewGuid().ToString("N"));
    readonly string? _oldData = Environment.GetEnvironmentVariable("GAMEOPT_DATA_DIR");
    readonly string _oldProfile = Planner.ZomboidUserProfile;
    readonly string _path;
    readonly GameInfo _game;

    public ZomboidOptionsTests()
    {
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", Path.Combine(_root, "data"));
        Planner.ZomboidUserProfile = Path.Combine(_root, "profile");
        _path = Path.Combine(Planner.ZomboidUserProfile, "Zomboid", "options.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _game = new GameInfo
        {
            Id = "steam:108600", SteamAppId = "108600", Name = "Project Zomboid", Store = "Steam",
            InstallDir = Directory.CreateDirectory(Path.Combine(_root, "install")).FullName,
        };
    }

    GamePlan Preview(VSyncChoice choice = VSyncChoice.Off, bool preserve = false)
    {
        var hardware = new HardwareInfo { NativeWidth = 2560, NativeHeight = 1440 };
        return Planner.Plan(_game, hardware, new OptimizeRequest
        {
            Display = hardware.Primary, VSync = choice, Preserve = preserve ? [Categories.VSync] : [],
        }, []);
    }

    [Theory]
    [InlineData(false, VSyncChoice.Off)]
    [InlineData(true, VSyncChoice.On)]
    public void Preview_apply_and_undo_preserve_every_other_byte_under_dutch_culture(bool bom, VSyncChoice choice)
    {
        var old = choice == VSyncChoice.Off ? "true" : "false";
        var next = choice == VSyncChoice.Off ? "false" : "true";
        var encoding = new UTF8Encoding(bom, true);
        byte[] Bytes(string value) => [.. encoding.GetPreamble(), .. encoding.GetBytes(Ini.Replace("vsync=true", "vsync=" + value))];

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
            var change = Assert.Single(edit.Changes);
            Assert.Equal("VSync", change.Setting);
            Assert.Equal(next, change.NewRaw);
            Assert.Equal(original, File.ReadAllBytes(_path));
            Assert.Equal(expected, edit.Updated);

            var result = Applier.Apply([plan], "fixture");
            Assert.Single(result.Applied);
            Assert.Equal(expected, File.ReadAllBytes(_path));
            var restore = Applier.PlanRestore(Applier.Runs().Single(run => run.Id == result.RunId));
            Assert.Single(Applier.Restore(restore, RestoreMode.UndoChanges).Applied);
            Assert.Equal(original, File.ReadAllBytes(_path));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Key_level_undo_preserves_later_edits_to_other_options()
    {
        File.WriteAllText(_path, Ini);
        var result = Applier.Apply([Preview()], "fixture");
        Assert.Single(result.Applied);
        var newer = File.ReadAllText(_path).Replace("minChatOpaque=1.0", "minChatOpaque=0.65");
        File.WriteAllText(_path, newer);
        var restore = Applier.PlanRestore(Applier.Runs().Single(run => run.Id == result.RunId));
        Assert.True(Assert.Single(restore.Items).ChangedSinceOptimize);
        Assert.Single(Applier.Restore(restore, RestoreMode.UndoChanges).Applied);
        Assert.Equal(Encoding.UTF8.GetBytes(Ini.Replace("minChatOpaque=1.0", "minChatOpaque=0.65")), File.ReadAllBytes(_path));
    }

    [Fact]
    public void Apply_refuses_a_stale_preview()
    {
        File.WriteAllText(_path, Ini);
        var plan = Preview();
        File.AppendAllText(_path, "\r\nnewOption=true");
        var before = File.ReadAllBytes(_path);
        Assert.Contains("changed since the preview", Assert.Single(Applier.Apply([plan], "fixture").Refused));
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void Apply_refuses_a_running_dummy_from_the_install_folder()
    {
        File.WriteAllText(_path, Ini);
        var plan = Preview();
        var exe = Path.Combine(_game.InstallDir, "zomboid-fixture.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        using var dummy = Process.Start(new ProcessStartInfo(exe, "-t 127.0.0.1")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
        })!;
        try
        {
            Assert.Single(Applier.Apply([plan], "fixture").Refused);
            Assert.Equal(PlanStatus.Running, plan.Status);
            Assert.Equal(Ini, File.ReadAllText(_path));
        }
        finally
        {
            if (!dummy.HasExited) dummy.Kill();
            dummy.WaitForExit(2000);
        }
    }

    [Theory]
    [InlineData("\nVSYNC=false")]
    [InlineData("\nvsync=false")]
    [InlineData("\n[Video]\nvsync=false")]
    [InlineData("\nline without an assignment")]
    public void Ambiguous_or_unsupported_ini_is_skipped(string extra)
    {
        var input = Ini + extra;
        File.WriteAllText(_path, input);
        var plan = Preview();
        Assert.Equal(PlanStatus.Skipped, plan.Status);
        Assert.NotEmpty(plan.Skipped);
        Assert.Empty(plan.Files);
        Assert.Equal(input, File.ReadAllText(_path));
    }

    [Fact]
    public void Keep_and_preserve_leave_the_config_unchanged()
    {
        File.WriteAllText(_path, Ini);
        Assert.Empty(Preview(VSyncChoice.Keep).Files);
        var preserved = Preview(preserve: true);
        Assert.Empty(preserved.Files);
        Assert.Single(preserved.Excluded);
        Assert.Equal(Ini, File.ReadAllText(_path));
    }

    [Fact]
    public void A_missing_config_is_not_created()
    {
        var plan = Preview();
        Assert.Equal(SkipKind.NotCreatedYet, plan.SkipKind);
        Assert.Null(Applier.Apply([plan], "fixture").RunId);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Discovery_confirms_only_the_expected_zomboid_structure()
    {
        var roots = new ConfigRoots("", "", "", "", "", "", Planner.ZomboidUserProfile, Path.Combine(_root, "data"));
        var finder = new ConfigFinder(roots, _ => throw new InvalidOperationException("Unexpected registry read."));
        File.WriteAllText(_path, "vsync=true");
        Assert.Equal(ConfigConfidence.Present, Assert.Single(finder.Find(_game).Candidates).Confidence);

        File.WriteAllText(_path, Ini);
        var match = Assert.Single(finder.Find(_game).Candidates);
        Assert.Equal(_path, match.Path);
        Assert.Equal(ConfigConfidence.Confirmed, match.Confidence);
    }

    public void Dispose()
    {
        Planner.ZomboidUserProfile = _oldProfile;
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", _oldData);
        Directory.Delete(_root, recursive: true);
    }
}
