using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using Microsoft.Win32;

namespace OptiGame.Core.Optimize;

/// <summary>
/// One setting that will change. Id is stable across runs (used for "keep my settings"), Locator says
/// where it lives in the file, and the raw values are what a key-level undo needs.
/// </summary>
public sealed record SettingChange(string Setting, string? Old, string New)
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = Categories.Other;
    /// <summary>"ini|Section|Key", "kv|key", "json|a.b", "gvas|Property".</summary>
    public string Locator { get; init; } = "";
    /// <summary>Value as written in the file before (null: the key was added).</summary>
    public string? OldRaw { get; init; }
    public string? NewRaw { get; init; }
    public override string ToString() => $"{Setting}: {Old ?? "(not set)"} -> {New}";
}

/// <summary>A change that was worked out but left out, and why.</summary>
public sealed record ExcludedChange(string Id, string Category, string Setting, string? Old, string New, string Why);

/// <summary>A settings file to rewrite. Updated holds the full new contents; nothing is written during planning.</summary>
public sealed class FileEdit
{
    public required string Path { get; init; }
    public required byte[] Original { get; init; }
    public required byte[] Updated { get; set; }
    public required string Format { get; init; }
    public List<SettingChange> Changes { get; } = new();
}

/// <summary>A registry value to set under HKEY_CURRENT_USER.</summary>
public sealed class RegistryEdit
{
    public required string SubKey { get; init; }
    public required string ValueName { get; init; }
    public required string Setting { get; init; }
    public required RegistryValueKind Kind { get; init; }
    public object? OldValue { get; init; }
    public required object NewValue { get; init; }
    public string Id { get; init; } = "";
    public string Category { get; init; } = Categories.Other;
    public string Display => $"{Setting}: {OldValue ?? "(not set)"} -> {NewValue}";
}

public enum PlanStatus { Ready, NothingToChange, Skipped, Running }

/// <summary>Why a game (or part of it) could not be optimized. Drives the explanation and the offered actions.</summary>
public enum SkipKind { None, NoHandler, NotCreatedYet, LocationNotFound, UnsupportedFormat, Ambiguous, Running }

public enum PlanAction { OpenSettingsFolder, AddRule, LaunchOnceAndRescan, CloseGame }

public sealed class GamePlan
{
    public required GameInfo Game { get; init; }
    public required QualityStep Step { get; init; }
    public OptimizeRequest? Request { get; init; }
    public List<FileEdit> Files { get; } = new();
    public List<RegistryEdit> Registry { get; } = new();
    /// <summary>Changes left out by "keep my settings", a profile, or a one-off untick in the preview.</summary>
    public List<ExcludedChange> Excluded { get; } = new();
    /// <summary>Parts that were left alone, with the reason ("uses its own settings menu", "file not found").</summary>
    public List<string> Skipped { get; } = new();
    /// <summary>Preferences this game's handler cannot apply (e.g. a frame cap for a Unity game).</summary>
    public List<string> NotControlled { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? RuleId { get; set; }
    public SkipKind SkipKind { get; set; }
    /// <summary>Folder that holds (or should hold) the game's settings, for "open settings folder".</summary>
    public string? SettingsFolder { get; set; }
    public List<PlanAction> Actions { get; } = new();
    /// <summary>True when some settings were found and read, even if none needed changing.</summary>
    internal bool Examined { get; set; }

    public bool HasChanges => Files.Any(f => f.Changes.Count > 0) || Registry.Count > 0;
    public int ChangeCount => Files.Sum(f => f.Changes.Count) + Registry.Count;
    public IEnumerable<(string id, string category, string text)> AllChanges =>
        Files.SelectMany(f => f.Changes.Select(c => (c.Id, c.Category, c.ToString())))
             .Concat(Registry.Select(r => (r.Id, r.Category, r.Display)));

    public PlanStatus Status { get; set; }
    public string StatusText => Status switch
    {
        PlanStatus.Ready => $"{ChangeCount} change(s) ready" + (Excluded.Count > 0 ? $", {Excluded.Count} kept" : ""),
        PlanStatus.NothingToChange => Excluded.Count > 0 ? $"Nothing to change ({Excluded.Count} kept by you)" : "Already set for this step",
        PlanStatus.Running => "Game is running. Close it first.",
        _ => SkipText,
    };

    /// <summary>The reasons and hints recorded for a skipped game, one per line (what was looked at, where its settings probably are).</summary>
    public string SkipDetail => string.Join(Environment.NewLine, Skipped.Distinct());

    public string SkipText => SkipKind switch
    {
        SkipKind.NoHandler => "No handler or rule for this game",
        SkipKind.NotCreatedYet => "Settings not created yet: start the game once, then rescan",
        SkipKind.LocationNotFound => "Settings location not found",
        SkipKind.UnsupportedFormat => "Settings format not supported",
        SkipKind.Ambiguous => "Settings are ambiguous; left alone",
        SkipKind.Running => "Game is running",
        _ => Skipped.FirstOrDefault() ?? "Skipped",
    };

    internal void Skip(SkipKind kind, string why, string? folder = null)
    {
        if (SkipKind == SkipKind.None) SkipKind = kind;
        Skipped.Add(why);
        SettingsFolder ??= folder;
    }
}
