using OptiGame.Core.Formats;
using OptiGame.Core.Library;
using OptiGame.Core.Library.ConfigDiscovery;

namespace OptiGame.Core.Optimize;

public static partial class Planner
{
    const string HelldiversVSync = SjsonSettings.Format + "|vsync";

    internal static bool IsHelldivers(GameInfo game) =>
        game.SteamAppId == "553850" && game.Store.Equals("Steam", StringComparison.OrdinalIgnoreCase);

    internal static bool IsHelldiversSettings(byte[] bytes) => ReadHelldiversSettings(bytes) is not null;

    static (ISettingsDocument Document, string VSync)? ReadHelldiversSettings(byte[] bytes)
    {
        var document = FormatHandlers.Find(SjsonSettings.Format)!.Read(bytes);
        return document is SjsonSettings settings && settings.HasObject("render_settings") &&
               settings.GetRaw(HelldiversVSync) is { } vsync ? (settings, vsync) : null;
    }

    static void Helldivers(GamePlan plan, OptimizeRequest req)
    {
        var folder = Path.Combine(RoamingAppData, "Arrowhead", "Helldivers2");
        var path = ConfigPaths.CanonicalLocal(Path.Combine(folder, "user_settings.config"));
        if (path is null)
        {
            plan.Skip(SkipKind.LocationNotFound, "The Helldivers 2 settings path is outside the local profile.");
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
                    state == PathState.Missing ? "Helldivers 2 has not created user_settings.config yet."
                        : "The Helldivers 2 settings path cannot be read safely.", folder);
                return;
            }
            bytes = ConfigPaths.ReadBounded(path);
            settings = ReadHelldiversSettings(bytes);
        }
        catch (FormatException ex) { plan.Skip(SkipKind.UnsupportedFormat, ex.Message, folder); return; }
        catch (Exception ex) when (ConfigPaths.IsReadError(ex))
        {
            plan.Skip(SkipKind.LocationNotFound, "The Helldivers 2 config could not be read within the settings limits.", folder);
            return;
        }

        if (settings is not { } config)
        {
            plan.Skip(SkipKind.Ambiguous, "Could not verify a unique top-level VSync boolean and render settings block.", folder);
            return;
        }

        plan.SettingsFolder = folder;
        plan.NotControlled.Add("Helldivers 2 config support covers VSync.");
        var value = req.VSync switch { VSyncChoice.On => "true", VSyncChoice.Off => "false", _ => config.VSync };
        const string settingId = "helldivers2:vsync";
        const string label = "VSync";
        if (value == config.VSync || !Want(plan, req, settingId, Categories.VSync, label, config.VSync, value))
        {
            plan.Examined = true;
            return;
        }

        if (!config.Document.TrySet(HelldiversVSync, value))
        {
            plan.Skip(SkipKind.Ambiguous, "The VSync value could not be edited safely.", folder);
            return;
        }
        var edit = new FileEdit
        {
            Path = path, Format = SjsonSettings.Format, Original = bytes, Updated = config.Document.ToBytes(),
        };
        edit.Changes.Add(new SettingChange(label, config.VSync, value)
        {
            Id = settingId, Category = Categories.VSync, Locator = HelldiversVSync, OldRaw = config.VSync, NewRaw = value,
        });
        plan.Files.Add(edit);
    }
}
