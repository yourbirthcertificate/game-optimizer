using System.Text;
using System.Text.Json;
using OptiGame.Core.Formats;
using OptiGame.Core.Optimize;

namespace OptiGame.Core.Library.ConfigDiscovery;

internal sealed class KnownConfigSource : IConfigSource
{
    const string Engine = "Engine";
    internal const string Rule = "Matched rule";

    public IEnumerable<ConfigLead> Find(ConfigSearch search)
    {
        var game = search.Game;
        var roots = search.Paths.Roots;
        switch (game.Engine)
        {
            case GameEngine.Unreal:
                foreach (var path in Planner.UnrealSettingsPaths(game, search.Rule, roots.Local, roots.Documents))
                    yield return ConfigLead.File(path, "ini", new(Engine, "Unreal GameUserSettings.ini location."),
                        strongAssociation: true, confirm: IsUnrealSettings);
                break;
            case GameEngine.Source when search.Paths.Install is not null:
                yield return ConfigLead.File(Planner.SourceSettingsPath(game), "kv",
                    new(Engine, "Source video settings location."), strongAssociation: true, confirm: IsSourceSettings);
                break;
            case GameEngine.RedEngine:
                yield return ConfigLead.File(Planner.RedSettingsPath(game, roots.Local), "red",
                    new(Engine, "REDengine user settings location."), strongAssociation: true, confirm: IsRedSettings);
                break;
        }

        if (search.Rule is not { } rule) yield break;
        foreach (var file in rule.Files)
        {
            if (IsRegistry(file)) continue;

            var format = file.Format.ToLowerInvariant();
            var path = RuleSet.ExpandPath(file.Path, game);
            yield return ConfigLead.File(path, format, new(Rule, "Settings file declared by rule " + rule.Id + "."),
                strongAssociation: true, confirm: data => HasRuleKeys(data, format, file.Set.Keys));
        }
    }

    internal static bool IsRegistry(RuleFile file) => file.Format.Equals("registry", StringComparison.OrdinalIgnoreCase);

    internal static string? UnityPath(GameInfo game)
    {
        if (game.Engine != GameEngine.Unity || game.EngineDetail is not { } detail) return null;
        return detail.Split('\\') is [var company, var product]
            && ConfigPaths.IsComponent(company) && ConfigPaths.IsComponent(product) ? detail : null;
    }

    static bool IsUnrealSettings(byte[] bytes)
    {
        var options = FormatHandlers.Ini.Read(bytes).Content.Flatten();
        return options.Any(option => option.Value is not null &&
            (option.Key.StartsWith("ScalabilityGroups/sg.", StringComparison.OrdinalIgnoreCase) ||
             (option.Key.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) &&
              option.Key.EndsWith("/ResolutionSizeX", StringComparison.OrdinalIgnoreCase))));
    }

    static bool IsRedSettings(byte[] bytes)
    {
        var options = FormatHandlers.Red.Read(bytes).Content.Options;
        return options.GroupBy(option => (option.Group, option.Name)).Any(group =>
            group.Count() == 1 && group.First() is { Value.Start: >= 0 } option &&
            (option.Group.StartsWith("/graphics/", StringComparison.Ordinal)
                || option.Group.StartsWith("/video/", StringComparison.Ordinal)));
    }

    static bool IsSourceSettings(byte[] bytes)
    {
        var blocks = KeyValues(bytes).Children.Where(node =>
            node.Key.Equals("VideoConfig", StringComparison.OrdinalIgnoreCase)).ToArray();
        return blocks is [var block] && block.Value is null &&
            new[] { "setting.defaultres", "setting.defaultresheight", "setting.mat_vsync" }
                .Any(key => block.Children.Count(child => child.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) == 1
                    && block.Get(key) is not null);
    }

    static bool HasRuleKeys(byte[] bytes, string format, IEnumerable<string> keys)
    {
        switch (format)
        {
            case "ini":
                var options = FormatHandlers.Ini.Read(bytes).Content.Flatten();
                return keys.Any(key => key.IndexOf('/') > 0 && options.GetValueOrDefault(key) is not null);
            case "json":
                using (var json = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }))
                    return keys.Any(key => HasJsonKey(json.RootElement, key));
            case "kv":
                var nodes = KeyValues(bytes);
                return keys.Any(key => CountValues(nodes, key) == 1);
            case "gvas":
                var save = FormatHandlers.Gvas.Read(bytes).Content;
                return keys.Any(key => save.Properties.Where(property => property.Name == key).ToArray()
                    is [var property] && property.Get() is not null);
            default:
                return false;
        }
    }

    static bool HasJsonKey(JsonElement node, string dottedPath)
    {
        foreach (var part in dottedPath.Split('.'))
        {
            if (node.ValueKind != JsonValueKind.Object ||
                node.EnumerateObject().Where(property => property.NameEquals(part)).ToArray() is not [var property])
                return false;
            node = property.Value;
        }
        return node.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String;
    }

    internal static KvNode KeyValues(byte[] bytes)
    {
        var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        // The shared KeyValues parser is recursive. Bound nesting before passing it on.
        var depth = 0;
        foreach (var token in KvNode.Tokenize(text))
        {
            if (token is null) depth--;
            else if (token == "\u0001{") depth++;
            if (depth is < 0 or > 64) throw new FormatException("KeyValues nesting is outside the discovery limits.");
        }
        return KvNode.Parse(text);
    }

    static int CountValues(KvNode node, string key) => node.Children.Sum(child =>
        child.Value is null ? CountValues(child, key) : child.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ? 1 : 0);

    internal static bool CanConfirm(ConfigLead lead, byte[] bytes)
    {
        try { return lead.Confirm!(bytes); }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            return false;
        }
    }
}

internal sealed class RegistryConfigSource : IConfigSource
{
    const string Source = "Registry";

    public IEnumerable<ConfigLead> Find(ConfigSearch search)
    {
        var found = false;
        if (KnownConfigSource.UnityPath(search.Game) is { } unity)
        {
            found = true;
            yield return new(@"HKCU\Software\" + unity, ConfigCandidateKind.Registry, "registry",
                new(Source, "Unity app.info identifies the company and product."), StrongAssociation: true);
        }
        if (search.Rule is { } rule)
        {
            foreach (var file in rule.Files.Where(KnownConfigSource.IsRegistry))
            {
                found = true;
                yield return new(file.Path, ConfigCandidateKind.Registry, "registry",
                    new(KnownConfigSource.Rule, "Registry key declared by rule " + rule.Id + "."), StrongAssociation: true);
            }
        }
        if (!found)
            search.Note(Source, "no-key-metadata", "No exact game key was supplied by engine metadata or a matched rule.");
    }

    internal static string? CanonicalKey(string path)
    {
        if (!RuleSet.TryRegistryPath(path, out var sub) ||
            !sub.StartsWith(@"Software\", StringComparison.OrdinalIgnoreCase) ||
            !sub.Split('\\').All(ConfigPaths.IsComponent))
            return null;
        return @"HKCU\" + sub;
    }
}
