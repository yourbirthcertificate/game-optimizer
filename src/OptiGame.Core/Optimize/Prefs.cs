using System.Text.Json;
using System.Text.Json.Serialization;
using OptiGame.Core.Hardware;

namespace OptiGame.Core.Optimize;

public enum FpsMode { Keep, Auto, Uncapped, Custom }
public enum ResolutionMode { Keep, Native, Current, Custom }
public enum DisplayModeChoice { Keep, Recommended, Fullscreen, Borderless, Windowed }
public enum VSyncChoice { Keep, On, Off }
public enum QualityMode { Offset, Fixed }

/// <summary>Setting categories that can be kept as they are ("keep my settings").</summary>
public static class Categories
{
    public const string Resolution = "resolution", DisplayMode = "displayMode", VSync = "vsync", FpsCap = "fpsCap",
        RenderScale = "renderScale", Texture = "texture", Quality = "quality", Other = "other";

    public static readonly string[] Keepable = { Resolution, DisplayMode, VSync, FpsCap, RenderScale, Texture, Quality };

    public static string Label(string c) => c switch
    {
        Resolution => "Resolution", DisplayMode => "Display mode", VSync => "VSync", FpsCap => "Frame-rate cap",
        RenderScale => "Render scale", Texture => "Texture quality", Quality => "Other quality settings", _ => "Other",
    };
}

public sealed class FpsPolicy
{
    public FpsMode Mode { get; set; } = FpsMode.Auto;
    /// <summary>Custom cap in frames per second (fractions allowed).</summary>
    public double? Value { get; set; }

    public static readonly double[] Presets = { 30, 60, 90, 120, 144 };
    public string Describe(DisplayInfo d) => Mode switch
    {
        FpsMode.Keep => "Keep the game's setting",
        FpsMode.Auto => $"Automatic: {d.AutoFpsCap} fps (active {DisplayMath.Hz(d.CurrentHz)} Hz minus 3)",
        FpsMode.Uncapped => "No cap, where the game allows it",
        _ => $"Cap at {Value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "?"} fps",
    };
}

public sealed class ResolutionPref
{
    public ResolutionMode Mode { get; set; } = ResolutionMode.Native;
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class QualityPref
{
    public QualityMode Mode { get; set; } = QualityMode.Offset;
    /// <summary>Steps from each game's recommendation (Offset mode).</summary>
    public int Offset { get; set; }
    /// <summary>The step used for every game (Fixed mode).</summary>
    public QualityStep Step { get; set; } = QualityStep.High;
}

/// <summary>Settings a profile or a game can set. Null means "not set here": the next level decides.</summary>
public class SettingsPrefs
{
    public QualityPref? Quality { get; set; }
    public FpsPolicy? Fps { get; set; }
    /// <summary>Gaming display by GDI device name (\\.\DISPLAY2). Selecting it never changes Windows settings.</summary>
    public string? Display { get; set; }
    public ResolutionPref? Resolution { get; set; }
    public DisplayModeChoice? DisplayMode { get; set; }
    public VSyncChoice? VSync { get; set; }
    /// <summary>Setting categories to leave as they are (see Categories).</summary>
    public List<string>? Preserve { get; set; }
}

/// <summary>A named bundle of preferences. It describes settings; it does not promise frame rates, heat or noise.</summary>
public sealed class Profile : SettingsPrefs
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    [JsonIgnore] public bool BuiltIn { get; set; }

    public Profile Clone(string id, string name)
    {
        var json = JsonSerializer.Serialize(this);
        var p = JsonSerializer.Deserialize<Profile>(json)!;
        p.Id = id; p.Name = name; p.BuiltIn = false;
        return p;
    }
}

public sealed class GamePrefs : SettingsPrefs
{
    /// <summary>Quality step set by hand on the game's slider; wins over every profile.</summary>
    public QualityStep? Step { get; set; }
    public string? ProfileId { get; set; }
    /// <summary>Setting ids left out of every optimize for this game ("keep my settings").</summary>
    public List<string> Excluded { get; set; } = new();
    /// <summary>Steam app id you linked to this game for requirement lookups (manual and GOG games).</summary>
    public string? SteamAppId { get; set; }

    [JsonIgnore] public bool IsEmpty => Step is null && ProfileId is null && Excluded.Count == 0 && SteamAppId is null &&
        Quality is null && Fps is null && Display is null && Resolution is null && DisplayMode is null && VSync is null && Preserve is null;
}

public sealed class ManualGame
{
    public string Path { get; set; } = "";
    public string? Name { get; set; }
}

/// <summary>
/// Saved choices. Precedence, highest first: the game's own settings, the profile assigned to the game,
/// the default profile, then the built-in Balanced profile. Quality: a step set by hand on the game's
/// slider wins; otherwise the profile's quality (an offset, plus the All games slider, or a fixed step).
/// </summary>
public sealed class Prefs
{
    public const int CurrentVersion = 2, MaxOffset = 3;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>-3 (faster) to +3 (nicer) from each game's recommendation; the All games slider.</summary>
    public int GlobalOffset { get; set; }
    public string DefaultProfileId { get; set; } = "balanced";
    /// <summary>Gaming display for all games (null = the main Windows display).</summary>
    public string? GamingDisplay { get; set; }
    public List<Profile> Profiles { get; set; } = new();
    public Dictionary<string, GamePrefs> Games { get; set; } = new();
    public List<ManualGame> ManualGames { get; set; } = new();
    public List<string> SharedCacheDirs { get; set; } = new();

    /// <summary>Version 1 stored hand-set steps here. Read for migration only.</summary>
    [JsonPropertyName("PerGame")] public Dictionary<string, QualityStep>? LegacyPerGame { get; set; }

    // ------------------------------------------------------------------ built-in profiles

    public static readonly IReadOnlyList<Profile> BuiltInProfiles = new[]
    {
        new Profile
        {
            Id = "balanced", Name = "Balanced", BuiltIn = true,
            Description = "Each game's recommended quality step, the display's native resolution, the engine's usual fullscreen mode, VSync off and a cap 3 fps under the active refresh rate.",
            Quality = new() { Mode = QualityMode.Offset, Offset = 0 }, Fps = new() { Mode = FpsMode.Auto },
            Resolution = new() { Mode = ResolutionMode.Native }, DisplayMode = DisplayModeChoice.Recommended, VSync = VSyncChoice.Off, Preserve = new(),
        },
        new Profile
        {
            Id = "quiet", Name = "Quiet", BuiltIn = true,
            Description = "One quality step below the recommendation and a 60 fps cap. A lower cap usually means less GPU work per second; how much cooler or quieter a PC runs depends on the game and the hardware.",
            Quality = new() { Mode = QualityMode.Offset, Offset = -1 }, Fps = new() { Mode = FpsMode.Custom, Value = 60 },
            Resolution = new() { Mode = ResolutionMode.Native }, DisplayMode = DisplayModeChoice.Recommended, VSync = VSyncChoice.Keep, Preserve = new(),
        },
        new Profile
        {
            Id = "competitive", Name = "Competitive", BuiltIn = true,
            Description = "Two quality steps below the recommendation, no frame cap where the game allows it, exclusive fullscreen and VSync off.",
            Quality = new() { Mode = QualityMode.Offset, Offset = -2 }, Fps = new() { Mode = FpsMode.Uncapped },
            Resolution = new() { Mode = ResolutionMode.Native }, DisplayMode = DisplayModeChoice.Fullscreen, VSync = VSyncChoice.Off, Preserve = new(),
        },
        new Profile
        {
            Id = "graphics-only", Name = "Graphics only", BuiltIn = true,
            Description = "Changes quality settings only. Resolution, display mode, VSync and the frame cap stay as the game has them.",
            Quality = new() { Mode = QualityMode.Offset, Offset = 0 }, Fps = new() { Mode = FpsMode.Keep },
            Resolution = new() { Mode = ResolutionMode.Keep }, DisplayMode = DisplayModeChoice.Keep, VSync = VSyncChoice.Keep, Preserve = new(),
        },
    };

    public IEnumerable<Profile> AllProfiles => BuiltInProfiles.Concat(Profiles);

    public Profile? FindProfile(string? id) =>
        id is null ? null : AllProfiles.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public Profile DefaultProfile => FindProfile(DefaultProfileId) ?? BuiltInProfiles[0];

    public GamePrefs Game(string id) => Games.TryGetValue(id, out var g) ? g : Games[id] = new GamePrefs();
    public GamePrefs? GameOrNull(string id) => Games.GetValueOrDefault(id);

    public static QualityStep Shift(QualityStep recommended, int offset) =>
        (QualityStep)Math.Clamp((int)recommended + offset, 0, QualitySteps.Count - 1);

    /// <summary>Quality step for a game (see the class summary for precedence).</summary>
    public QualityStep StepFor(string gameId, QualityStep recommended) => StepWithSource(gameId, recommended).step;

    public (QualityStep step, string source) StepWithSource(string gameId, QualityStep recommended)
    {
        var g = GameOrNull(gameId);
        if (g?.Step is { } s) return (s, "set by hand for this game");
        var q = g?.Quality ?? FindProfile(g?.ProfileId)?.Quality ?? DefaultProfile.Quality ?? new QualityPref();
        var from = g?.Quality is not null ? "this game's quality choice" : $"profile {ProfileFor(gameId).Name}";
        return q.Mode == QualityMode.Fixed
            ? (q.Step, $"fixed step from {from}")
            : (Shift(recommended, q.Offset + GlobalOffset), q.Offset == 0 && GlobalOffset == 0 ? "recommended" : $"recommendation {Signed(q.Offset + GlobalOffset)} step(s)");
    }

    static string Signed(int n) => n > 0 ? "+" + n : n.ToString();

    public Profile ProfileFor(string gameId) => FindProfile(GameOrNull(gameId)?.ProfileId) ?? DefaultProfile;

    /// <summary>One setting resolved through game, assigned profile, default profile, then Balanced.</summary>
    public T Pick<T>(string gameId, Func<SettingsPrefs, T?> get) where T : class =>
        new SettingsPrefs?[] { GameOrNull(gameId), FindProfile(GameOrNull(gameId)?.ProfileId), DefaultProfile, BuiltInProfiles[0] }
            .Where(x => x is not null).Select(x => get(x!)).FirstOrDefault(v => v is not null)!;

    public T PickValue<T>(string gameId, Func<SettingsPrefs, T?> get) where T : struct =>
        new SettingsPrefs?[] { GameOrNull(gameId), FindProfile(GameOrNull(gameId)?.ProfileId), DefaultProfile, BuiltInProfiles[0] }
            .Where(x => x is not null).Select(x => get(x!)).First(v => v.HasValue)!.Value;

    public static string OffsetLabel(int offset) => offset switch
    {
        0 => "Recommended for each game",
        < 0 => $"{-offset} step{(offset < -1 ? "s" : "")} faster than recommended",
        _ => $"{offset} step{(offset > 1 ? "s" : "")} nicer than recommended",
    };

    // ------------------------------------------------------------------ load, migrate, save

    public static Prefs Load()
    {
        try
        {
            if (File.Exists(AppPaths.PrefsFile))
                return Parse(File.ReadAllText(AppPaths.PrefsFile), out bool migrated, keepV1Copy: true);
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException) { }
        return new Prefs();
    }

    /// <summary>Reads any version. Version 1 files (hand-set steps in "PerGame") are migrated in memory.</summary>
    public static Prefs Parse(string json, out bool migrated, bool keepV1Copy = false)
    {
        var p = JsonSerializer.Deserialize<Prefs>(json, Json) ?? new Prefs();
        migrated = false;
        if (p.LegacyPerGame is { Count: > 0 } legacy)
        {
            foreach (var (id, step) in legacy) p.Game(id).Step ??= step;
            migrated = true;
        }
        if (p.Version < CurrentVersion)
        {
            migrated = true;
            if (keepV1Copy)
                try { File.Copy(AppPaths.PrefsFile, AppPaths.PrefsFile + $".v{Math.Max(1, p.Version)}.bak", overwrite: false); }
                catch (IOException) { }
        }
        p.LegacyPerGame = null;
        p.Version = CurrentVersion;
        p.GlobalOffset = Math.Clamp(p.GlobalOffset, -MaxOffset, MaxOffset);
        p.Profiles.RemoveAll(x => BuiltInProfiles.Any(b => b.Id.Equals(x.Id, StringComparison.OrdinalIgnoreCase)) || string.IsNullOrWhiteSpace(x.Id));
        foreach (var g in p.Games.Values) g.Excluded = g.Excluded.Distinct().ToList();
        return p;
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson()
    {
        foreach (var k in Games.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToList()) Games.Remove(k);
        return JsonSerializer.Serialize(this, Json);
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var tmp = AppPaths.PrefsFile + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, AppPaths.PrefsFile, overwrite: true);
    }

    /// <summary>Points the requirement lookup at the shared cache folders saved here.</summary>
    public void ApplySharedCaches()
    {
        foreach (var d in SharedCacheDirs)
            if (!Recommend.RequirementSource.SharedCacheDirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                Recommend.RequirementSource.SharedCacheDirs.Add(d);
    }

    // ------------------------------------------------------------------ profile management

    public static string NewId(string name) =>
        new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-') is { Length: > 0 } s ? s : "profile";

    public Profile CreateProfile(string name, Profile? from = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A profile needs a name.");
        var id = NewId(name);
        for (int i = 2; FindProfile(id) is not null; i++) id = NewId(name) + "-" + i;
        var p = (from ?? BuiltInProfiles[0]).Clone(id, name.Trim());
        // A copied description would go stale as soon as a setting changes; the settings list describes it.
        p.Description = from is null ? "" : $"Started as a copy of {from.Name}.";
        Profiles.Add(p);
        return p;
    }

    public void RenameProfile(string id, string name)
    {
        var p = Profiles.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException(FindProfile(id) is null ? $"No profile '{id}'." : "Built-in profiles cannot be renamed; duplicate one first.");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A profile needs a name.");
        p.Name = name.Trim();
    }

    /// <summary>Deletes a user profile. Games that used it go back to the default profile.</summary>
    public int DeleteProfile(string id)
    {
        var p = Profiles.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException(FindProfile(id) is null ? $"No profile '{id}'." : "Built-in profiles cannot be deleted.");
        Profiles.Remove(p);
        int n = 0;
        foreach (var g in Games.Values.Where(g => g.ProfileId == id)) { g.ProfileId = null; n++; }
        if (DefaultProfileId == id) DefaultProfileId = "balanced";
        return n;
    }
}
