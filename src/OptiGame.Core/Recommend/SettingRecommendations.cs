using OptiGame.Core.Optimize;

namespace OptiGame.Core.Recommend;

public enum RecommendationPriority { Balanced, Look, Speed, Quiet }

public sealed record RecommendationEvidence(string Source, string Detail, string? Url = null);

public sealed record SettingRecommendation(
    string Id, string Name, string CurrentValue, string SuggestedValue, string Reason);

/// <summary>On-demand advice about individual settings. It does not change the optimizer's quality step or edits.</summary>
public sealed record SettingRecommendationReport(
    double? TargetFps, string? TargetSource, (int Width, int Height)? Resolution)
{
    public RecommendationPriority Priority { get; init; }
    public IReadOnlyList<SettingRecommendation> Settings { get; init; } = [];
    public IReadOnlyList<RecommendationEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public IReadOnlyList<string> Skipped { get; init; } = [];
    public string? FallbackReason { get; init; }

    internal static (double? Fps, string? Source) ResolveTarget(OptimizeRequest request)
    {
        if (request.RecommendationTargetFps is double explicitTarget)
            return double.IsFinite(explicitTarget) && explicitTarget > 0 ? (explicitTarget, "chosen target") : (null, null);
        if (request.FpsMode == FpsMode.Custom && request.FpsCap is double cap && double.IsFinite(cap) && cap > 0)
            return (cap, "custom frame cap");
        if (string.IsNullOrEmpty(request.Display.DeviceName) || !double.IsFinite(request.Display.CurrentHz) ||
            request.Display.CurrentHz <= 3 || request.Display.CurrentHz >= int.MaxValue)
            return (null, null);
        return (request.Display.AutoFpsCap, "active display auto cap");
    }
}
