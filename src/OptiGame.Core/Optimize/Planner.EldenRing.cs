using System.Globalization;
using OptiGame.Core.Formats;
using OptiGame.Core.Library;
using OptiGame.Core.Library.ConfigDiscovery;

namespace OptiGame.Core.Optimize;

public static partial class Planner
{
    internal static string RoamingAppData { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    internal static bool IsEldenRing(GameInfo game) =>
        game.SteamAppId == "1245620" && game.Store.Equals("Steam", StringComparison.OrdinalIgnoreCase);

    internal static bool IsEldenRingSettings(byte[] bytes) => ReadEldenRingSettings(bytes) is not null;

    static (ISettingsDocument Document, string WidthKey, string HeightKey, string Width, string Height)?
        ReadEldenRingSettings(byte[] bytes)
    {
        var xml = FormatHandlers.Find("xml")!.Read(bytes);
        var prefix = xml.GetRaw("xml|config|ScreenMode") switch
        {
            "FULLSCREEN" => "Resolution-FullScreen",
            "BORDERLESS" => "Resolution-BorderlessScreen",
            _ => null,
        };
        if (prefix is null) return null;
        var widthKey = prefix + "Width";
        var heightKey = prefix + "Height";
        var width = xml.GetRaw("xml|config|" + widthKey);
        var height = xml.GetRaw("xml|config|" + heightKey);
        return int.TryParse(width, NumberStyles.None, Inv, out int w) && w > 0 &&
               int.TryParse(height, NumberStyles.None, Inv, out int h) && h > 0
            ? (xml, widthKey, heightKey, width!, height!) : null;
    }

    static void EldenRing(GamePlan plan, OptimizeRequest req)
    {
        const string settingId = "elden-ring:resolution";
        var folder = Path.Combine(RoamingAppData, "EldenRing");
        var path = ConfigPaths.CanonicalLocal(Path.Combine(folder, "GraphicsConfig.xml"));
        if (path is null)
        {
            plan.Skip(SkipKind.LocationNotFound, "The Elden Ring settings path is outside the local profile.");
            return;
        }

        byte[] bytes;
        (ISettingsDocument Document, string WidthKey, string HeightKey, string Width, string Height)? settings;
        try
        {
            var state = ConfigPaths.Inspect(path);
            if (state != PathState.File)
            {
                plan.Skip(state == PathState.Missing ? SkipKind.NotCreatedYet : SkipKind.LocationNotFound,
                    state == PathState.Missing ? "No Elden Ring graphics XML yet. Start the game once to create it."
                        : "The Elden Ring settings path cannot be read safely.", folder);
                return;
            }
            bytes = ConfigPaths.ReadBounded(path);
            settings = ReadEldenRingSettings(bytes);
        }
        catch (FormatException ex) { plan.Skip(SkipKind.UnsupportedFormat, ex.Message, folder); return; }
        catch (Exception ex) when (ConfigPaths.IsReadError(ex))
        {
            plan.Skip(SkipKind.LocationNotFound, "The Elden Ring graphics XML could not be read within the settings limits.", folder);
            return;
        }
        if (settings is not { } display)
        {
            plan.Skip(SkipKind.Ambiguous, "The current display mode and its resolution pair could not be verified in the XML.", folder);
            return;
        }

        plan.SettingsFolder = folder;
        plan.NotControlled.Add("Elden Ring XML support covers the current fullscreen or borderless resolution.");
        if (req.Resolution is not { } requested)
        {
            plan.Examined = true;
            return;
        }
        if (requested.w <= 0 || requested.h <= 0)
        {
            plan.Skip(SkipKind.Ambiguous, "The requested resolution must contain two positive dimensions.", folder);
            return;
        }

        var width = requested.w.ToString(Inv);
        var height = requested.h.ToString(Inv);
        var oldResolution = display.Width + "x" + display.Height;
        var newResolution = width + "x" + height;
        if (oldResolution == newResolution ||
            !Want(plan, req, settingId, Categories.Resolution, "Resolution", oldResolution, newResolution))
        {
            plan.Examined = true;
            return;
        }

        var locator = "xml|config|" + display.WidthKey + "|" + display.HeightKey;
        if (!display.Document.TrySet(locator, newResolution))
        {
            plan.Skip(SkipKind.Ambiguous, "The resolution pair could not be edited safely.", folder);
            return;
        }

        var edit = new FileEdit { Path = path, Original = bytes, Updated = display.Document.ToBytes(), Format = "xml" };
        edit.Changes.Add(new SettingChange("Resolution", oldResolution, newResolution)
        {
            Id = settingId, Category = Categories.Resolution, Locator = locator, OldRaw = oldResolution, NewRaw = newResolution,
        });
        plan.Files.Add(edit);
    }
}
