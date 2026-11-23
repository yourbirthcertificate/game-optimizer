namespace OptiGame.Core.Recommend;

public sealed record RecommendationEvidence(string Source, string Detail, string? Url = null);

public sealed record SettingRecommendation(
    string Id, string Name, string CurrentValue, string SuggestedValue, string Reason);

/// <summary>On-demand advice about individual settings. It does not change the optimizer's quality step or edits.</summary>
public sealed record SettingRecommendationReport(
    double? TargetFps, string? TargetSource, (int Width, int Height)? Resolution)
{
    public IReadOnlyList<SettingRecommendation> Settings { get; init; } = [];
    public IReadOnlyList<RecommendationEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public IReadOnlyList<string> Skipped { get; init; } = [];
    public string? FallbackReason { get; init; }
}
