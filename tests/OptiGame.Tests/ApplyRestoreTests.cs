using System.Text;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Optimize;
using OptiGame.Core.Safety;

namespace OptiGame.Tests;

/// <summary>Uses a temporary data folder and a fake game folder; touches nothing real.</summary>
[Collection("data dir")]
public sealed class ApplyRestoreTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "gameopt-test-" + Guid.NewGuid().ToString("N"));
    readonly string? _oldData = Environment.GetEnvironmentVariable("GAMEOPT_DATA_DIR");

    public ApplyRestoreTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", Path.Combine(_root, "data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", _oldData);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    GamePlan PlanFor(string file, string newText)
    {
        var game = new GameInfo { Id = "test:1", Name = "Test Game", InstallDir = Path.Combine(_root, "game"), Store = "Test" };
        Directory.CreateDirectory(game.InstallDir);
        var plan = new GamePlan { Game = game, Step = QualityStep.High, Status = PlanStatus.Ready };
        var edit = new FileEdit { Path = file, Original = File.ReadAllBytes(file), Updated = Encoding.UTF8.GetBytes(newText), Format = "ini" };
        edit.Changes.Add(new SettingChange("x", "1", "2"));
        plan.Files.Add(edit);
        return plan;
    }

    [Fact]
    public void Apply_backs_up_then_restore_puts_the_file_back()
    {
        var file = Path.Combine(_root, "settings.ini");
        File.WriteAllText(file, "[A]\nx=1\n");
        var res = Applier.Apply(new[] { PlanFor(file, "[A]\nx=2\n") }, "test");
        Assert.Empty(res.Errors);
        Assert.NotNull(res.RunId);
        Assert.Equal("[A]\nx=2\n", File.ReadAllText(file));

        var run = Assert.Single(Applier.Runs());
        var back = Applier.Restore(run);
        Assert.Single(back.Applied);
        Assert.Equal("[A]\nx=1\n", File.ReadAllText(file));
    }

    [Fact]
    public void Stale_plan_is_refused()
    {
        var file = Path.Combine(_root, "settings.ini");
        File.WriteAllText(file, "[A]\nx=1\n");
        var plan = PlanFor(file, "[A]\nx=2\n");
        File.WriteAllText(file, "[A]\nx=5\n"); // the game rewrote it after the preview
        var res = Applier.Apply(new[] { plan }, "test");
        Assert.Single(res.Refused);
        Assert.Null(res.RunId);
        Assert.Equal("[A]\nx=5\n", File.ReadAllText(file));
    }
}
