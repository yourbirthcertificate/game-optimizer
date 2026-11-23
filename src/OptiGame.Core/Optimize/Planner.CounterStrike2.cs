using System.Text;
using OptiGame.Core.Formats;
using OptiGame.Core.Library;
using OptiGame.Core.Library.ConfigDiscovery;

namespace OptiGame.Core.Optimize;

public static partial class Planner
{
    internal static Func<string?> CounterStrikeSteamRoot { get; set; } = LibraryScanner.SteamRoot;

    static bool IsCounterStrike2(GameInfo game) =>
        game.SteamAppId == "730" && game.Store.Equals("Steam", StringComparison.OrdinalIgnoreCase) &&
        game.Engine == GameEngine.Source2;

    static void CounterStrike2(GamePlan plan, OptimizeRequest request)
    {
        const string key = "setting.mat_vsync";
        const string locator = "kv|" + key;
        var steamRoot = CounterStrikeSteamRoot();
        if (steamRoot is null || SteamConfigAccount.SingleUserdataId(steamRoot) is not { } account)
        {
            plan.Skip(SkipKind.LocationNotFound, "A single local Steam account for Counter-Strike 2 could not be verified.");
            return;
        }

        var folder = Path.Combine(steamRoot, "userdata", account, "730", "local", "cfg");
        var path = ConfigPaths.CanonicalLocal(Path.Combine(folder, "cs2_video.txt"));
        var paths = new ConfigPaths(ConfigRoots.Current(), plan.Game.InstallDir);
        if (path is null || paths.IsExcluded(path))
        {
            plan.Skip(SkipKind.LocationNotFound, "The Counter-Strike 2 settings path could not be verified.");
            return;
        }

        byte[] bytes;
        KvSettingsDocument document;
        try
        {
            var state = ConfigPaths.Inspect(path);
            if (state != PathState.File)
            {
                plan.Skip(state == PathState.Missing ? SkipKind.NotCreatedYet : SkipKind.LocationNotFound,
                    state == PathState.Missing ? "Counter-Strike 2 has not created cs2_video.txt for the verified account."
                        : "The Counter-Strike 2 settings path cannot be read safely.", folder);
                return;
            }
            bytes = ConfigPaths.ReadBounded(path);
            document = FormatHandlers.Kv.Read(bytes);
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            plan.Skip(SkipKind.UnsupportedFormat, "Counter-Strike 2 video settings could not be parsed.", folder);
            return;
        }
        catch (Exception ex) when (ConfigPaths.IsReadError(ex))
        {
            plan.Skip(SkipKind.LocationNotFound, "Counter-Strike 2 video settings could not be read within the settings limits.", folder);
            return;
        }

        if (document.Root?.Children is not [{ Key: "video.cfg", Value: null } config] ||
            config.Children.Any(option => option.Value is null) ||
            config.Children.Where(option => option.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray() is not [var sync] ||
            sync.Value is not ("0" or "1") || document.GetRaw(locator) != sync.Value)
        {
            plan.Skip(SkipKind.Ambiguous, "Could not verify a unique VSync value in the Counter-Strike 2 video settings block.", folder);
            return;
        }

        plan.SettingsFolder = folder;
        plan.NotControlled.Add("Counter-Strike 2 config support covers VSync.");
        const string id = "cs2:vsync";
        const string label = "VSync";
        var desired = request.VSync switch { VSyncChoice.On => "1", VSyncChoice.Off => "0", _ => sync.Value };
        if (desired == sync.Value || !Want(plan, request, id, Categories.VSync, label, sync.Value, desired))
        {
            plan.Examined = true;
            return;
        }
        if (!document.TrySet(locator, desired))
        {
            plan.Skip(SkipKind.Ambiguous, "The Counter-Strike 2 VSync value could not be edited safely.", folder);
            return;
        }

        var edit = new FileEdit { Path = path, Format = "kv", Original = bytes, Updated = document.ToBytes() };
        edit.Changes.Add(new(label, sync.Value, desired)
        {
            Id = id, Category = Categories.VSync, Locator = locator, OldRaw = sync.Value, NewRaw = desired,
        });
        plan.Files.Add(edit);
    }
}
