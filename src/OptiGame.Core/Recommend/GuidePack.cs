using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OptiGame.Core.Optimize;

namespace OptiGame.Core.Recommend;

public sealed record GuideSource(string Id, string Name, string Url, DateOnly Retrieved, string AppliesTo,
    string Summary, string Confidence, DateOnly? Verified = null)
{
    internal RecommendationEvidence ToEvidence() => new(Name, FormattableString.Invariant(
        $"{Summary} Source scope: {AppliesTo}. Citation reviewed {Verified:yyyy-MM-dd} (source confidence: {Confidence})."), Url);
}

public sealed record GuideAdvice(string Game, string Option, string Source, string? Value = null, string? Direction = null);

/// <summary>Read-only published evidence. Guide entries are never loaded as optimizer rules.</summary>
public sealed partial record GuidePack(int SchemaVersion, IReadOnlyList<GuideSource> Sources, IReadOnlyList<GuideAdvice> Advice)
{
    const string Resource = "OptiGame.Core.guides.reviewed.v1.json";
    static readonly Lazy<GuidePack?> BuiltIn = new(() => ReadBuiltIn(out _));
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };
    static readonly HashSet<string> SourceHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "support.cdprojektred.com", "www.pcgamer.com", "www.tomshardware.com", "www.digitaltrends.com", "steamcommunity.com",
    };

    internal static GuidePack? Reviewed => BuiltIn.Value;

    internal GuideSource? FindReviewedSource(string id) =>
        Sources.FirstOrDefault(source => source.Id == id && source.Verified is not null);

    internal string? FindReviewedValue(string game, string option, string source) => FindReviewedSource(source) is null
        ? null
        : Advice.FirstOrDefault(entry => entry.Game == game && entry.Option == option && entry.Source == source)?.Value;

    public static GuidePack? ReadBuiltIn(out List<RuleProblem> problems)
    {
        using var stream = typeof(GuidePack).Assembly.GetManifestResourceStream(Resource);
        if (stream is null)
        {
            problems = [RuleProblem.Error("pack", "The embedded guide pack is missing.")];
            return null;
        }
        using var reader = new StreamReader(stream);
        return TryParse(reader.ReadToEnd(), out problems);
    }

    public static GuidePack? TryParse(string json, out List<RuleProblem> problems)
    {
        GuidePack? pack;
        try { pack = JsonSerializer.Deserialize<GuidePack>(json, JsonOptions); }
        catch (JsonException ex)
        {
            problems = [RuleProblem.Error(ex.Path ?? "pack", "Invalid guide pack JSON or field type.")];
            return null;
        }

        problems = [];
        if (pack is null)
        {
            problems.Add(RuleProblem.Error("pack", "Expected a guide pack object."));
            return null;
        }

        pack.Validate(problems);
        return problems.Any(problem => problem.Severity == RuleProblemSeverity.Error) ? null : pack;
    }

    void Validate(List<RuleProblem> problems)
    {
        if (SchemaVersion != 1)
            problems.Add(RuleProblem.Error("schemaVersion", "Expected guide pack version 1."));
        if (Sources.Count == 0)
            problems.Add(RuleProblem.Error("sources", "At least one source is required."));
        if (Advice.Count == 0)
            problems.Add(RuleProblem.Error("advice", "At least one advice entry is required."));

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Sources.Count; i++)
        {
            var at = FormattableString.Invariant($"sources[{i}]");
            if (Sources[i] is not { } source)
            {
                problems.Add(RuleProblem.Error(at, "Expected a source object."));
                continue;
            }

            if (!IdPattern().IsMatch(source.Id) || !sourceIds.Add(source.Id))
                problems.Add(RuleProblem.Error(at + ".id", "Expected a unique lowercase id using letters, digits and dashes."));
            if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Summary) ||
                string.IsNullOrWhiteSpace(source.AppliesTo))
                problems.Add(RuleProblem.Error(at, "Source name, summary and game version scope are required."));
            if (!IsSupportedUrl(source.Url))
                problems.Add(RuleProblem.Error(at + ".url", "Expected an HTTPS citation on an allowlisted host."));
            if (source.Confidence is not ("high" or "medium" or "low"))
                problems.Add(RuleProblem.Error(at + ".confidence", "Expected high, medium or low source confidence."));
            if (source.Retrieved == default || source.Verified == default(DateOnly) || source.Verified < source.Retrieved)
                problems.Add(RuleProblem.Error(at + ".verified", "Use a real retrieval date and a review date on or after retrieval."));
            else if (source.Verified is null)
                problems.Add(RuleProblem.Warning(at + ".verified", "Unreviewed citation. It is ineligible for built-in recommendations."));
        }

        var adviceKeys = new HashSet<(string Game, string Option, string Source)>();
        for (var i = 0; i < Advice.Count; i++)
        {
            var at = FormattableString.Invariant($"advice[{i}]");
            if (Advice[i] is not { } entry)
            {
                problems.Add(RuleProblem.Error(at, "Expected an advice object."));
                continue;
            }

            if (!IdPattern().IsMatch(entry.Game) || string.IsNullOrWhiteSpace(entry.Option))
                problems.Add(RuleProblem.Error(at, "A game id and a setting key are required."));
            if (!sourceIds.Contains(entry.Source))
                problems.Add(RuleProblem.Error(at + ".source", "The advice refers to an unknown source."));
            if (!adviceKeys.Add((entry.Game, entry.Option, entry.Source)))
                problems.Add(RuleProblem.Error(at, "Duplicate game, setting and source."));
            if (entry.Direction is null ? string.IsNullOrWhiteSpace(entry.Value) :
                entry.Value is not null || entry.Direction is not ("lower" or "raise" or "keep"))
                problems.Add(RuleProblem.Error(at, "Give one setting value or a direction of lower, raise or keep."));
        }
    }

    static bool IsSupportedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && SourceHosts.Contains(uri.IdnHost);

    [GeneratedRegex(@"\A[a-z0-9]+(?:-[a-z0-9]+)*\z")]
    private static partial Regex IdPattern();
}
