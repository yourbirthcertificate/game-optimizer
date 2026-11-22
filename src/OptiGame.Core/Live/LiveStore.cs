using System.Text.Json;
using Microsoft.Win32;

namespace OptiGame.Core.Live;

/// <summary>
/// Local-only persistence for the live side (boost settings, what was learned, the user's own exe list):
/// live.json in the same data folder as the optimizer's preferences and backups
/// (%LOCALAPPDATA%\GameOpt, or the folder given with --data-dir). GameClear's old
/// %AppData%\GameClear\data.json is read once when no live.json exists yet.
/// </summary>
public sealed class LiveStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public const string RunValueName = "OptiGame";
    readonly string _file;
    public LiveData Data { get; private set; } = new();
    /// <summary>True when the data came from GameClear's old file on this start.</summary>
    public bool ImportedFromGameClear { get; private set; }

    public static string LegacyGameClearFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameClear", "data.json");

    /// <param name="dir">Data folder; null means <see cref="AppPaths.DataDir"/>.</param>
    /// <param name="legacyFile">GameClear's data file to import once; null skips the import (tests, temp folders).</param>
    public LiveStore(string? dir = null, string? legacyFile = null)
    {
        dir ??= AppPaths.DataDir;
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "live.json");
        try
        {
            if (File.Exists(_file)) Data = JsonSerializer.Deserialize<LiveData>(File.ReadAllText(_file)) ?? new LiveData();
            else if (legacyFile is not null && File.Exists(legacyFile))
            {
                Data = JsonSerializer.Deserialize<LiveData>(File.ReadAllText(legacyFile)) ?? new LiveData();
                ImportedFromGameClear = true;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { Data = new LiveData(); }
        Data.Prefs = new(Data.Prefs, StringComparer.OrdinalIgnoreCase);
        Data.Settings.PropertyChanged += (_, _) => Save();
        if (ImportedFromGameClear) Save();
    }

    public void Save()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* non-fatal */ }
    }

    public Pref PrefFor(string key, string name)
    {
        if (!Data.Prefs.TryGetValue(key, out var p)) Data.Prefs[key] = p = new Pref { Name = name };
        return p;
    }

    // ---- learning (threshold = 2) ----
    public const int LearnThreshold = 2;

    /// <summary>Applies learned/kept preferences on top of the built-in classification.</summary>
    public void ApplyLearning(AppGroup g)
    {
        g.Learned = false;
        if (!Data.Prefs.TryGetValue(g.Key, out var p)) return;
        if (p.Keep) { g.Tier = Tier.Keep; g.Reason = "You chose to keep this running"; g.Learned = p.Skipped >= LearnThreshold; return; }
        if (g.Tier == Tier.Suggested && p.Skipped >= LearnThreshold) { g.Tier = Tier.Keep; g.Reason = "Learned: you usually keep this open"; g.Learned = true; }
        else if (g.Tier == Tier.Review && p.Closed >= LearnThreshold && p.Skipped == 0) { g.Tier = Tier.Suggested; g.Reason = "Learned: you usually close this before gaming"; g.Learned = true; }
    }

    public void RecordChoices(IEnumerable<AppGroup> listed)
    {
        foreach (var g in listed)
        {
            var p = PrefFor(g.Key, g.Name);
            if (g.IsSelected) p.Closed++; else if (g.Tier == Tier.Suggested) p.Skipped++;
        }
        Save();
    }

    public void SetKeep(AppGroup g, bool keep)
    {
        var p = PrefFor(g.Key, g.Name);
        p.Keep = keep;
        if (!keep) { p.Skipped = 0; }
        Save();
    }

    public void ResetLearning() { Data.Prefs.Clear(); Save(); }

    // ---- start with Windows ----
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool GetStartWithWindows()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValueName) != null;
    }
    public static void SetStartWithWindows(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k == null) return;
        if (on) k.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue(RunValueName, false);
    }
}
