using System.Globalization;
using OptiGame.Core.Formats;
using OptiGame.Core.Library;
using OptiGame.Core.Library.ConfigDiscovery;

namespace OptiGame.Core.Optimize;

public static partial class Planner
{
    internal static string ZomboidUserProfile { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    const string ZomboidVSync = FlatIniSettings.Format + "|vsync";

    internal static bool IsZomboid(GameInfo game) =>
        game.SteamAppId == "108600" && game.Store.Equals("Steam", StringComparison.OrdinalIgnoreCase);

    internal static bool IsZomboidSettings(byte[] bytes) => ReadZomboidSettings(bytes) is not null;

    static (ISettingsDocument Document, string VSync)? ReadZomboidSettings(byte[] bytes)
    {
        var document = FormatHandlers.Find(FlatIniSettings.Format)!.Read(bytes);
        var current = document.GetRaw(ZomboidVSync);
        return current is ("true" or "false") && Positive("version") && Positive("width") && Positive("height")
            ? (document, current) : null;

        bool Positive(string key) =>
            int.TryParse(document.GetRaw(FlatIniSettings.Format + "|" + key), NumberStyles.None, Inv, out var value) &&
            value > 0;
    }

    static void Zomboid(GamePlan plan, OptimizeRequest req)
    {
        var folder = Path.Combine(ZomboidUserProfile, "Zomboid");
        var path = ConfigPaths.CanonicalLocal(Path.Combine(folder, "options.ini"));
        if (path is null)
        {
            plan.Skip(SkipKind.LocationNotFound, "The Project Zomboid settings path is outside the local profile.");
            return;
        }

        byte[] bytes;
        (ISettingsDocument Document, string VSync)? settings;
        try
        {
            var state = ConfigPaths.Inspect(path);
            if (state != PathState.File)
            {
                plan.Skip(state == PathState.Missing ? SkipKind.NotCreatedYet : SkipKind.LocationNotFound,
                    state == PathState.Missing ? "Project Zomboid has not created options.ini yet."
                        : "The Project Zomboid settings path cannot be read safely.", folder);
                return;
            }
            bytes = ConfigPaths.ReadBounded(path);
            settings = ReadZomboidSettings(bytes);
        }
        catch (FormatException ex) { plan.Skip(SkipKind.UnsupportedFormat, ex.Message, folder); return; }
        catch (Exception ex) when (ConfigPaths.IsReadError(ex))
        {
            plan.Skip(SkipKind.LocationNotFound, "Project Zomboid's options.ini could not be read within the settings limits.", folder);
            return;
        }

        if (settings is not { } config)
        {
            plan.Skip(SkipKind.Ambiguous, "Could not verify a unique VSync boolean and the expected version and dimensions.", folder);
            return;
        }

        plan.SettingsFolder = folder;
        plan.NotControlled.Add("Project Zomboid INI support covers VSync.");
        const string settingId = "zomboid:vsync";
        const string label = "VSync";
        var value = req.VSync switch { VSyncChoice.On => "true", VSyncChoice.Off => "false", _ => config.VSync };
        if (value == config.VSync || !Want(plan, req, settingId, Categories.VSync, label, config.VSync, value))
        {
            plan.Examined = true;
            return;
        }

        if (!config.Document.TrySet(ZomboidVSync, value))
        {
            plan.Skip(SkipKind.Ambiguous, "The VSync value could not be edited safely.", folder);
            return;
        }

        var edit = new FileEdit
        {
            Path = path, Format = FlatIniSettings.Format, Original = bytes, Updated = config.Document.ToBytes(),
        };
        edit.Changes.Add(new SettingChange(label, config.VSync, value)
        {
            Id = settingId, Category = Categories.VSync, Locator = ZomboidVSync, OldRaw = config.VSync, NewRaw = value,
        });
        plan.Files.Add(edit);
    }
}
