using System.Diagnostics;
using System.Globalization;
using System.Text;
using OptiGame.Core;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Optimize;
using OptiGame.Core.Safety;

namespace OptiGame.Tests;

[Collection("data dir")]
public sealed class CounterStrike2Tests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("optigame-cs2-").FullName;
    readonly string? _oldData = AppPaths.Override;
    readonly Func<string?> _oldSteamRoot = Planner.CounterStrikeSteamRoot;
    readonly string _login;
    readonly string _settings;
    readonly GameInfo _game;

    public CounterStrike2Tests()
    {
        AppPaths.Override = Path.Combine(_root, "data");
        var steam = Directory.CreateDirectory(Path.Combine(_root, "Steam")).FullName;
        Planner.CounterStrikeSteamRoot = () => steam;
        _login = Path.Combine(Directory.CreateDirectory(Path.Combine(steam, "config")).FullName, "loginusers.vdf");
        File.WriteAllText(_login, """ "users" { "76561197960265729" { "MostRecent" "1" } } """);
        _settings = Path.Combine(Directory.CreateDirectory(Path.Combine(steam, "userdata", "1", "730", "local", "cfg")).FullName,
            "cs2_video.txt");
        _game = new GameInfo
        {
            Id = "steam:730", SteamAppId = "730", Store = "Steam", Engine = GameEngine.Source2, Name = "Counter-Strike 2",
            InstallDir = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName,
        };
    }

    [Theory]
    [InlineData("1", "0", VSyncChoice.Off)]
    [InlineData("0", "1", VSyncChoice.On)]
    public void Preview_apply_and_undo_change_only_the_observed_vsync_token_under_dutch_culture(
        string before, string after, VSyncChoice choice)
    {
        var original = Encoding.UTF8.GetBytes(Fixture(before));
        var expected = Encoding.UTF8.GetBytes(Fixture(after));
        File.WriteAllBytes(_settings, original);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            var plan = Plan(choice);
            Assert.Equal(PlanStatus.Ready, plan.Status);
            var edit = Assert.Single(plan.Files);
            Assert.Equal(_settings, edit.Path);
            Assert.Equal(after, Assert.Single(edit.Changes).NewRaw);
            Assert.Equal(original, File.ReadAllBytes(_settings));
            Assert.Equal(expected, edit.Updated);

            var applied = Applier.Apply([plan], "fixture");
            Assert.Single(applied.Applied);
            Assert.Equal(expected, File.ReadAllBytes(_settings));
            var run = Applier.Runs().Single(item => item.Id == applied.RunId);
            Assert.Single(Applier.Restore(Applier.PlanRestore(run), RestoreMode.UndoChanges).Applied);
            Assert.Equal(original, File.ReadAllBytes(_settings));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Refuses_multiple_local_accounts_and_duplicate_vsync_keys()
    {
        File.WriteAllText(_settings, Fixture("1").Replace("\t\"Version\"", "\t\"setting.mat_vsync\" \"0\"\r\n\t\"Version\"", StringComparison.Ordinal));
        var duplicate = Plan(VSyncChoice.Off);
        Assert.Equal(SkipKind.Ambiguous, duplicate.SkipKind);
        Assert.Empty(duplicate.Files);

        File.WriteAllText(_settings, Fixture("1"));
        File.WriteAllText(_login, """ "users" { "76561197960265729" {} "76561197960265730" {} } """);
        var ambiguous = Plan(VSyncChoice.Off);
        Assert.Equal(SkipKind.LocationNotFound, ambiguous.SkipKind);
        Assert.Empty(ambiguous.Files);
        Assert.Equal(Fixture("1"), File.ReadAllText(_settings));
    }

    [Fact]
    public void Apply_refuses_a_stale_Counter_Strike_2_preview()
    {
        File.WriteAllText(_settings, Fixture("1"));
        var plan = Plan(VSyncChoice.Off);
        var current = Fixture("1").Replace("\"16\"", "\"17\"", StringComparison.Ordinal);
        File.WriteAllText(_settings, current);
        Assert.Single(Applier.Apply([plan], "fixture").Refused);
        Assert.Equal(current, File.ReadAllText(_settings));
    }

    [Fact]
    public void Apply_refuses_a_running_process_from_the_fixture_install()
    {
        File.WriteAllText(_settings, Fixture("1"));
        var path = Path.Combine(_game.InstallDir, "cs2-fixture.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), path);
        using var dummy = Process.Start(new ProcessStartInfo(path, "-t 127.0.0.1")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
        })!;
        try
        {
            var plan = Plan(VSyncChoice.Off);
            Assert.Single(Applier.Apply([plan], "fixture").Refused);
            Assert.Equal(PlanStatus.Running, plan.Status);
            Assert.Equal(Fixture("1"), File.ReadAllText(_settings));
        }
        finally
        {
            if (!dummy.HasExited) dummy.Kill();
            dummy.WaitForExit(2000);
        }
    }

    GamePlan Plan(VSyncChoice choice) => Planner.Plan(_game, new HardwareInfo(), new OptimizeRequest
    {
        Display = new DisplayInfo(), Step = QualityStep.High, VSync = choice,
    }, []);

    static string Fixture(string value) =>
        "\"video.cfg\"\r\n{\r\n\t// keep this comment\r\n\t\"setting.mat_vsync\"\t\"" + value + "\"\r\n\t\"Version\" \"16\"\r\n}\r\n";

    public void Dispose()
    {
        AppPaths.Override = _oldData;
        Planner.CounterStrikeSteamRoot = _oldSteamRoot;
        Directory.Delete(_root, recursive: true);
    }
}
