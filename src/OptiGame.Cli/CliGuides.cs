using OptiGame.Core;
using OptiGame.Core.Optimize;
using OptiGame.Core.Recommend;

namespace OptiGame.Cli;

public static partial class Cli
{
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
