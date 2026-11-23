using System.Text;
using OptiGame.Core.Art;

namespace OptiGame.Tests;

public class EpicArtTests
{
    static string Catalog(string json)
    {
        var file = Path.Combine(Path.GetTempPath(), "optigame-epic-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(file, Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
        return file;
    }

    [Fact]
    public void A_launcher_game_with_only_DieselGameBox_gets_its_wide_key_art()
    {
        var file = Catalog("""[{"title":"Fortnite","keyImages":[{"type":"DieselGameBoxTall","url":"https://cdn1.epicgames.com/tall.jpg"},{"type":"DieselGameBox","url":"https://cdn1.epicgames.com/wide.jpg"}]}]""");
        try { Assert.Equal("https://cdn1.epicgames.com/wide.jpg", ArtService.EpicImageUrl("fortnite", file)); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void An_offer_wide_image_is_preferred_and_portrait_art_is_never_chosen_over_wide()
    {
        var file = Catalog("""[{"title":"G","keyImages":[{"type":"DieselGameBox","url":"https://cdn1.epicgames.com/box.jpg"},{"type":"OfferImageWide","url":"https://cdn1.epicgames.com/offer.jpg"},{"type":"Thumbnail","url":"https://cdn1.epicgames.com/thumb.jpg"}]}]""");
        try { Assert.Equal("https://cdn1.epicgames.com/offer.jpg", ArtService.EpicImageUrl("G", file)); }
        finally { File.Delete(file); }
    }
}

public class SteamArtTests
{
    [Fact]
    public void Steam_local_cache_finds_library_header_in_a_hashed_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "optigame-steam-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "appcache", "librarycache", "2062430", "a157aa8de4bd9070194ddffb27c31636355dca05");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "library_header.jpg"), [1]);
        try { Assert.EndsWith("library_header.jpg", ArtService.SteamLocal("2062430", root)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_library_hero_url_is_an_https_steam_image_url_on_an_allowed_host()
        => Assert.True(ArtService.HostAllowed(ArtService.SteamHeroUrl("2062430")));
}
