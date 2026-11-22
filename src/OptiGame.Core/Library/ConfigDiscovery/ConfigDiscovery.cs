using Microsoft.Win32;
using OptiGame.Core.Optimize;

namespace OptiGame.Core.Library.ConfigDiscovery;

public enum ConfigCandidateKind { File, Registry }
public enum ConfigConfidence { Lead, Present, Confirmed }

public sealed record ConfigEvidence(string Source, string Observation, Uri? Url = null, DateOnly? Reviewed = null);

/// <summary>A location discovered on this PC. Confidence describes the evidence for the location.</summary>
public sealed record ConfigCandidate(
    string Path, ConfigCandidateKind Kind, string? FormatHint, ConfigConfidence Confidence,
    IReadOnlyList<ConfigEvidence> Evidence);

public sealed record ConfigDiagnostic(string Source, string Code, string Message, string? Path = null);

public sealed record ConfigDiscoveryResult(
    IReadOnlyList<ConfigCandidate> Candidates, IReadOnlyList<ConfigDiagnostic> Diagnostics);

public sealed record ConfigDiscoveryOptions(GameRule? Rule = null, string? LinkedSteamAppId = null);

/// <summary>Read-only settings discovery. Use one instance for a library scan to share the folder index.</summary>
public sealed class ConfigFinder
{
    readonly ConfigRoots _roots;
    readonly IConfigSource[] _sources;
    readonly Func<string, IReadOnlyList<string>?> _registryNames;

    public ConfigFinder() : this(ConfigRoots.Current(), ReadRegistryNames) { }

    internal ConfigFinder(ConfigRoots roots, Func<string, IReadOnlyList<string>?> registryNames,
        IReadOnlyList<ReviewedLocation>? pack = null, Func<string?>? steamRoot = null)
    {
        _roots = roots;
        _registryNames = registryNames;
        var folders = new LocalConfigFolders();
        _sources =
        [
            new KnownConfigSource(),
            new FolderConfigSource(folders),
            new RegistryConfigSource(),
            new ReviewedConfigSource(pack, steamRoot ?? LibraryScanner.SteamRoot),
            new ExecutableConfigSource(folders),
        ];
    }

    public ConfigDiscoveryResult Find(GameInfo game, ConfigDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var search = new ConfigSearch(game, options ?? new(), _roots, cancellationToken);
        // File paths start with a drive letter and colon. Registry keys start with HKCU\.
        var locations = new Dictionary<string, LocatedConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var lead in source.Find(search))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isFile = lead.Kind == ConfigCandidateKind.File;
                var path = isFile ? search.Paths.CanonicalFile(lead.Path) : RegistryConfigSource.CanonicalKey(lead.Path);
                if (path is null)
                {
                    search.Note(lead.Evidence.Source, "blocked-path",
                        "The location is outside the search roots or uses an unsupported path.", lead.Path);
                    continue;
                }

                if (!locations.TryGetValue(path, out var location))
                    locations.Add(path, location = new LocatedConfig(path, lead.Kind));
                location.Leads.Add(lead);
            }
        }

        var candidates = locations.Values.Select(location => Verify(location, search))
            .Where(candidate => candidate is not null).Select(candidate => candidate!.Value)
            .OrderByDescending(candidate => candidate.Value.Confidence)
            .ThenByDescending(candidate => candidate.StrongAssociation)
            .ThenBy(candidate => candidate.Value.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Value.Path, StringComparer.Ordinal)
            .Select(candidate => candidate.Value).ToArray();
        return new(candidates, search.Diagnostics.Distinct().ToArray());
    }

    (ConfigCandidate Value, bool StrongAssociation)? Verify(LocatedConfig location, ConfigSearch search)
    {
        search.Token.ThrowIfCancellationRequested();
        var leads = location.Leads.OrderByDescending(lead => lead.StrongAssociation).ToArray();
        var evidence = leads.Select(lead => lead.Evidence).Distinct().ToList();
        var hint = leads.Select(lead => lead.FormatHint).FirstOrDefault(format => format is not null);
        var confidence = ConfigConfidence.Lead;
        const string source = "Local verification";
        try
        {
            if (location.Kind == ConfigCandidateKind.Registry)
            {
                var names = _registryNames(location.Path);
                if (names is not null)
                {
                    confidence = ConfigConfidence.Present;
                    var display = names.Order(StringComparer.OrdinalIgnoreCase).ThenBy(x => x, StringComparer.Ordinal)
                        .Take(32).Select(name => name.Length == 0 ? "(default)" : name);
                    var detail = names.Count == 0 ? "Key exists with no value names."
                        : "Value names: " + string.Join(", ", display) + (names.Count > 32 ? ", …" : "");
                    evidence.Add(new(source, detail));
                }
                else search.Note(source, "missing", "The registry key has not been found.", location.Path);
            }
            else
            {
                var state = ConfigPaths.Inspect(location.Path);
                if (state is PathState.Blocked or PathState.Directory)
                {
                    search.Note(source, "blocked-path",
                        "The location is a directory, a reparse point, offline or on a non-local drive.", location.Path);
                    return null;
                }

                if (state == PathState.File)
                {
                    confidence = ConfigConfidence.Present;
                    evidence.Add(new(source, "File exists."));
                    var verifiers = leads.Where(lead => lead.Confirm is not null).ToArray();
                    if (verifiers.Length == 0)
                        search.Note(source, "unverified-format",
                            "A game-specific format check is unavailable for this location.", location.Path);
                    else
                    {
                        var bytes = ConfigPaths.ReadBounded(location.Path);
                        var verified = verifiers.FirstOrDefault(lead => KnownConfigSource.CanConfirm(lead, bytes));
                        if (verified is not null)
                        {
                            confidence = ConfigConfidence.Confirmed;
                            hint = verified.FormatHint;
                            evidence.Add(new(source, "Parsed the expected settings structure."));
                        }
                        else search.Note(source, "unrecognized-content",
                            "The file did not contain the expected settings structure.", location.Path);
                    }
                }
                else search.Note(source, "missing", "The file has not been found.", location.Path);
            }
        }
        catch (Exception ex) when (ConfigPaths.IsReadError(ex) || ex is FormatException)
        {
            search.Note(source, "unreadable", ex is FormatException
                ? "The file could not be parsed as the expected settings format."
                : "The location could not be read within the search limits.", location.Path);
        }

        return (new(location.Path, location.Kind, hint, confidence, evidence.ToArray()), leads[0].StrongAssociation);
    }

    static string[]? ReadRegistryNames(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path[5..], writable: false);
        return key?.GetValueNames();
    }

    sealed record LocatedConfig(string Path, ConfigCandidateKind Kind)
    {
        public List<ConfigLead> Leads { get; } = new();
    }

}

internal interface IConfigSource
{
    IEnumerable<ConfigLead> Find(ConfigSearch search);
}

internal sealed record ConfigLead(
    string Path, ConfigCandidateKind Kind, string? FormatHint, ConfigEvidence Evidence,
    bool StrongAssociation = false, Func<byte[], bool>? Confirm = null)
{
    public static ConfigLead File(string path, string? formatHint, ConfigEvidence evidence,
        bool strongAssociation = false, Func<byte[], bool>? confirm = null) =>
        new(path, ConfigCandidateKind.File, formatHint, evidence, strongAssociation, confirm);
}

internal sealed class ConfigSearch(GameInfo game, ConfigDiscoveryOptions options, ConfigRoots roots, CancellationToken token)
{
    public GameInfo Game { get; } = game;
    public GameRule? Rule { get; } = options.Rule is { } rule && rule.Matches(game) ? rule : null;
    public string? SteamId { get; } = game.SteamAppId ?? options.LinkedSteamAppId;
    public ConfigPaths Paths { get; } = new(roots, game.InstallDir);
    public CancellationToken Token { get; } = token;
    public List<ConfigDiagnostic> Diagnostics { get; } = new();

    public void Note(string source, string code, string message, string? path = null) =>
        Diagnostics.Add(new(source, code, message, path));
}
