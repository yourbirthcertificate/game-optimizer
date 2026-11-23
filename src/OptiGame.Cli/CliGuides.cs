using OptiGame.Core;
using OptiGame.Core.Optimize;
using OptiGame.Core.Recommend;

namespace OptiGame.Cli;

public static partial class Cli
{
    internal static int ListGuides(List<string> argv)
    {
        var games = Opt(argv, "--game");
        var options = Opt(argv, "--option");
        var scopes = Opt(argv, "--scope");
        NoMore(argv);
        if (games is not [var game] || string.IsNullOrWhiteSpace(game) || options.Count > 1 || scopes.Count > 1)
            throw new ArgumentException("Use: optigame-cli rule guides --game ID [--option KEY] [--scope TEXT]");

        var pack = GuidePack.ReadBuiltIn(out var problems);
        if (pack is null)
        {
            PrintGuideValidation("Embedded guide pack", null, problems);
            return 1;
        }
        var observations = pack.ForGame(game, options.FirstOrDefault(), scopes.FirstOrDefault());
        if (observations.Count == 0) Console.WriteLine("No guide observations match this game, setting and scope.");
        foreach (var setting in observations)
        {
            Console.WriteLine(setting.Option + (setting.HasDisagreement ? " (reviewed values differ)" : ""));
            foreach (var (advice, source) in setting.Observations)
            {
                Console.WriteLine("  " + (advice.Value ?? advice.Direction) + " | " + source.Name);
                Console.WriteLine("  " + source.ToEvidence().Detail + " " + source.Url);
            }
        }
        return 0;
    }

    static int ValidateGuidePacks(List<string> files)
    {
        if (files.Count == 0)
        {
            var pack = GuidePack.ReadBuiltIn(out var problems);
            return PrintGuideValidation("Embedded guide pack", pack, problems) ? 0 : 1;
        }

        var valid = true;
        foreach (var file in files)
        {
            var pack = GuidePack.TryParse(File.ReadAllText(file), out var problems);
            valid &= PrintGuideValidation(PathDisplay.Short(file), pack, problems);
        }
        return valid ? 0 : 1;
    }

    static bool PrintGuideValidation(string name, GuidePack? pack, List<RuleProblem> problems)
    {
        var status = pack is null ? "INVALID" : problems.Count > 0 ? "valid, with warnings" : "valid";
        Console.WriteLine(name + ": " + status);
        foreach (var problem in problems) Console.WriteLine("   " + problem);
        return pack is not null;
    }
}
