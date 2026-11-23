using System.Text.Json;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Optimize;
using OptiGame.Core.Rules;

namespace OptiGame.Tests;

public sealed class SkippedRuleDraftReportTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("optigame-draft-report-").FullName;

    [Fact]
    public void Reports_one_observed_draft_and_isolates_missing_mismatched_and_unused_pairs()
    {
        var before = CopyFixture("snapshot-before.json");
        var after = CopyFixture("snapshot-after.json");
        File.WriteAllText(Path.Combine(_root, "wrong.json"),
            File.ReadAllText(after).Replace("{install}/settings.ini", "{install}/different.ini", StringComparison.Ordinal));
        var manifest = WriteManifest("""
            [
              {"gameId":"steam:1","before":"snapshot-before.json","after":"snapshot-after.json","step":"high"},
              {"gameId":"steam:2","before":"snapshot-before.json","after":"wrong.json","step":"low"},
              {"gameId":"steam:3","before":"snapshot-before.json","after":"absent.json","step":"high"},
              {"gameId":"steam:5","before":"absent.json","after":"absent.json","step":"high"}
            ]
            """);
        var beforeBytes = File.ReadAllBytes(before);
        var afterBytes = File.ReadAllBytes(after);

        var report = SkippedRuleDraftReport.Build([Plan("4"), Plan("3"), Plan("2"), Plan("1"), Plan("5", PlanStatus.Ready)], manifest);
        Assert.Equal(["steam:5"], report.UnusedGameIds);
        Assert.Collection(report.Games,
            ready =>
            {
                Assert.Equal(DraftReportStatus.DraftNeedsConfirmation, ready.Status);
                Assert.Equal(SkipKind.NoHandler, ready.SkipKind);
                Assert.Contains("Confirm", ready.Review);
                var rule = Assert.IsType<GameRule>(RuleSet.TryParse(ready.Draft!.Value.GetRawText(), out var problems));
                Assert.Empty(problems);
                var row = Assert.Single(Assert.Single(rule.Files).Set);
                Assert.Equal("Video/Shadows", row.Key);
                Assert.Equal("3", row.Value[(int)QualityStep.High].GetString());
                Assert.Equal(1, row.Value.EnumerateArray().Count(value => value.ValueKind != JsonValueKind.Null));
            },
            mismatch =>
            {
                Assert.Equal(DraftReportStatus.NeedsReview, mismatch.Status);
                Assert.Null(mismatch.Draft);
                Assert.Contains("same settings target", mismatch.Review);
            },
            missing =>
            {
                Assert.Equal(DraftReportStatus.NeedsReview, missing.Status);
                Assert.Null(missing.Draft);
                Assert.Contains("after snapshot could not be read", missing.Review);
            },
            empty =>
            {
                Assert.Equal(DraftReportStatus.NeedsSnapshots, empty.Status);
                Assert.Null(empty.Draft);
            });
        Assert.Equal(beforeBytes, File.ReadAllBytes(before));
        Assert.Equal(afterBytes, File.ReadAllBytes(after));
        Assert.Equal(4, Directory.GetFiles(_root).Length);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public void Refuses_duplicate_game_pairs_and_ambiguous_step_spellings()
    {
        var manifest = WriteManifest("""
            [
              {"gameId":"steam:1"},
              {"gameId":"STEAM:1"},
              {"gameId":"steam:2","before":"a.json","after":"b.json","step":"-1"}
            ]
            """);
        var report = SkippedRuleDraftReport.Build([Plan("1"), Plan("2")], manifest);
        Assert.Collection(report.Games,
            duplicate => Assert.Contains("More than one snapshot pair", duplicate.Review),
            step => Assert.Contains("Name the quality step", step.Review));
        Assert.All(report.Games, entry => Assert.Null(entry.Draft));
    }

    [Fact]
    public void Cli_report_without_manifest_needs_snapshots_and_refuses_save()
    {
        Assert.Equal(0, OptiGame.Cli.Cli.DraftSkippedRules(["--all-skipped"], () => [Plan("1")]));
        Assert.Throws<ArgumentException>(() =>
            OptiGame.Cli.Cli.DraftSkippedRules(["--all-skipped", "--save"], () => throw new InvalidOperationException()));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    GamePlan Plan(string id, PlanStatus status = PlanStatus.Skipped) => new()
    {
        Game = new GameInfo { Id = "steam:" + id, SteamAppId = id, Name = "Fixture " + id, Store = "Steam", InstallDir = _root },
        Status = status,
        SkipKind = SkipKind.NoHandler,
        Step = QualityStep.High,
    };

    string WriteManifest(string text)
    {
        var path = Path.Combine(_root, "pairs.json");
        File.WriteAllText(path, text);
        return path;
    }

    string CopyFixture(string name)
    {
        var path = Path.Combine(_root, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", name), path);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
