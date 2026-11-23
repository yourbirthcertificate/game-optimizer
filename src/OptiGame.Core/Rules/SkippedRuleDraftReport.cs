using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library.ConfigDiscovery;
using OptiGame.Core.Optimize;

namespace OptiGame.Core.Rules;

public enum DraftReportStatus { NeedsSnapshots, NeedsReview, DraftNeedsConfirmation }

public sealed record SkippedRuleDraftEntry(
    string GameId, string Name, SkipKind SkipKind, string SkipReason, string? SettingsFolder, DraftReportStatus Status,
    string Review, JsonElement? Draft = null, IReadOnlyList<string>? Notes = null);

public sealed record SkippedRuleDraftReport(IReadOnlyList<SkippedRuleDraftEntry> Games, IReadOnlyList<string> UnusedGameIds)
{
    static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>Reports inactive draft previews using only explicitly associated snapshot pairs.</summary>
    public static SkippedRuleDraftReport Build(IEnumerable<GamePlan> plans, string? manifestPath = null)
    {
        var directory = manifestPath is null ? "" : Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var pairs = manifestPath is null ? [] : ReadManifest(manifestPath);
        var byGame = pairs.ToLookup(pair => DisplayId(pair.GameId), StringComparer.OrdinalIgnoreCase);
        var games = plans.Where(plan => plan.Status == PlanStatus.Skipped)
            .OrderBy(plan => plan.Game.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Game.Id, StringComparer.OrdinalIgnoreCase)
            .Select(plan => BuildEntry(plan, byGame[DisplayId(plan.Game.Id)], directory))
            .ToArray();
        var unused = byGame.Select(group => group.Key)
            .Except(games.Select(game => game.GameId), StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(games, unused);
    }

    static SkippedRuleDraftEntry BuildEntry(GamePlan plan, IEnumerable<SnapshotPair> pairs, string directory)
    {
        var row = new SkippedRuleDraftEntry(DisplayId(plan.Game.Id), plan.Game.Name, plan.SkipKind, plan.SkipText,
            plan.SettingsFolder is null ? null : PathDisplay.Short(plan.SettingsFolder), DraftReportStatus.NeedsSnapshots,
            "Supply a before and after snapshot pair for the same setting target.");
        if (pairs.ToArray() is not [var pair])
            return pairs.Any() ? NeedsReview("More than one snapshot pair was supplied for this game.") : row;

        if (string.IsNullOrWhiteSpace(pair.Before) || string.IsNullOrWhiteSpace(pair.After))
            return row with { Review = "The manifest entry needs both snapshot file paths." };
        if (!TryStep(pair.Step, out var step))
            return NeedsReview("Name the quality step observed in the after snapshot.");
        if (pair.Keys?.Any(string.IsNullOrWhiteSpace) == true)
            return NeedsReview("Each selected key must be a non-empty setting key.");

        try
        {
            var before = SnapshotRuleDraft.ReadSnapshot(ReadLocal(pair.Before, directory, "before snapshot"));
            var after = SnapshotRuleDraft.ReadSnapshot(ReadLocal(pair.After, directory, "after snapshot"));
            var draft = SnapshotRuleDraft.Build(plan.Game, before, after, step, pair.Keys ?? []);
            return row with
            {
                Status = DraftReportStatus.DraftNeedsConfirmation,
                Review = "Confirm the selected step and observed settings in game before activating a rule.",
                Draft = JsonSerializer.Deserialize<JsonElement>(draft.Json),
                Notes = [.. draft.Skipped, .. draft.Problems.Select(problem => problem.ToString())],
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return NeedsReview(ex.Message);
        }

        SkippedRuleDraftEntry NeedsReview(string reason) =>
            row with { Status = DraftReportStatus.NeedsReview, Review = reason };
    }

    static SnapshotPair[] ReadManifest(string path)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<SnapshotPair[]>(ReadLocal(path, "", "manifest"), ManifestOptions);
            if (manifest is null || manifest.Any(pair => pair is null || string.IsNullOrWhiteSpace(pair.GameId)))
                throw new FormatException("The manifest must be an array of entries with a gameId.");
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new FormatException("Invalid snapshot manifest. " + ex.Message, ex);
        }
    }

    static string ReadLocal(string path, string directory, string label)
    {
        try
        {
            var fullPath = directory.Length == 0 ? Path.GetFullPath(path) : Path.GetFullPath(path, directory);
            var local = ConfigPaths.CanonicalLocal(fullPath);
            if (local is null || ConfigPaths.Inspect(local) != PathState.File)
                throw new IOException("Expected a local file.");

            using var bytes = new MemoryStream(ConfigPaths.ReadBounded(local));
            using var reader = new StreamReader(bytes, new UTF8Encoding(false, true));
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ConfigPaths.IsReadError(ex) || ex is ArgumentException or NotSupportedException)
        {
            throw new FormatException("The " + label + " could not be read as a local JSON file.", ex);
        }
    }

    static bool TryStep(string? text, out QualityStep step) =>
        QualitySteps.TryParse(text ?? "", out step) &&
        (string.Equals(text, step.Label(), StringComparison.OrdinalIgnoreCase) ||
         string.Equals(text, step.ToString(), StringComparison.OrdinalIgnoreCase) ||
         string.Equals(text, ((int)step).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));

    static string DisplayId(string id) => id.StartsWith("manual:", StringComparison.OrdinalIgnoreCase)
        ? "manual:" + PathDisplay.Short(id["manual:".Length..])
        : id;

    sealed record SnapshotPair(string GameId, string? Before = null, string? After = null,
        string? Step = null, IReadOnlyList<string>? Keys = null);
}
