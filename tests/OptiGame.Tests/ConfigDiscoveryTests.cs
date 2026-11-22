using System.Globalization;
using OptiGame.Core;
using OptiGame.Core.Library;
using OptiGame.Core.Library.ConfigDiscovery;
using OptiGame.Core.Optimize;

namespace OptiGame.Tests;

public sealed class ConfigDiscoveryTests : IDisposable
{
    readonly string _temp = Path.Combine(Path.GetTempPath(), "optigame-config-" + Guid.NewGuid().ToString("N"));
    readonly ConfigRoots _roots;
    readonly string _install;

    public ConfigDiscoveryTests()
    {
        _roots = new(At("Local"), At("LocalLow"), At("Roaming"), At("Documents"), At("Saved Games"),
            At("ProgramData"), At("Profile"), At("Local", "GameOpt"));
        _install = Directory.CreateDirectory(At("Install", "Standalone")).FullName;
    }

    [Fact]
    public void Merges_sources_for_the_same_path_and_confirms_only_the_engine_settings()
    {
        var game = Game("Cyberpunk 2077", engine: GameEngine.RedEngine);
        var folder = Path.Combine(_roots.Local, "CD Projekt Red", game.Name);
        var enginePath = Path.Combine(folder, "UserSettings.json");
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "cyberpunk-UserSettings.json"));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(enginePath, bytes);
        var decoy = Write(Path.Combine(folder, "Settings.json"), """{"account": 123}""");

        var result = Finder().Find(game);

        Assert.Collection(result.Candidates,
            entry =>
            {
                Assert.Equal(enginePath, entry.Path);
                Assert.Equal("red", entry.FormatHint);
                Assert.Equal(ConfigConfidence.Confirmed, entry.Confidence);
                Assert.Contains(entry.Evidence, item => item.Source == "Engine");
                Assert.Contains(entry.Evidence, item => item.Source == "Local folders");
            },
            entry =>
            {
                Assert.Equal(decoy, entry.Path);
                Assert.Equal(ConfigConfidence.Present, entry.Confidence);
            });
        Assert.Equal(bytes, File.ReadAllBytes(enginePath));
    }

    [Fact]
    public void Finds_multiple_exact_folder_matches_including_publisher_and_app_id_children()
    {
        var game = Game("Short-Game", "11123");
        var low = Write(At("LocalLow", "Studio", "Short Game", "UserSettings.ini"), "[Display]\nWidth=1280\n");
        var docs = Write(At("Documents", "My Games", "ShortGame", "options.xml"), "<settings/>");
        var account = Write(At("ProgramData", "Short Game", "11123", "Profile", "Config", "graphics.json"), "{}");
        var install = Write(Path.Combine(_install, "DefaultSettings.json"), "{}");
        var near = Write(At("Local", "Short Game 2", "settings.ini"), "");
        var backup = Write(At("Local", "GameOpt", "Short Game", "settings.json"), "{}");

        var result = Finder().Find(game);

        Assert.Equal(4, result.Candidates.Count);
        Assert.DoesNotContain(result.Candidates, item => item.Path == near || item.Path == backup);
        Assert.All(result.Candidates, item => Assert.Equal(ConfigConfidence.Present, item.Confidence));
        Assert.Contains(result.Candidates, item => item.Path == low);
        Assert.Contains(result.Candidates, item => item.Path == docs);
        Assert.Contains(result.Candidates, item => item.Path == account &&
            item.Evidence.Any(e => e.Observation.Contains("exact Steam app ID", StringComparison.Ordinal)));
        Assert.Contains(result.Candidates, item => item.Path == install &&
            item.Evidence.Any(e => e.Observation.Contains("shipped default", StringComparison.Ordinal)));
        Assert.Contains(result.Diagnostics, item => item.Code == "multiple-matches");
    }

    [Fact]
    public void Finds_an_exact_executable_stem_using_directory_entries_only()
    {
        Write(Path.Combine(_install, "bin", "x64", "ModularGame.exe"), "");
        Write(Path.Combine(_install, "bin", "x64", "CrashHelper.exe"), "");
        var hint = Write(At("Local", "ModularGame", "video.ini"), "");
        Write(At("Local", "Modular-Game", "video.ini"), "");
        Write(At("Local", "CrashHelper", "video.ini"), "");

        var result = Finder().Find(Game("Unrelated display title"));

        var entry = Assert.Single(result.Candidates);
        Assert.Equal(hint, entry.Path);
        Assert.Equal(ConfigConfidence.Present, entry.Confidence);
        Assert.Contains(entry.Evidence, item => item.Source == "Executable name");
    }

    [Fact]
    public void Gets_only_registry_value_names_for_exact_hkcu_keys()
    {
        var game = Game("Unity fixture", engine: GameEngine.Unity, detail: @"Studio\Product");
        var rule = Rule(
            new RuleFile { Format = "registry", Path = "HKEY_CURRENT_USER/SOFTWARE/studio/product" },
            new RuleFile { Format = "registry", Path = @"HKLM\Software\Studio\Product" });
        var queries = new List<string>();
        var finder = new ConfigFinder(_roots, path =>
        {
            queries.Add(path);
            return ["FullScreen_h123", "Quality_h234"];
        }, pack: []);

        var result = finder.Find(game, new(rule));

        Assert.Equal(new[] { @"HKCU\Software\Studio\Product" }, queries);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(ConfigCandidateKind.Registry, candidate.Kind);
        Assert.Equal(ConfigConfidence.Present, candidate.Confidence);
        Assert.Contains(candidate.Evidence, item => item.Observation.Contains("FullScreen_h123", StringComparison.Ordinal));
        Assert.Contains(candidate.Evidence, item => item.Source == "Matched rule");
        Assert.Contains(result.Diagnostics, item => item.Code == "blocked-path" && item.Path!.StartsWith("HKLM", StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_missing_candidates_and_keeps_invalid_or_ambiguous_files_unconfirmed()
    {
        var path = Write(Path.Combine(_install, "video.json"), """{"graphics":{"quality":1,"quality":2}}""");
        var matched = new RuleFile { Format = "json", Path = "{install}/video.json" };
        matched.Set.Add("graphics.quality", System.Text.Json.JsonSerializer.SerializeToElement(4));
        var rule = Rule(matched, new RuleFile { Format = "ini", Path = "{install}/missing.ini" });
        var finder = Finder();
        var game = Game("Test title");

        var first = finder.Find(game, new(rule));
        Assert.Contains(first.Candidates, item => item.Path == path && item.Confidence == ConfigConfidence.Present);
        Assert.Contains(first.Diagnostics, item => item.Path == path && item.Code == "unrecognized-content");
        Assert.Contains(first.Candidates, item => item.Path.EndsWith("missing.ini", StringComparison.Ordinal) &&
            item.Confidence == ConfigConfidence.Lead);
        Assert.Contains(first.Diagnostics, item => item.Code == "missing");

        Write(path, """{"unrelated": [1,2]}""");
        Assert.DoesNotContain(finder.Find(game, new(rule)).Candidates, item => item.Confidence == ConfigConfidence.Confirmed);

        Write(path, """{"graphics":{"quality":1}}""");
        Assert.Equal(path, Assert.Single(finder.Find(game, new(rule)).Candidates,
            item => item.Confidence == ConfigConfidence.Confirmed).Path);
    }

    [Fact]
    public void Finds_bundled_config_by_exact_steam_id_without_promoting_an_unsupported_format()
    {
        var path = Write(At("Roaming", "Arrowhead", "Helldivers2", "user_settings.config"), "adapter_index = 0");
        var finder = new ConfigFinder(_roots, _ => null);
        var game = Game("Alternate display title", "553850");
        var result = finder.Find(game);

        var entry = Assert.Single(result.Candidates);
        Assert.Equal(path, entry.Path);
        Assert.Equal(ConfigConfidence.Present, entry.Confidence);
        Assert.Contains(entry.Evidence, item => item.Url?.Host == "arrowhead.zendesk.com" &&
            item.Reviewed == new DateOnly(2026, 11, 22));
        Assert.Contains(result.Diagnostics, item => item.Code == "unverified-format");
        Assert.Empty(finder.Find(Game("Alternate display title", "553851")).Candidates);
        Assert.Empty(finder.Find(Game("Alternate display title", store: "GOG"), new(LinkedSteamAppId: "553850")).Candidates);
    }

    [Fact]
    public void Skips_unknown_placeholders_traversal_and_network_locations()
    {
        var finder = Finder([
            Pack("roaming", "../Profile/settings.ini"),
            Pack("roaming", "<unknown>/settings.ini"),
            Pack("roaming", "settings.ini:alternate"),
            Pack("roaming", "NUL.ini"),
            Pack("roaming", @"\\example.test\share\settings.ini"),
            Pack("unknown-root", "settings.ini"),
        ]);
        var rule = Rule(new RuleFile { Path = @"\\example.test\share\settings.ini", Format = "ini" });

        var result = finder.Find(Game("Fixture", "123"), new(rule));

        Assert.Empty(result.Candidates);
        Assert.Equal(6, result.Diagnostics.Count(item => item.Code == "blocked-template"));
        Assert.Contains(result.Diagnostics, item => item.Code == "blocked-path" && item.Source == "Matched rule");
    }

    [Fact]
    public void Expands_only_an_unambiguous_steam_account_id()
    {
        const string account = "76561198011111111";
        var steam = At("Steam");
        var idFile = Path.Combine(steam, "config", "loginusers.vdf");
        Write(idFile, "\"users\" { \"" + account + "\" {} }");
        var path = Write(At("Saved Games", "Studio", "Game", account, "options.xml"), "<settings/>");
        var finder = new ConfigFinder(_roots, _ => null,
            [Pack("savedGames", "Studio/Game/<storeUserId>/options.xml") with { Store = "Steam", FormatHint = "xml" }],
            () => steam);
        var game = Game("Fixture", "123");

        Assert.Equal(path, Assert.Single(finder.Find(game).Candidates).Path);
        Write(idFile, "\"users\" { \"" + account + "\" {} \"76561198022222222\" {} }");
        var ambiguous = finder.Find(game);
        Assert.Empty(ambiguous.Candidates);
        Assert.Contains(ambiguous.Diagnostics, item => item.Code == "store-user-unresolved");
    }

    [Fact]
    public void Summary_is_stable_under_dutch_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            Assert.Equal("5,0", 5.0.ToString("0.0", CultureInfo.CurrentCulture));
            Assert.Equal("57 of 82 games skipped. Confirmed: 5. Present only: 12. Reported only: 10. No candidates: 30.",
                OptiGame.Cli.Cli.ConfigSummary(57, 82, 5, 12, 10));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    ConfigFinder Finder(IReadOnlyList<ReviewedLocation>? pack = null) =>
        new(_roots, _ => throw new InvalidOperationException("A test must supply registry names."), pack ?? []);

    static ReviewedLocation Pack(string root, string relativePath) =>
        new("123", null, root, relativePath, "ini", "Recorded source", new Uri("https://example.test/config"),
            new DateOnly(2026, 11, 22));

    GameInfo Game(string name, string? appId = null, GameEngine engine = GameEngine.Unknown,
        string? detail = null, string store = "Steam") =>
        new() { Name = name, Id = "test:" + name, InstallDir = _install, Store = store, SteamAppId = appId,
            Engine = engine, EngineDetail = detail };

    GameRule Rule(params RuleFile[] files) =>
        new() { Id = "fixture", Match = new RuleMatch { InstallDirs = [Path.GetFileName(_install)] }, Files = [.. files] };

    string At(params string[] segments) => Path.Combine(_temp, Path.Combine(segments));

    static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);
}
