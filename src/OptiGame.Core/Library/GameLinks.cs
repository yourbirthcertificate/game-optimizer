namespace OptiGame.Core.Library;

public sealed record GameLink(string Label, string Url);

/// <summary>Where to read what people say about a game's settings: PCGamingWiki and Steam guides.</summary>
public static class GameLinks
{
    public static List<GameLink> For(GameInfo g)
    {
        var links = new List<GameLink>();
        if (g.SteamAppId is { } id)
        {
            // PCGamingWiki redirects this to the game's page (fixes, settings, known issues).
            links.Add(new("PCGamingWiki", $"https://www.pcgamingwiki.com/api/appid.php?appid={id}"));
            links.Add(new("Steam guides", $"https://steamcommunity.com/app/{id}/guides/?browsefilter=toprated"));
        }
        else
        {
            links.Add(new("PCGamingWiki", "https://www.pcgamingwiki.com/w/index.php?search=" + Uri.EscapeDataString(g.Name)));
        }
        return links;
    }
}
