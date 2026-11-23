using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OptiGame.Core.Recommend;

namespace OptiGame.Core.Bench;

/// <summary>The conditions the author recorded when taking a capture, independent of preferences at import.</summary>
public sealed record BenchmarkCaptureContext(string PairId, string GameBuild, string Scenario, string Hardware,
    int Width, int Height, string FrameCap, string RenderMode, IReadOnlyDictionary<string, string> Settings)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static BenchmarkCaptureContext Parse(string json)
    {
        BenchmarkCaptureContext? context;
        try { context = JsonSerializer.Deserialize<BenchmarkCaptureContext>(json, Json); }
        catch (JsonException ex) { throw new FormatException("Invalid benchmark capture context. " + ex.Message, ex); }
        if (context is null || !context.IsComplete())
            throw new FormatException("Capture context requires pairId, gameBuild, scenario, hardware, width, height, frameCap, renderMode, and observed settings.");
        return context;
    }

    internal bool IsComplete() =>
        !new[] { PairId, GameBuild, Scenario, Hardware, FrameCap, RenderMode }.Any(string.IsNullOrWhiteSpace) &&
        Width > 0 && Height > 0 &&
        (FrameCap == "uncapped" || double.TryParse(FrameCap, NumberStyles.Float, CultureInfo.InvariantCulture, out var cap) &&
            double.IsFinite(cap) && cap > 0) &&
        Settings is { Count: > 0 } &&
        Settings.All(setting => !string.IsNullOrWhiteSpace(setting.Key) && !string.IsNullOrWhiteSpace(setting.Value));
}

public sealed record BenchmarkSettingComparison(string OptionId, string BeforeValue, string AfterValue,
    BenchmarkCaptureContext Context, BenchResult Before, BenchResult After)
{
    public RecommendationEvidence ToEvidence() => new("Local benchmark pair", FormattableString.Invariant(
        $"{OptionId}: {BeforeValue} → {AfterValue} at {Context.Width}x{Context.Height} in {Context.Scenario} (build {Context.GameBuild}, cap {Context.FrameCap}, {Context.RenderMode}). Average FPS {Before.Metrics.AverageFps:0.#} → {After.Metrics.AverageFps:0.#}, 1% low {Before.Metrics.OnePercentLowFps:0.#} → {After.Metrics.OnePercentLowFps:0.#}."));
}

public static class BenchmarkSettingEvidence
{
    /// <summary>Matches a user-declared before/after pair with one changed, recorded setting.</summary>
    public static BenchmarkSettingComparison? Compare(BenchResult before, BenchResult after)
    {
        if (before.GameId != after.GameId || before.Id == after.Id ||
            !before.Label.Equals("before", StringComparison.OrdinalIgnoreCase) ||
            !after.Label.Equals("after", StringComparison.OrdinalIgnoreCase) ||
            before.CaptureContext is not { } a || after.CaptureContext is not { } b ||
            !a.IsComplete() || !b.IsComplete() || a.PairId != b.PairId ||
            a.GameBuild != b.GameBuild || a.Scenario != b.Scenario || a.Hardware != b.Hardware ||
            a.Width != b.Width || a.Height != b.Height || a.FrameCap != b.FrameCap || a.RenderMode != b.RenderMode ||
            a.Settings.Count != b.Settings.Count || a.Settings.Keys.Any(key => !b.Settings.ContainsKey(key)) ||
            !HasMeasurement(before) || !HasMeasurement(after))
            return null;

        var shorter = Math.Min(before.Metrics.DurationSeconds, after.Metrics.DurationSeconds);
        var longer = Math.Max(before.Metrics.DurationSeconds, after.Metrics.DurationSeconds);
        if (longer / shorter > 1.25) return null;

        var changed = a.Settings.Where(entry => entry.Value != b.Settings[entry.Key]).ToArray();
        return changed is [var setting]
            ? new(setting.Key, setting.Value, b.Settings[setting.Key], b, before, after)
            : null;
    }

    internal static SettingRecommendationReport Include(SettingRecommendationReport report,
        string hardware, IEnumerable<BenchmarkSettingComparison> comparisons)
    {
        var evidence = comparisons.Where(comparison =>
            comparison.Context.Hardware == hardware &&
            report.Resolution == (comparison.Context.Width, comparison.Context.Height) &&
            report.Settings.Any(setting => setting.Id == comparison.OptionId &&
                setting.CurrentValue == comparison.BeforeValue && setting.SuggestedValue == comparison.AfterValue))
            .Select(comparison => comparison.ToEvidence()).Distinct().ToArray();
        return evidence.Length == 0 ? report : report with { Evidence = [.. report.Evidence, .. evidence] };
    }

    static bool HasMeasurement(BenchResult run) =>
        double.IsFinite(run.Metrics.DurationSeconds) && run.Metrics.DurationSeconds > 0 &&
        double.IsFinite(run.Metrics.AverageFps) && run.Metrics.AverageFps > 0 &&
        double.IsFinite(run.Metrics.OnePercentLowFps) && run.Metrics.OnePercentLowFps > 0;
}
