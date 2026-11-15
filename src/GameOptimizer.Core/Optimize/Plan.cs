using GameOptimizer.Core.Hardware;
using GameOptimizer.Core.Library;
using Microsoft.Win32;

namespace GameOptimizer.Core.Optimize;

/// <summary>One setting that will change: shown in the preview as "Setting: old -> new".</summary>
public sealed record SettingChange(string Setting, string? Old, string New)
{
    public override string ToString() => $"{Setting}: {Old ?? "(not set)"} -> {New}";
}

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
    public string Display => $"{Setting}: {OldValue ?? "(not set)"} -> {NewValue}";
}

public enum PlanStatus { Ready, NothingToChange, Skipped, Running }

public sealed class GamePlan
{
    public required GameInfo Game { get; init; }
    public required QualityStep Step { get; init; }
    public List<FileEdit> Files { get; } = new();
    public List<RegistryEdit> Registry { get; } = new();
    /// <summary>Parts that were left alone, with the reason ("uses its own settings menu", "file not found").</summary>
    public List<string> Skipped { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? RuleId { get; set; }
    /// <summary>True when some settings were found and read, even if none needed changing.</summary>
    internal bool Examined { get; set; }

    public bool HasChanges => Files.Any(f => f.Changes.Count > 0) || Registry.Count > 0;
    public int ChangeCount => Files.Sum(f => f.Changes.Count) + Registry.Count;

    public PlanStatus Status { get; set; }
    public string StatusText => Status switch
    {
        PlanStatus.Ready => $"{ChangeCount} change(s) ready",
        PlanStatus.NothingToChange => "Already set for this step",
        PlanStatus.Running => "Game is running. Close it first.",
        _ => Skipped.FirstOrDefault() ?? "Skipped",
    };
}
