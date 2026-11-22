using OptiGame.Core.Art;
using OptiGame.Core.Library;

namespace OptiGame.Tests;

[Collection("data dir")]
public sealed class GameArtTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "optigame-art-" + Guid.NewGuid().ToString("N"));
    readonly string? _oldData = Environment.GetEnvironmentVariable("GAMEOPT_DATA_DIR");
    readonly GameInfo _game = new() { Id = "fixture:123", Name = "Fixture", Store = "Manual", InstallDir = "" };

    static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jA8sAAAAASUVORK5CYII=");
    static readonly byte[] Icon =
    [
        0, 0, 1, 0, 1, 0, 1, 1, 0, 0, 1, 0, 32, 0, (byte)Png.Length, 0, 0, 0, 22, 0, 0, 0,
        .. Png,
    ];

    public GameArtTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", _root);
    }

    [Fact]
    public void Cover_and_small_icon_persist_and_reset_independently()
    {
        var source = Path.Combine(_root, "cover.png");
        File.WriteAllBytes(source, Png);
        Assert.True(ArtService.SetUserImage(_game, source));
        Assert.True(ArtService.SetUserImage(_game, ArtSlot.SmallIcon, Icon));

        var cover = ArtService.UserPath(_game);
        var icon = ArtService.UserPath(_game, ArtSlot.SmallIcon);
        Assert.NotEqual(cover, icon);
        Assert.Equal(cover, ArtService.Local(_game)?.Path);
        Assert.Equal(Icon, ArtService.ReadUserImage(icon, ArtSlot.SmallIcon));

        ArtService.Reset(_game);
        Assert.False(File.Exists(cover));
        Assert.Equal(Icon, File.ReadAllBytes(icon));

        Assert.True(ArtService.SetUserImage(_game, source));
        Assert.True(ArtService.ClearUserSmallIcon(_game));
        Assert.False(File.Exists(icon));
        Assert.Equal(Png, File.ReadAllBytes(cover));
        Assert.Equal(Png, File.ReadAllBytes(source));
    }

    [Fact]
    public void Unsupported_replacement_leaves_the_current_cover_and_icon_intact()
    {
        Assert.True(ArtService.SetUserImage(_game, ArtSlot.Cover, Png));
        Assert.True(ArtService.SetUserImage(_game, ArtSlot.SmallIcon, Icon));
        Assert.False(ArtService.SetUserImage(_game, ArtSlot.Cover, Icon));
        Assert.False(ArtService.SetUserImage(_game, ArtSlot.SmallIcon, [1, 2, 3]));
        Assert.Equal(Png, File.ReadAllBytes(ArtService.UserPath(_game)));
        Assert.Equal(Icon, File.ReadAllBytes(ArtService.UserPath(_game, ArtSlot.SmallIcon)));
        Assert.Empty(Directory.EnumerateFiles(ArtService.Dir, "*.tmp"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", _oldData);
        Directory.Delete(_root, recursive: true);
    }
}
