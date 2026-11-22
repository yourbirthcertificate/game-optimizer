using System.Text.Json;

namespace OptiGame.Core.Library.ConfigDiscovery;

internal sealed record ReviewedLocation(
    string SteamAppId, string? Store, string Root, string RelativePath, string FormatHint,
    string Source, Uri Url, DateOnly Reviewed);

internal sealed class ReviewedConfigSource(IReadOnlyList<ReviewedLocation>? pack, Func<string?> steamRoot) : IConfigSource
{
    const string Source = "Reviewed config pack";
    const string Resource = "OptiGame.Core.config.locations.json";
    const string StoreUser = "<storeUserId>";
    static readonly Lazy<IReadOnlyList<ReviewedLocation>> EmbeddedPack = new(Load);

    public IEnumerable<ConfigLead> Find(ConfigSearch search)
    {
        if (search.SteamId is not { } id || !ConfigPaths.IsId(id))
        {
            search.Note(Source, "no-store-id", "No exact Steam app ID is available for the reviewed pack.");
            yield break;
        }

        var found = false;
        foreach (var entry in pack ?? EmbeddedPack.Value)
        {
            if (entry.SteamAppId != id || (entry.Store is not null &&
                (!entry.Store.Equals(search.Game.Store, StringComparison.OrdinalIgnoreCase) || search.Game.SteamAppId != id)))
                continue;
            found = true;
            var path = Expand(entry, search);
            if (path is null) continue;
            yield return ConfigLead.File(path, entry.FormatHint,
                new(entry.Source, "Reviewed Windows config path for Steam app " + id + ".", entry.Url, entry.Reviewed),
                strongAssociation: true,
                confirm: entry.FormatHint == "xml" && Optimize.Planner.IsEldenRing(search.Game)
                    ? Optimize.Planner.IsEldenRingSettings : null);
        }
        if (!found)
            search.Note(Source, "no-match", "The pack has no Windows config path for this game and store.");
    }

    string? Expand(ReviewedLocation entry, ConfigSearch search)
    {
        var roots = search.Paths.Roots;
        var root = entry.Root switch
        {
            "local" => roots.Local,
            "localLow" => roots.LocalLow,
            "roaming" => roots.Roaming,
            "documents" => roots.Documents,
            "myGames" => roots.MyGames,
            "savedGames" => roots.SavedGames,
            "programData" => roots.ProgramData,
            "profile" => roots.Profile,
            "install" => search.Paths.Install,
            _ => null,
        };
        var parts = entry.RelativePath.Split(new[] { '\\', '/' });
        if (string.IsNullOrEmpty(root) || parts.Any(part => part != StoreUser && !ConfigPaths.IsFileComponent(part)) ||
            parts.Any(part => part.Contains('%') || part.Contains('{') || part.Contains('}')))
        {
            search.Note(Source, "blocked-template",
                "The reviewed path uses an unknown root or an unsupported path segment.", entry.RelativePath);
            return null;
        }

        var account = Array.IndexOf(parts, StoreUser);
        if (account >= 0)
        {
            if (entry.Store != "Steam" || Array.LastIndexOf(parts, StoreUser) != account ||
                ReadOnlySteamAccount() is not { } steamId)
            {
                search.Note(Source, "store-user-unresolved", "The path needs an unambiguous Steam account ID on this PC.");
                return null;
            }
            parts[account] = steamId;
        }
        return Path.Combine(root, Path.Combine(parts));
    }

    string? ReadOnlySteamAccount()
    {
        try
        {
            if (steamRoot() is not { } root ||
                ConfigPaths.CanonicalLocal(Path.Combine(root, "config", "loginusers.vdf")) is not { } path ||
                ConfigPaths.Inspect(path) != PathState.File)
                return null;

            var users = KnownConfigSource.KeyValues(ConfigPaths.ReadBounded(path))["users"];
            return users?.Children is [var user] && user.Value is null && user.Key.Length == 17 && ConfigPaths.IsId(user.Key)
                ? user.Key : null;
        }
        catch (Exception ex) when (ConfigPaths.IsReadError(ex) || ex is FormatException or System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    static IReadOnlyList<ReviewedLocation> Load()
    {
        using var stream = typeof(ReviewedConfigSource).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("The reviewed config pack is missing.");
        return JsonSerializer.Deserialize<ReviewedLocation[]>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new FormatException("The reviewed config pack is empty.");
    }
}
