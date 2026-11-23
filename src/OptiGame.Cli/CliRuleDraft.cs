using OptiGame.Core;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Rules;

namespace OptiGame.Cli;

public static partial class Cli
{
    internal static int DraftRule(List<string> argv, Func<string, GameInfo> findGame)
    {
        var game = Opt(argv, "--game");
        var before = Opt(argv, "--before");
        var after = Opt(argv, "--after");
        var steps = Opt(argv, "--step");
        var keys = Opt(argv, "--key");
        var save = Flag(argv, "--save");
        NoMore(argv);
        if (game is not [var name] || before is not [var beforePath] || after is not [var afterPath] ||
            steps is not [var stepText] || stepText.StartsWith('-') || !QualitySteps.TryParse(stepText, out var step))
            throw new ArgumentException("Use: optigame-cli rule draft --game NAME --before FILE --after FILE --step STEP [--key KEY]... [--save]");

        var beforeSnapshot = SnapshotRuleDraft.ReadSnapshot(File.ReadAllText(beforePath));
        var afterSnapshot = SnapshotRuleDraft.ReadSnapshot(File.ReadAllText(afterPath));
        var draft = SnapshotRuleDraft.Build(findGame(name), beforeSnapshot, afterSnapshot, step, keys);
        Console.WriteLine(draft.Json);
        foreach (var reason in draft.Skipped) Console.WriteLine("Skipped: " + reason);
        foreach (var problem in draft.Problems) Console.WriteLine(problem);
        if (save)
            Console.WriteLine("Saved inactive draft to " + PathDisplay.Short(SnapshotRuleDraft.Save(draft)) + ".");
        return 0;
    }
}
