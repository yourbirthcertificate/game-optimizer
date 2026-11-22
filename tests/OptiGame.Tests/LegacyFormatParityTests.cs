using System.Globalization;
using System.Text;
using System.Text.Json;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Optimize;
using OptiGame.Core.Safety;

namespace OptiGame.Tests;

public sealed class LegacyFormatParityTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "optigame-format-parity-" + Guid.NewGuid().ToString("N"));

    public LegacyFormatParityTests() => Directory.CreateDirectory(_folder);

    [Theory]
    [InlineData("ini", "Video/Scale", "[Video]\r\nScale=1.5\r\n; keep\r\n", "[Video]\r\nScale=59.940000\r\n; keep\r\n", "ini|Video|Scale", "1.5", "59.940000")]
    [InlineData("kv", "scale", "\"scale\"\t\"1.5\" // keep\n", "\"scale\"\t\"59.94\" // keep\n", "kv|scale", "1.5", "59.94")]
    [InlineData("json", "video.scale", "{\"video\":{\"scale\":1.5,/* legacy comment */\"on\":false,}}", "{\"video\":{\"scale\":59.94,\"on\":false}}", "json|video.scale", "1.5", "59.94")]
    public void Rule_preview_keeps_the_existing_bytes_and_backup_locator(
        string format, string key, string input, string expected, string locator, string oldRaw, string newRaw)
    {
        var path = Path.Combine(_folder, "settings.fixture");
        var original = Encoding.UTF8.GetBytes(input);
        File.WriteAllBytes(path, original);
        var game = new GameInfo { Id = "fixture", Name = "Fixture", Store = "Test", InstallDir = _folder };
        var rule = new GameRule
        {
            Id = "fixture", Match = new RuleMatch { InstallDirs = [Path.GetFileName(_folder)] },
            Files = [new RuleFile
            {
                Format = format, Path = path, Set = new() { [key] = JsonSerializer.SerializeToElement(59.94) },
            }],
        };

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            var plan = Planner.Plan(game, new HardwareInfo(), QualityStep.High, [rule]);
            var edit = Assert.Single(plan.Files);
            var change = Assert.Single(edit.Changes);
            Assert.Equal(format, edit.Format);
            Assert.Equal(locator, change.Locator);
            Assert.Equal(oldRaw, change.OldRaw);
            Assert.Equal(newRaw, change.NewRaw);
            Assert.Equal(Encoding.UTF8.GetBytes(expected), edit.Updated);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("ini", "ini|Video|Added", null, "3",
        "[Video]\r\nAdded=3\r\nOther=7\r\n", "[Video]\r\nOther=7\r\n")]
    [InlineData("kv", "kv|mode", "1", "2",
        "\"VideoConfig\"\n{\n \"mode\" \"2\"\n", "\"VideoConfig\"\n{\n \"mode\" \"1\"\n")]
    [InlineData("json", "json|video.optional", null, "2",
        "{\"video\":{\"optional\":2,\"other\":7}}", "{\"video\":{\"optional\":null,\"other\":7}}")]
    public void Key_undo_reads_legacy_locators_and_preserves_their_restore_semantics(
        string format, string locator, string? original, string written, string current, string expected)
    {
        var (bytes, keys) = Applier.UndoKeys(format, Encoding.UTF8.GetBytes(current),
            [new() { Setting = "Fixture setting", Locator = locator, OldRaw = original, NewRaw = written }]);

        Assert.Equal(KeyState.Revert, Assert.Single(keys).State);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), bytes);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
