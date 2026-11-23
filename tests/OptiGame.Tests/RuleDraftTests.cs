using System.Globalization;
using System.Text;
using System.Text.Json;
using OptiGame.Core;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Optimize;
using OptiGame.Core.Rules;
using OptiGame.Core.Safety;

namespace OptiGame.Tests;

[Collection("data dir")]
public sealed class RuleDraftTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("optigame-rule-draft-").FullName;
    readonly string? _oldData = AppPaths.Override;

    GameInfo Game => new() { Id = "steam:1234", Name = "Fixture Game", InstallDir = _root, Store = "Steam", SteamAppId = "1234" };

    public RuleDraftTests() => AppPaths.Override = Path.Combine(_root, "data");

    [Fact]
    public void Draft_uses_the_after_value_at_the_selected_step_and_leaves_other_steps_empty()
    {
        var (before, after) = ReadFixture();
        var draft = SnapshotRuleDraft.Build(Game, before, after, QualityStep.High, []);
        var rule = ReadRule(draft);
        var file = Assert.Single(rule.Files);
        Assert.Equal("{install}/settings.ini", file.Path);
        var row = Assert.Single(file.Set);
        Assert.Equal("Video/Shadows", row.Key);
        Assert.Equal(QualitySteps.Count, row.Value.GetArrayLength());
        Assert.Equal("3", row.Value[(int)QualityStep.High].GetString());
        foreach (var step in Enum.GetValues<QualityStep>().Where(step => step != QualityStep.High))
        {
            Assert.Equal(JsonValueKind.Null, row.Value[(int)step].ValueKind);
            Assert.IsType<Keep>(RuleSet.Resolve(row.Value, (int)step, new Dictionary<string, object>(), out _));
        }
        Assert.Contains("2026-11-15T12:05:00", rule.Notes);
        Assert.Equal("OptiGame+ before/after snapshots", rule.Reference);
        Assert.Null(rule.Verified);
    }

    [Fact]
    public void Multiple_changes_require_an_explicit_key_and_unsupported_changes_are_skipped()
    {
        var (before, after) = ReadFixture();
        before.Values["Video/Rendering"] = new("int", "1");
        after.Values["Video/Rendering"] = new("int", "2");
        after.Values["Video/Added"] = new("int", "1");

        var ambiguous = Assert.Throws<ArgumentException>(() => SnapshotRuleDraft.Build(Game, before, after, QualityStep.Low, []));
        Assert.Contains("--key", ambiguous.Message);

        var chosen = SnapshotRuleDraft.Build(Game, before, after, QualityStep.Low, ["Video/Rendering"]);
        Assert.Equal("2", Assert.Single(Assert.Single(ReadRule(chosen).Files).Set).Value[0].GetString());

        Assert.Contains("added", Assert.Throws<FormatException>(() =>
            SnapshotRuleDraft.Build(Game, before, after, QualityStep.Low, ["Video/Added"])).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no recorded change", Assert.Throws<FormatException>(() =>
            SnapshotRuleDraft.Build(Game, before, after, QualityStep.Low, ["Video/VSync"])).Message);
    }

    [Fact]
    public void Wrong_targets_wrong_order_and_duplicate_snapshot_keys_are_refused()
    {
        var (before, after) = ReadFixture();
        after.Target = "{install}/different.ini";
        Assert.Contains("same settings target", Assert.Throws<FormatException>(() =>
            SnapshotRuleDraft.Build(Game, before, after, QualityStep.High, [])).Message);

        after.Target = before.Target;
        Assert.Contains("before snapshot first", Assert.Throws<FormatException>(() =>
            SnapshotRuleDraft.Build(Game, after, before, QualityStep.High, [])).Message);
        Assert.Throws<FormatException>(() => SnapshotRuleDraft.ReadSnapshot("""
            {"Format":"ini","Values":{"Video/Shadows":{"Type":"int","Display":"1"},"Video/Shadows":{"Type":"int","Display":"3"}}}
            """));
    }

    [Fact]
    public void Cli_previews_then_saves_an_inactive_draft_without_replacing_it()
    {
        var before = Fixture("snapshot-before.json");
        var after = Fixture("snapshot-after.json");
        var originalBefore = File.ReadAllBytes(before);
        var originalAfter = File.ReadAllBytes(after);
        List<string> Args() => ["--game", Game.Name, "--before", before, "--after", after, "--step", "high"];
        GameInfo Resolve(string name) { Assert.Equal(Game.Name, name); return Game; }

        Assert.Equal(0, OptiGame.Cli.Cli.DraftRule(Args(), Resolve));
        Assert.False(Directory.Exists(AppPaths.DataDir));
        Assert.Equal(0, OptiGame.Cli.Cli.DraftRule([.. Args(), "--save"], Resolve));
        var directory = Path.Combine(RuleSet.UserRulesDir, "drafts");
        var file = Assert.Single(Directory.GetFiles(directory));
        var saved = File.ReadAllBytes(file);
        Assert.Throws<IOException>(() => OptiGame.Cli.Cli.DraftRule([.. Args(), "--save"], Resolve));
        Assert.Equal(saved, File.ReadAllBytes(file));
        Assert.Single(Directory.GetFiles(directory));
        Assert.DoesNotContain(RuleSet.Load(), rule => rule.Id == "fixture-game");
        Assert.Equal(originalBefore, File.ReadAllBytes(before));
        Assert.Equal(originalAfter, File.ReadAllBytes(after));
    }

    [Fact]
    public void Json_snapshot_skips_a_literal_dotted_name_that_collides_with_a_nested_key()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"video":{"quality":2},"video.quality":99}""");
        var snapshot = Snapshot.Capture("json", path);
        Assert.Equal(new SnapValue("int", "2"), snapshot.Values["video.quality"]);
        Assert.Contains(snapshot.Unreadable, reason => reason.Contains("dotted rule key", StringComparison.Ordinal));
    }

    [Fact]
    public void Literal_json_values_keep_their_type_and_decimal_point_under_dutch_culture()
    {
        var (before, after) = ReadFixture();
        before.Format = after.Format = "json";
        before.Target = after.Target = "{install}/settings.json";
        before.Values = new() { ["scale"] = new("int", "1"), ["text"] = new("string", "old"), ["variable"] = new("string", "old") };
        after.Values = new() { ["scale"] = new("double", "0.75"), ["text"] = new("string", " new "), ["variable"] = new("string", "$fpsCap") };
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            var draft = SnapshotRuleDraft.Build(Game, before, after, QualityStep.Low, ["scale", "text"]);
            var saved = File.ReadAllText(SnapshotRuleDraft.Save(draft));
            Assert.Contains("0.75", saved);
            var set = Assert.Single(ReadRule(draft).Files).Set;
            Assert.Equal(0.75, set["scale"][0].GetDouble());
            Assert.Equal(" new ", set["text"][0].GetString());
            Assert.Throws<FormatException>(() => SnapshotRuleDraft.Build(Game, before, after, QualityStep.Low, ["variable"]));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void An_activated_fixture_draft_uses_existing_preview_apply_and_byte_exact_undo()
    {
        var (before, after) = ReadFixture();
        var rule = ReadRule(SnapshotRuleDraft.Build(Game, before, after, QualityStep.High, []));
        var path = Path.Combine(_root, "settings.ini");
        var original = Encoding.UTF8.GetBytes("[Video]\r\n; observed setting\r\nShadows=1\r\nVSync=True\r\n");
        File.WriteAllBytes(path, original);
        var keep = Planner.Plan(Game, new HardwareInfo(), QualityStep.Low, [rule]);
        Assert.Empty(keep.Files);
        Assert.Contains(keep.Skipped, line => line.Contains("no observed value", StringComparison.Ordinal));

        var plan = Planner.Plan(Game, new HardwareInfo(), QualityStep.High, [rule]);
        Assert.Single(Assert.Single(plan.Files).Changes);
        Assert.Equal(original, File.ReadAllBytes(path));
        var applied = Applier.Apply([plan], "fixture");
        Assert.Single(applied.Applied);
        Assert.Equal(Encoding.UTF8.GetBytes("[Video]\r\n; observed setting\r\nShadows=3\r\nVSync=True\r\n"), File.ReadAllBytes(path));

        var run = Applier.Runs().Single(r => r.Id == applied.RunId);
        Assert.Single(Applier.Restore(Applier.PlanRestore(run), RestoreMode.UndoChanges).Applied);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    static (Snapshot Before, Snapshot After) ReadFixture() =>
        (SnapshotRuleDraft.ReadSnapshot(File.ReadAllText(Fixture("snapshot-before.json"))),
            SnapshotRuleDraft.ReadSnapshot(File.ReadAllText(Fixture("snapshot-after.json"))));

    static GameRule ReadRule(RuleDraft draft)
    {
        var rule = Assert.IsType<GameRule>(RuleSet.TryParse(draft.Json, out var problems));
        Assert.Empty(problems);
        return rule;
    }

    public void Dispose()
    {
        AppPaths.Override = _oldData;
        Directory.Delete(_root, recursive: true);
    }
}
