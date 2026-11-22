using OptiGame.Core.Live;

namespace OptiGame.Core.Library.ConfigDiscovery;

internal sealed class FolderConfigSource(LocalConfigFolders folders) : IConfigSource
{
    const string Source = "Local folders";

    public IEnumerable<ConfigLead> Find(ConfigSearch search)
    {
        var game = search.Game;
        var names = SettingsLocator.MatchKeys(game, minimumLength: 1).ToHashSet(StringComparer.Ordinal);
        var index = folders.For(search);
        var matches = names.SelectMany(name => index.GetValueOrDefault(name) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (matches.Count > 1)
            search.Note(Source, "multiple-matches", "More than one folder matches the game name or install-folder name.");
        var engineFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unityPath = KnownConfigSource.UnityPath(game);
        if (unityPath is not null)
        {
            foreach (var root in new[] { search.Paths.Roots.LocalLow, search.Paths.Roots.Local, search.Paths.Roots.Roaming })
            {
                if (search.Paths.CanonicalFile(Path.Combine(root, unityPath)) is { } path)
                    engineFolders.Add(path);
            }
        }
        matches.UnionWith(engineFolders);

        var listing = new ConfigListings(search, Source, 4096);
        var found = false;
        foreach (var folder in matches.Order(StringComparer.OrdinalIgnoreCase))
        {
            var byEngine = engineFolders.Contains(folder);
            var reason = byEngine ? "Folder matches the company and product reported by the game."
                : "Folder name matches the game or install-folder name.";
            foreach (var lead in SettingsIn(listing, folder, depth: 2, new(Source, reason), byEngine))
            {
                found = true;
                yield return lead;
            }

            if (game.SteamAppId is { } appId && ConfigPaths.IsId(appId)
                && listing.DirectoryExists(Path.Combine(folder, appId)))
            {
                foreach (var lead in SettingsIn(listing, Path.Combine(folder, appId), depth: 2,
                    new(Source, "Folder under a game-name match has the exact Steam app ID."), strongAssociation: true))
                {
                    found = true;
                    yield return lead;
                }
            }
        }

        if (search.Paths.Install is { } install)
        {
            foreach (var lead in SettingsIn(new ConfigListings(search, Source, 4096), install, depth: 0,
                new(Source, "Settings-like filename beside the installed game. It may be a shipped default.")))
            {
                found = true;
                yield return lead;
            }
        }
        else search.Note(Source, "blocked-path", "The install directory is outside the local search scope.");

        if (!found)
            search.Note(Source, "no-match", "No settings-like files were found in matched folders or the install directory.");
    }

    internal static IEnumerable<ConfigLead> SettingsIn(ConfigListings listing, string folder, int depth,
        ConfigEvidence evidence, bool strongAssociation = false)
    {
        foreach (var file in listing.Walk(folder, depth).OfType<FileInfo>())
        {
            if (!SettingsLocator.IsSettingsFile(file.Name)) continue;
            yield return ConfigLead.File(file.FullName, FormatHint(file.Extension), evidence, strongAssociation);
        }
    }

    static string? FormatHint(string extension) => extension.ToLowerInvariant() switch
    {
        ".ini" => "ini",
        ".json" => "json",
        ".xml" => "xml",
        ".vcfg" => "vcfg",
        ".lua" => "lua",
        ".yaml" or ".yml" => "yaml",
        ".toml" => "toml",
        ".cfg" or ".config" or ".conf" or ".settings" or ".txt" => null,
        _ => null,
    };
}

internal sealed class ExecutableConfigSource(LocalConfigFolders folders) : IConfigSource
{
    const string Source = "Executable name";

    public IEnumerable<ConfigLead> Find(ConfigSearch search)
    {
        if (search.Paths.Install is not { } install) yield break;

        var listing = new ConfigListings(search, Source, 4096);
        var stems = listing.Walk(install, depth: 3).OfType<FileInfo>()
            .Where(file => file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(file => !GameExes.IsExcludedName(file.Name))
            .Select(file => Path.GetFileNameWithoutExtension(file.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var index = folders.For(search);
        var local = ConfigPaths.CanonicalLocal(search.Paths.Roots.Local);
        var found = false;
        foreach (var stem in stems)
        {
            var matches = index.GetValueOrDefault(SettingsLocator.Normalize(stem)) ?? [];
            foreach (var folder in matches)
            {
                if (local is null || !folder.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(folder).Equals(stem, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var lead in FolderConfigSource.SettingsIn(listing, folder, depth: 1,
                    new(Source, "Local AppData folder name matches " + stem + ".exe from the install directory.")))
                {
                    found = true;
                    yield return lead;
                }
            }
        }
        if (!found)
            search.Note(Source, "no-match", "No settings-like files were found under an exact executable-name match in Local AppData.");
    }
}

/// <summary>One bounded folder index per discovery instance. Rescans use a new instance.</summary>
internal sealed class LocalConfigFolders
{
    const string Source = "Local folders";
    readonly object _gate = new();
    Dictionary<string, List<string>>? _index;
    ConfigDiagnostic[] _diagnostics = [];

    public IReadOnlyDictionary<string, List<string>> For(ConfigSearch search)
    {
        lock (_gate)
        {
            if (_index is null)
            {
                var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var firstDiagnostic = search.Diagnostics.Count;
                var listing = new ConfigListings(search, Source, 20000);
                foreach (var root in search.Paths.Roots.Folders)
                {
                    foreach (var folder in listing.Walk(root, depth: 1, foldersOnly: true).OfType<DirectoryInfo>())
                    {
                        var key = SettingsLocator.Normalize(folder.Name);
                        if (key.Length == 0) continue;
                        if (!index.TryGetValue(key, out var paths)) index.Add(key, paths = new());
                        paths.Add(folder.FullName);
                    }
                }
                _diagnostics = search.Diagnostics.Skip(firstDiagnostic).ToArray();
                _index = index;
            }
            else search.Diagnostics.AddRange(_diagnostics);

            return _index;
        }
    }
}

internal sealed class ConfigListings(ConfigSearch search, string source, int entryLimit)
{
    int _remaining = entryLimit;
    bool _reportedLimit;
    bool _reportedBlockedIndexEntry;

    public bool DirectoryExists(string path)
    {
        var full = search.Paths.CanonicalFile(path);
        if (full is null) return false;
        try
        {
            var state = ConfigPaths.Inspect(full);
            if (state == PathState.Blocked)
                search.Note(source, "blocked-path", "A reparse point or non-local drive was skipped.", full);
            return state == PathState.Directory;
        }
        catch (Exception ex) when (ConfigPaths.IsReadError(ex))
        {
            search.Note(source, "unreadable", "The folder could not be read.", full);
            return false;
        }
    }

    public IEnumerable<FileSystemInfo> Walk(string path, int depth, bool foldersOnly = false)
    {
        search.Token.ThrowIfCancellationRequested();
        if (_remaining == 0 || !DirectoryExists(path)) yield break;

        foreach (var entry in List(path, foldersOnly))
        {
            yield return entry;
            if (depth > 0 && entry is DirectoryInfo)
                foreach (var child in Walk(entry.FullName, depth - 1, foldersOnly))
                    yield return child;
        }
    }

    List<FileSystemInfo> List(string path, bool foldersOnly)
    {
        var result = new List<FileSystemInfo>();
        try
        {
            var directory = new DirectoryInfo(path);
            var entries = foldersOnly ? directory.EnumerateDirectories() : directory.EnumerateFileSystemInfos();
            foreach (var entry in entries)
            {
                search.Token.ThrowIfCancellationRequested();
                if (_remaining == 0) { ReportLimit(); break; }
                _remaining--;
                if (search.Paths.IsExcluded(entry.FullName)) continue;
                if (ConfigPaths.IsBlocked(entry.Attributes))
                {
                    if (!foldersOnly || !_reportedBlockedIndexEntry)
                        search.Note(source, "blocked-path", "A reparse point or offline entry was skipped.",
                            foldersOnly ? null : entry.FullName);
                    _reportedBlockedIndexEntry |= foldersOnly;
                    continue;
                }
                result.Add(entry);
            }
        }
        catch (Exception ex) when (ConfigPaths.IsReadError(ex))
        {
            search.Note(source, "unreadable", "The folder could not be listed completely.", path);
        }
        if (_remaining == 0) ReportLimit();
        return result;
    }

    void ReportLimit()
    {
        if (_reportedLimit) return;
        _reportedLimit = true;
        search.Note(source, "search-limit", "The directory listing limit was reached. Results may be partial.");
    }
}
