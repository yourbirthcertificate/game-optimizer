using System.Text.Json;
using System.Text.Json.Serialization;
using OptiGame.Core.Optimize;
using OptiGame.Core.Rules;

namespace OptiGame.Cli;

public static partial class Cli
{
    static readonly JsonSerializerOptions DraftReportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static int DraftSkippedRules(List<string> argv, Func<IReadOnlyList<GamePlan>> findPlans)
    {
        var allSkipped = Flag(argv, "--all-skipped");
        var manifests = Opt(argv, "--manifest");
        NoMore(argv);
        if (!allSkipped || manifests.Count > 1 || manifests.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Use: optigame-cli rule draft --all-skipped [--manifest FILE]");

        var report = SkippedRuleDraftReport.Build(findPlans(), manifests.FirstOrDefault());
        Console.WriteLine(JsonSerializer.Serialize(report, DraftReportJson));
        return 0;
    }
}
