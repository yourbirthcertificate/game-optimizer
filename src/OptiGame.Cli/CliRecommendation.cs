using System.Globalization;
using OptiGame.Core.Recommend;

namespace OptiGame.Cli;

public static partial class Cli
{
    internal static double? ReadRecommendationTarget(List<string> argv)
    {
        var targets = Opt(argv, "--target-fps");
        if (targets.Count == 0) return null;
        if (targets is [var first] &&
            double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) &&
            double.IsFinite(fps) && fps > 0) return fps;
        throw new ArgumentException("--target-fps needs one positive, finite number, using a decimal point if needed.");
    }

    internal static IEnumerable<string> SettingAdviceLines(SettingRecommendationReport? report)
    {
        if (report is null) yield break;

        yield return report.TargetFps is { } fps
            ? FormattableString.Invariant($"Per-setting advice (advisory), target {fps:0.##} FPS ({report.TargetSource})") +
                (report.Resolution is { } r ? FormattableString.Invariant($" at {r.Width}x{r.Height}") : "") + "."
            : "Per-setting advice (advisory).";
        if (report.FallbackReason is { } reason) yield return "Per-setting advice unavailable: " + reason;
        foreach (var setting in report.Settings)
            yield return $"{setting.Name}: {setting.CurrentValue} → {setting.SuggestedValue}. {setting.Reason}";
        foreach (var skip in report.Skipped) yield return "Kept: " + skip;
        foreach (var evidence in report.Evidence)
            yield return $"Evidence ({evidence.Source}): {evidence.Detail}" + (evidence.Url is null ? "" : " " + evidence.Url);
        foreach (var assumption in report.Assumptions) yield return "Assumption: " + assumption;
    }
}
