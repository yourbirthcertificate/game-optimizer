using System.Globalization;
using OptiGame.Core;
using OptiGame.Core.Library;
using OptiGame.Core.Library.ConfigDiscovery;
using OptiGame.Core.Optimize;

namespace OptiGame.Cli;

public static partial class Cli
{
    const string ConfigUsage = "Use: optigame-cli config find --game NAME | config find --all-skipped";

    static int Config(List<string> argv)
    {
        if (argv.Count == 0 || argv[0] != "find") throw new ArgumentException(ConfigUsage);
        argv.RemoveAt(0);
        var allSkipped = Flag(argv, "--all-skipped");
        var names = Opt(argv, "--game");
        if (allSkipped ? names.Count != 0 : names.Count != 1 || string.IsNullOrWhiteSpace(names[0]))
            throw new ArgumentException(ConfigUsage);
        NoMore(argv);

        var session = OptimizerSession.LoadForInspection();
        var finder = new ConfigFinder();
        if (!allSkipped)
        {
            var game = ConfigGame(session, names[0]);
            PrintConfig(game, Discover(finder, session, game));
            return 0;
        }

        var plans = SkippedPlans(session);
        var confirmed = 0;
        var present = 0;
        var reported = 0;
        foreach (var plan in plans)
        {
            var result = Discover(finder, session, plan.Game);
            PrintConfig(plan.Game, result, plan.SkipText);
            if (result.Candidates.Any(candidate => candidate.Confidence == ConfigConfidence.Confirmed)) confirmed++;
            else if (result.Candidates.Any(candidate => candidate.Confidence == ConfigConfidence.Present)) present++;
            else if (result.Candidates.Count > 0) reported++;
        }
        Console.WriteLine(ConfigSummary(plans.Count, session.Scan.Games.Count, confirmed, present, reported));
        return 0;
    }

    internal static string ConfigSummary(int skipped, int installed, int confirmed, int present, int reported) =>
        FormattableString.Invariant(
            $"{skipped} of {installed} games skipped. Confirmed: {confirmed}. Present only: {present}. Reported only: {reported}. No candidates: {skipped - confirmed - present - reported}.");

    static ConfigDiscoveryResult Discover(ConfigFinder finder, OptimizerSession session, GameInfo game) =>
        finder.Find(game, new(session.Rules.FirstOrDefault(rule => rule.Matches(game)), session.AppIdFor(game)));

    static GameInfo ConfigGame(OptimizerSession session, string name)
    {
        var exactId = session.Scan.Games.FirstOrDefault(game =>
            game.Id.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            ConfigPath(game.Id).Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exactId is not null) return exactId;

        var matches = session.Scan.Games.Where(game => game.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) matches = session.Matching(name);
        return matches switch
        {
            [var single] => single,
            [] => throw new ArgumentException("No installed game matches '" + CleanConfigText(name) + "'."),
            _ => throw new ArgumentException("More than one installed game matches. Use the stable game ID: " +
                string.Join(", ", matches.Select(game => ConfigPath(game.Id)))),
        };
    }

    static void PrintConfig(GameInfo game, ConfigDiscoveryResult result, string? skip = null)
    {
        Console.WriteLine(CleanConfigText(game.Name) + (skip is null ? "" : " | " + CleanConfigText(skip)));
        if (result.Candidates.Count == 0) Console.WriteLine("  No candidate locations.");
        else Console.WriteLine("  Confidence | Kind | Format hint | Location");

        foreach (var candidate in result.Candidates)
        {
            Console.WriteLine($"  {candidate.Confidence} | {candidate.Kind} | {candidate.FormatHint ?? "unknown"} | {ConfigPath(candidate.Path)}");
            foreach (var evidence in candidate.Evidence)
            {
                var source = CleanConfigText(evidence.Source);
                var observation = CleanConfigText(evidence.Observation);
                var reviewed = evidence.Reviewed?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                Console.WriteLine("    " + source + ": " + observation +
                    (reviewed is null ? "" : " Reviewed " + reviewed + ".") +
                    (evidence.Url is null ? "" : " " + evidence.Url.AbsoluteUri));
            }
        }
        foreach (var diagnostic in result.Diagnostics)
        {
            var path = diagnostic.Path is null ? "" : " (" + ConfigPath(diagnostic.Path) + ")";
            Console.WriteLine("  " + diagnostic.Source + ": " + diagnostic.Message + path);
        }
    }

    static string ConfigPath(string path) =>
        CleanConfigText(path.StartsWith("manual:", StringComparison.OrdinalIgnoreCase)
            ? "manual:" + PathDisplay.Short(path["manual:".Length..]) : PathDisplay.Short(path));

    static string CleanConfigText(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
